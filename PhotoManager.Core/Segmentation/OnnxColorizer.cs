using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Segmentation;

/// <summary>
/// ONNX-backed AI colorizer. The canonical model is DeOldify
/// (Artistic / Stable variants exported to ONNX). Same degrade-gracefully
/// pattern as <see cref="OnnxDenoiser"/>: <see cref="IsAvailable"/> is
/// false when the model is missing and <see cref="Colorize"/> returns
/// null in that case so callers can no-op without exceptions.
///
/// DeOldify takes a 3-channel "grayscale-as-RGB" input — the source's
/// luminance replicated across R/G/B — and outputs a 3-channel RGB
/// colour image. Most DeOldify ONNX exports are fixed-shape (256×256 or
/// 288×288); we resize the source down to the model's input dimensions
/// before inference and bilinear-upsample the colour result back to the
/// source's resolution. This loses spatial detail past the input size,
/// but matches how the upstream Python tool works.
///
/// <c>strength</c> linearly mixes between the source's grayscale (0.0)
/// and the fully-colourised output (1.0) so the user can dial intensity
/// without re-running inference.
/// </summary>
public sealed class OnnxColorizer : IDisposable {
  // Must match what's embedded in the upstream .onnx graph's external-data
  // reference — ONNX Runtime looks for the .onnx.data sibling by that exact
  // name. Don't rename either file on disk.
  public const string DefaultModelFileName = "deoldify-artistic.onnx";

  /// <summary>
  /// Fallback input dimension when the model declares dynamic shapes.
  /// 512 gives gentler chroma than 256 (less aggressive saturation) and
  /// halves the upsampling factor when going back to source size, so
  /// gradient banding from bicubic upsample is much less visible.
  /// </summary>
  private const int FallbackInputSize = 512;

  // DeOldify uses ImageNet-normalised float inputs and produces ImageNet-
  // normalised float outputs — feeding raw [0..1] grayscale and treating
  // the result as RGB returns garbage. These are the published torchvision
  // ImageNet stats the upstream training pipeline uses (in RGB order).
  private static readonly float[] ImageNetMean = { 0.485f, 0.456f, 0.406f };
  private static readonly float[] ImageNetStd  = { 0.229f, 0.224f, 0.225f };

  // DeOldify is trained with RGB tensors at both ends (fast.ai pipeline).
  // Per-region probing confirmed: in skies the model emits B-dominant
  // pixels (correct), in lions it emits R-dominant pixels (correct), but
  // on tight face crops *without* contextual scene cues the model often
  // produces a slight neutral-blue cast — that's DeOldify's known "I'm
  // unsure what this is, default to slight blue" behaviour, not a
  // channel-mapping bug. So we keep RGB ordering on both ends.
  private const int RChannel = 0;
  private const int GChannel = 1;
  private const int BChannel = 2;

  private readonly Lazy<SessionInfo?> _session;

  public OnnxColorizer(FileInfo? modelFile = null) {
    MigrateLegacyColorizeFiles();
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._session = new Lazy<SessionInfo?>(() => TryOpenSession(path));
  }

  /// <summary>
  /// Earlier builds saved DeOldify under "colorize-deoldify-*.onnx" / .onnx.data,
  /// but ONNX Runtime can't load that — the graph hard-codes the upstream
  /// "deoldify-*.onnx.data" filename for its external weights and refuses
  /// to find them under any other name. This is a one-shot rename so users
  /// who already downloaded the 244 MB / 833 MB don't have to re-download.
  /// Idempotent: only acts if the legacy name exists and the canonical
  /// name doesn't.
  /// </summary>
  private static void MigrateLegacyColorizeFiles() {
    string[][] pairs = [
      ["colorize-deoldify-artistic.onnx",      "deoldify-artistic.onnx"],
      ["colorize-deoldify-artistic.onnx.data", "deoldify-artistic.onnx.data"],
      ["colorize-deoldify-stable.onnx",        "deoldify-stable.onnx"],
      ["colorize-deoldify-stable.onnx.data",   "deoldify-stable.onnx.data"]
    ];
    foreach (var pair in pairs) {
      try {
        var legacy = AppDataPaths.ModelFile(pair[0]);
        var canonical = AppDataPaths.ModelFile(pair[1]);
        if (legacy.Exists && !canonical.Exists)
          File.Move(legacy.FullName, canonical.FullName);
      } catch {
        // Best-effort — if rename fails the user can manually re-download.
      }
    }
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Returns a freshly allocated colourised copy of <paramref name="source"/>
  /// at the source's original resolution. Returns null when no model is
  /// available so the caller can fall back to a clone / no-op without try/catch.
  /// </summary>
  /// <param name="source">Input image. Not mutated.</param>
  /// <param name="strength">Blend between source-grayscale (0.0) and
  ///   fully-colourised (1.0). Outside that range gets clamped.</param>
  /// <param name="ct">Cooperatively cancels before / after inference so a
  ///   live-preview re-render doesn't have to wait for the previous pass.</param>
  public Image<Rgba32>? Colorize(Image<Rgba32> source, double strength = 1.0, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    var info = this._session.Value;
    if (info == null)
      return null;

    var blend = (float)Math.Clamp(strength, 0.0, 1.0);
    if (blend < 1e-6)
      return source.Clone();

    ct.ThrowIfCancellationRequested();

    // Step 1: downscale to the model's input size and convert to a
    // 3-channel grayscale-as-RGB input tensor. DeOldify's network
    // implicitly treats the 3 channels as R = G = B = luminance.
    var inputW = info.InputW;
    var inputH = info.InputH;
    using var resized = source.Clone(c => c.Resize(inputW, inputH));
    var inputTensor = BuildGrayscaleInput(resized, inputW, inputH);

    ct.ThrowIfCancellationRequested();

    // Step 2: run inference. Output shape mirrors input on most DeOldify
    // exports (1×3×inputH×inputW), but we read the dimensions from the
    // result so non-square / different-sized output models still work.
    float[] outputTensor;
    int outW, outH;
    try {
      var input = new DenseTensor<float>(inputTensor, new[] { 1, 3, inputH, inputW });
      using var results = info.Session.Run(new[] { NamedOnnxValue.CreateFromTensor(info.InputName, input) });
      var first = results.First();
      var dims = first.AsTensor<float>().Dimensions;
      outH = dims.Length >= 4 ? dims[2] : inputH;
      outW = dims.Length >= 4 ? dims[3] : inputW;
      outputTensor = first.AsTensor<float>().ToArray();
    } catch (Exception ex) {
      throw new InvalidOperationException(
        $"DeOldify inference failed on {source.Width}×{source.Height} input ({OnnxAcceleration.LastSelectedDevice}): {ex.Message}", ex);
    }

    ct.ThrowIfCancellationRequested();

    // Step 3: convert the network's color output back to an Rgba32 image
    // at the model's output dimensions, then upsample to the source size
    // so the caller can drop the result straight back into the develop
    // pipeline at full resolution.
    using var colorAtModelSize = TensorToImage(outputTensor, outW, outH);
    using var colorAtSourceSize = colorAtModelSize.Clone(c => c.Resize(source.Width, source.Height));

    // Step 4: blend the per-pixel chroma from the colourised output onto
    // the source's luminance. This lets `strength` linearly interpolate
    // between the original grayscale and the colourised version while
    // preserving fine source detail (the model only sees a downscaled
    // copy, but the user wants to keep their full-res sharpness).
    return BlendChromaOntoLuminance(source, colorAtSourceSize, blend);
  }

  /// <summary>
  /// Pack a luminance-only ImageNet-normalised RGB tensor: shape
  /// [1, 3, H, W] where channel 0 = R-normalised, 1 = G, 2 = B.
  /// fast.ai (DeOldify's training framework) keeps tensors in RGB at
  /// the input boundary even though numpy/OpenCV use BGR — only the
  /// final visualisation step in the original Python pipeline swaps
  /// to BGR for display. Probing both conventions confirmed: RGB
  /// input gives DeOldify's classic mild-chroma output, BGR input
  /// makes the network blow R past 1.0 on every pixel.
  /// </summary>
  private static float[] BuildGrayscaleInput(Image<Rgba32> source, int w, int h) {
    var pixelCount = h * w;
    var tensor = new float[3 * pixelCount];
    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < w; x++) {
          var px = row[x];
          // Rec. 709 luma coefficients — same convention DeOldify trained on.
          var luma = (0.2126f * px.R + 0.7152f * px.G + 0.0722f * px.B) / 255f;
          var offset = y * w + x;
          tensor[0 * pixelCount + offset] = (luma - ImageNetMean[0]) / ImageNetStd[0];
          tensor[1 * pixelCount + offset] = (luma - ImageNetMean[1]) / ImageNetStd[1];
          tensor[2 * pixelCount + offset] = (luma - ImageNetMean[2]) / ImageNetStd[2];
        }
      }
    });
    return tensor;
  }

  /// <summary>
  /// Decode a DeOldify output tensor (ImageNet-normalised BGR) back to
  /// a regular [0..1]-clamped <see cref="Rgba32"/> image. Channel 0 of
  /// the tensor carries B, channel 2 carries R; we swap on the way out
  /// so the final pixel is in RGB order. Each channel uses the matching
  /// ImageNet stat (the means/stds arrays are RGB-ordered, so the cross-
  /// index is the same one used in <see cref="BuildGrayscaleInput"/>).
  /// </summary>
  private static Image<Rgba32> TensorToImage(float[] tensor, int w, int h) {
    var pixelCount = h * w;
    var image = new Image<Rgba32>(w, h);
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < w; x++) {
          var offset = y * w + x;
          var r = Math.Clamp(tensor[RChannel * pixelCount + offset] * ImageNetStd[0] + ImageNetMean[0], 0f, 1f);
          var g = Math.Clamp(tensor[GChannel * pixelCount + offset] * ImageNetStd[1] + ImageNetMean[1], 0f, 1f);
          var b = Math.Clamp(tensor[BChannel * pixelCount + offset] * ImageNetStd[2] + ImageNetMean[2], 0f, 1f);
          row[x] = new Rgba32(ToByte(r), ToByte(g), ToByte(b), (byte)255);
        }
      }
    });
    return image;
  }

  /// <summary>
  /// 2× amplification: enough to make the natural DeOldify chroma
  /// perceptible (sky/grass go appreciably blue/green), low enough that
  /// the model's mild "I'm-uncertain-so-default-blue" bias on close
  /// crops doesn't blow up into an obvious blue cast everywhere. Higher
  /// values (3×–4×) exaggerate that weakness on portraits / animal
  /// closeups; the soft-clip in the blend keeps colours in-gamut but
  /// the resulting "everything is blueish" look isn't what the user
  /// wants. The strength slider (0..1) scales on top, so the effective
  /// range is 0..2×.
  /// </summary>
  private const float ChromaAmplification = 2.0f;

  /// <summary>
  /// Transfer the chroma of <paramref name="color"/> onto the luminance
  /// of <paramref name="source"/> via YCbCr. The source's Y channel is
  /// preserved exactly (full-resolution detail), the colourised image's
  /// Cb/Cr are scaled by <paramref name="strength"/> × <see cref="ChromaAmplification"/>
  /// and then composed into RGB. A soft-clip (tanh-shaped) on the
  /// resulting RGB prevents any pixel from clipping hard at 0 / 255 — so
  /// the gradient banding that hard clipping creates simply doesn't form.
  /// </summary>
  private static Image<Rgba32> BlendChromaOntoLuminance(Image<Rgba32> source, Image<Rgba32> color, float strength) {
    var w = source.Width;
    var h = source.Height;
    var output = new Image<Rgba32>(w, h);
    var colorPixels = new Rgba32[w * h];
    color.CopyPixelDataTo(colorPixels);

    var weight = strength * ChromaAmplification;

    output.ProcessPixelRows(source, (outAcc, srcAcc) => {
      for (var y = 0; y < h; y++) {
        var dst = outAcc.GetRowSpan(y);
        var src = srcAcc.GetRowSpan(y);
        for (var x = 0; x < w; x++) {
          var sR = src[x].R / 255f;
          var sG = src[x].G / 255f;
          var sB = src[x].B / 255f;

          // Source luminance (BT.709) — anchored, never modified, so the
          // user keeps every bit of source-resolution detail.
          var sY = 0.2126f * sR + 0.7152f * sG + 0.0722f * sB;

          // Colourised pixel + its luminance and YCbCr chroma channels.
          // Cb/Cr formulas are the BT.709 inverse of the Y mix above.
          var c = colorPixels[y * w + x];
          var cR = c.R / 255f;
          var cG = c.G / 255f;
          var cB = c.B / 255f;
          var cY = 0.2126f * cR + 0.7152f * cG + 0.0722f * cB;
          var cCb = (cB - cY) / 1.8556f;
          var cCr = (cR - cY) / 1.5748f;

          // Scale the colorized chroma by strength × amplification.
          // YCbCr is the perceptually-friendly space — scaling Cb/Cr
          // affects saturation without changing perceived brightness,
          // so we don't get the "blue cast pulls everything dim" issue
          // that the previous straight-RGB scaling caused.
          var outCb = cCb * weight;
          var outCr = cCr * weight;

          // YCbCr → RGB with source luminance.
          var rOut = sY + 1.5748f * outCr;
          var gOut = sY - 0.1873f * outCb - 0.4681f * outCr;
          var bOut = sY + 1.8556f * outCb;

          // Soft-clip near the gamut boundaries: tanh-style compression
          // that maps [-∞, +∞] smoothly into [0, 1] so even pixels with
          // amplified chroma stay in-gamut without a hard knee. Only
          // engages once a channel actually approaches the boundary.
          dst[x] = new Rgba32(SoftClip(rOut), SoftClip(gOut), SoftClip(bOut), src[x].A);
        }
      }
    });
    return output;
  }

  /// <summary>
  /// Smooth gamut compression: identical to <c>v</c> in [0.05, 0.95],
  /// gently rolls off to 0 / 1 as the input crosses those thresholds.
  /// Removes the hard clipping that creates visible stripes when the
  /// amplified chroma pushes a channel past 0 or 255.
  /// </summary>
  private static byte SoftClip(float v) {
    if (float.IsNaN(v))
      return 0;
    // Identity zone for the bulk of the range, tanh-shaped knees at the
    // ends. The 0.05 margin keeps the soft-clip invisible on well-exposed
    // pixels.
    const float kneeLow = 0.05f;
    const float kneeHigh = 0.95f;
    if (v >= kneeLow && v <= kneeHigh)
      return (byte)Math.Round(v * 255f);
    if (v < kneeLow) {
      // Map (-∞, kneeLow] smoothly into [0, kneeLow].
      var t = (float)Math.Tanh((v - kneeLow) / kneeLow);   // t in (-1, 0]
      return (byte)Math.Round((kneeLow + kneeLow * t) * 255f);
    }
    {
      // Map [kneeHigh, +∞) smoothly into [kneeHigh, 1].
      var headroom = 1 - kneeHigh;
      var t = (float)Math.Tanh((v - kneeHigh) / headroom);  // t in [0, 1)
      return (byte)Math.Round((kneeHigh + headroom * t) * 255f);
    }
  }

  private static byte ToByte(float v) {
    if (float.IsNaN(v) || v <= 0f)
      return 0;
    if (v >= 1f)
      return 255;
    return (byte)Math.Round(v * 255f);
  }

  private static SessionInfo? TryOpenSession(FileInfo modelFile) {
    // Missing model = graceful no-op. Other failures throw so the
    // recolour stage doesn't silently degrade to grayscale output.
    if (!modelFile.Exists)
      return null;
    try {
      // Cached session — see OnnxColorizerDDColor.TryOpenSession. UI
      // slider drags fire multiple concurrent Colorize calls; they
      // share one cached session instead of each loading a fresh
      // copy of the model.
      var session = OnnxAcceleration.CreateSession(modelFile.FullName, preferCpu: true);
      var inputName = session.InputMetadata.Keys.First();
      var inputDims = session.InputMetadata[inputName].Dimensions;
      var fixedH = inputDims.Length >= 4 && inputDims[2] > 0 ? inputDims[2] : FallbackInputSize;
      var fixedW = inputDims.Length >= 4 && inputDims[3] > 0 ? inputDims[3] : FallbackInputSize;
      return new SessionInfo(session, inputName, fixedW, fixedH);
    } catch (Exception ex) {
      throw new InvalidOperationException(
        $"DeOldify session creation failed for {modelFile.Name}: {ex.Message}", ex);
    }
  }

  public void Dispose() {
    // Cached in OnnxAcceleration; do not dispose here.
  }

  private sealed record SessionInfo(InferenceSession Session, string InputName, int InputW, int InputH);
}
