using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Segmentation;

/// <summary>
/// State-of-the-art ONNX colorizer (DDColor — ECCV 2023). Same
/// degrade-gracefully pattern as <see cref="OnnxDenoiser"/>:
/// <see cref="IsAvailable"/> false when the model is missing and
/// <see cref="Colorize"/> returns null in that case.
///
/// DDColor's I/O contract is fundamentally different from DeOldify's:
/// the network only predicts the <b>Lab a/b chroma channels</b>, never
/// touches the source's <b>L (lightness) channel</b>. That means full-
/// resolution detail is preserved exactly — the colorisation pass is
/// chroma-only, blended into the source's luminance at native resolution.
/// Result: vastly better photographs of skin, foliage, sky, fabric.
///
/// Pipeline mirrors the upstream Python reference
/// (https://github.com/instant-high/DDColor-onnx/blob/main/ddcolorizer/ddcolor.py):
///   1. Source RGB → Lab → take the full-resolution L channel.
///   2. Resize the source to the model's fixed input size (typically
///      256×256 for paper-tiny, 512×512 for artistic), convert to Lab,
///      keep only L, build a "grey RGB" via Lab(L, 0, 0) → RGB.
///   3. Run inference. Output is a 2-channel ab tensor at the same
///      resolution.
///   4. Bilinear-resize the predicted ab back to the source's resolution.
///   5. Recombine source-L (full-res, untouched) + predicted-ab
///      (resized) → Lab → RGB.
///
/// <c>strength</c> linearly mixes between the source (0.0) and the
/// fully-colorised result (1.0).
/// </summary>
public sealed class OnnxColorizerDDColor : IDisposable {
  public const string DefaultModelFileName = "ddcolor-paper-tiny.onnx";

  /// <summary>Fallback input dimension when the model declares dynamic shapes (DDColor exports are typically fixed at 256 or 512).</summary>
  private const int FallbackInputSize = 256;

  private readonly Lazy<SessionInfo?> _session;

  public OnnxColorizerDDColor(FileInfo? modelFile = null) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._session = new Lazy<SessionInfo?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Mean absolute value of the model's predicted (a, b) chroma on the
  /// most recent <see cref="Colorize"/> call. Updated every successful
  /// run; useful for diagnosing "scenes look gray after recolour" —
  /// values below ~0.5 indicate the inference produced essentially
  /// no chroma, which usually points at a model / EP compatibility
  /// problem rather than a normal desaturated photograph.
  /// </summary>
  public static double LastInferenceMeanAbsAb { get; private set; }

  /// <summary>Input pixel statistics — captured per Colorize call so
  /// the UI can tell the user what DDColor was actually fed. Helps
  /// diagnose "model predicted near-zero chroma": if the input mean
  /// is at the extremes (R≈0 or R≈255) or the [min..max] span is
  /// tiny, the image is out of DDColor's training distribution and
  /// the low chroma is expected, not a model / pipeline bug.</summary>
  public static string LastInputStats { get; private set; } = "(none)";

  /// <summary>
  /// Returns a freshly allocated colourised copy of <paramref name="source"/>
  /// at the source's original resolution. Returns null when no model is
  /// available so the caller can fall back to a clone / no-op.
  /// </summary>
  /// <param name="source">Input image. Not mutated.</param>
  /// <param name="strength">Blend between source (0.0) and the fully
  ///   colorised result (1.0). Outside that range is clamped.</param>
  /// <param name="ct">Cooperatively cancels before / after inference.</param>
  public Image<Rgba32>? Colorize(Image<Rgba32> source, double strength = 1.0, double chromaBoost = 1.6, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    var info = this._session.Value;
    if (info == null)
      return null;

    var blend = (float)Math.Clamp(strength, 0.0, 1.0);
    var boost = (float)Math.Clamp(chromaBoost, 0.0, 5.0);
    if (blend < 1e-6)
      return source.Clone();

    ct.ThrowIfCancellationRequested();

    var srcW = source.Width;
    var srcH = source.Height;
    var inputSize = info.InputSize;

    // Diagnostic: input pixel statistics. DDColor was trained on
    // mid-range B&W photos (mean R/G/B around 100–160, full [0..255]
    // span). Inputs at the extremes (everything dark, everything
    // bright, or very narrow span) push the model out of distribution
    // and cause it to predict near-zero chroma — not a bug, just an
    // expected limitation. Surfacing the numbers tells the user
    // whether their image is unusual.
    {
      long sumR = 0, sumG = 0, sumB = 0;
      byte minR = 255, maxR = 0;
      var pcount = 0L;
      source.ProcessPixelRows(a => {
        for (var y = 0; y < a.Height; y++) {
          var row = a.GetRowSpan(y);
          for (var x = 0; x < row.Length; x++) {
            var p = row[x];
            sumR += p.R; sumG += p.G; sumB += p.B;
            if (p.R < minR) minR = p.R;
            if (p.R > maxR) maxR = p.R;
            pcount++;
          }
        }
      });
      LastInputStats = pcount == 0
        ? "(empty image)"
        : $"{srcW}x{srcH} mean=R{sumR / pcount}/G{sumG / pcount}/B{sumB / pcount} R-span[{minR}..{maxR}]";
    }

    // Step 1: source RGB → full-res Lab L channel. Stays untouched all
    // the way to the final recombine — that's why DDColor preserves
    // every bit of the source's spatial detail.
    var sourceL = ExtractLChannel(source);

    // Step 2: build the network input. Resize source, take its L,
    // synthesise grey-RGB by passing Lab(L, 0, 0) back through Lab→RGB.
    var inputTensor = BuildNetworkInput(source, inputSize);

    ct.ThrowIfCancellationRequested();

    // Step 3: run inference. Output shape is [1, 2, inputSize, inputSize]
    // — channel 0 = a, channel 1 = b. Inference failures (OpenVINO
    // device contention, malformed model, OOM) bubble up so the UI
    // can show why the recolour produced no output instead of
    // silently returning a grayscale image.
    float[] abOutput;
    int abH, abW;
    try {
      var input = new DenseTensor<float>(inputTensor, new[] { 1, 3, inputSize, inputSize });
      using var results = info.Session.Run(new[] { NamedOnnxValue.CreateFromTensor(info.InputName, input) });
      var first = results.First();
      var dims = first.AsTensor<float>().Dimensions;
      abH = dims.Length >= 4 ? dims[2] : inputSize;
      abW = dims.Length >= 4 ? dims[3] : inputSize;
      abOutput = first.AsTensor<float>().ToArray();
    } catch (Exception ex) {
      throw new InvalidOperationException(
        $"DDColor inference failed on {srcW}×{srcH} input ({OnnxAcceleration.LastSelectedDevice}): {ex.Message}", ex);
    }

    // Compute the model's chroma magnitude as a diagnostic. We DON'T
    // throw on low values any more — letting the pipeline complete
    // means the user gets to see the (possibly muted) result instead
    // of an error wall, and the value flows back to the UI for
    // display in the status bar. Useful range to expect on real
    // photos: 3–15. Tests on synthetic input have measured up to ~34.
    // Values below ~0.5 indicate the model effectively returned a
    // no-op, but we still proceed to compose so the user can see
    // what's happening rather than getting "nothing rendered".
    var sumAbsAb = 0.0;
    for (var i = 0; i < abOutput.Length; i++)
      sumAbsAb += Math.Abs(abOutput[i]);
    LastInferenceMeanAbsAb = sumAbsAb / abOutput.Length;

    ct.ThrowIfCancellationRequested();

    // Step 4: bilinear-upsample the predicted ab field to source size.
    var aFull = ResampleToSource(abOutput, abW, abH, srcW, srcH, channelOffset: 0, channelStride: abW * abH);
    var bFull = ResampleToSource(abOutput, abW, abH, srcW, srcH, channelOffset: 1, channelStride: abW * abH);

    // Step 5: combine source.L + predicted ab → Lab → RGB. Apply blend
    // weight against the source's RGB at the same time so strength=0
    // returns the source unchanged.
    return ComposeLabResult(source, sourceL, aFull, bFull, blend, boost);
  }

  /// <summary>Extract the source's full-resolution L (lightness) channel into a flat float array in [0..100].</summary>
  private static float[] ExtractLChannel(Image<Rgba32> source) {
    var w = source.Width;
    var h = source.Height;
    var l = new float[w * h];
    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var px = row[x];
          var (L, _, _) = RgbToLab(px.R / 255f, px.G / 255f, px.B / 255f);
          l[y * w + x] = L;
        }
      }
    });
    return l;
  }

  /// <summary>
  /// Build a [1, 3, N, N] grey-RGB tensor in [0..1]. The synthesis path
  /// (resized RGB → Lab → keep L → Lab(L, 0, 0) → RGB) matches the
  /// upstream Python pipeline byte-for-byte; a naive grayscale
  /// (R=G=B=luma) does NOT — the model is sensitive to the way Lab→RGB
  /// distributes the L back across the three channels.
  /// </summary>
  private static float[] BuildNetworkInput(Image<Rgba32> source, int size) {
    using var resized = source.Clone(c => c.Resize(size, size));
    var pixelCount = size * size;
    var tensor = new float[3 * pixelCount];
    resized.ProcessPixelRows(accessor => {
      for (var y = 0; y < size; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < size; x++) {
          var px = row[x];
          // Resized RGB → Lab → L only.
          var (L, _, _) = RgbToLab(px.R / 255f, px.G / 255f, px.B / 255f);
          // Lab(L, 0, 0) → RGB. Synthetic neutral-grey at the same lightness.
          var (gr, gg, gb) = LabToRgb(L, 0f, 0f);
          var off = y * size + x;
          tensor[0 * pixelCount + off] = gr;
          tensor[1 * pixelCount + off] = gg;
          tensor[2 * pixelCount + off] = gb;
        }
      }
    });
    return tensor;
  }

  /// <summary>
  /// Bilinear-resample a single channel of the [1, 2, abH, abW] output
  /// tensor up to the source's resolution. Chroma upsampling is the
  /// human eye's blind spot — bilinear is plenty (chroma subsampling
  /// is what JPEG 4:2:0 has done forever).
  /// </summary>
  private static float[] ResampleToSource(float[] ab, int abW, int abH, int dstW, int dstH, int channelOffset, int channelStride) {
    var dst = new float[dstW * dstH];
    var sx = (double)abW / dstW;
    var sy = (double)abH / dstH;
    var srcChannelStart = channelOffset * channelStride;
    for (var y = 0; y < dstH; y++) {
      var fy = y * sy;
      var y0 = (int)Math.Floor(fy);
      var y1 = Math.Min(y0 + 1, abH - 1);
      var dy = fy - y0;
      for (var x = 0; x < dstW; x++) {
        var fx = x * sx;
        var x0 = (int)Math.Floor(fx);
        var x1 = Math.Min(x0 + 1, abW - 1);
        var dxv = fx - x0;
        var v00 = ab[srcChannelStart + y0 * abW + x0];
        var v01 = ab[srcChannelStart + y0 * abW + x1];
        var v10 = ab[srcChannelStart + y1 * abW + x0];
        var v11 = ab[srcChannelStart + y1 * abW + x1];
        var top = v00 * (1 - dxv) + v01 * dxv;
        var bot = v10 * (1 - dxv) + v11 * dxv;
        dst[y * dstW + x] = (float)(top * (1 - dy) + bot * dy);
      }
    }
    return dst;
  }

  /// <summary>
  /// Recombine source-L + predicted-ab → Lab → RGB, blended against the
  /// original source by <paramref name="strength"/>. strength=1 returns
  /// the colorised pixel; strength=0 returns the source.
  /// </summary>
  private static Image<Rgba32> ComposeLabResult(Image<Rgba32> source, float[] sourceL, float[] a, float[] b, float strength, float chromaBoost) {
    var w = source.Width;
    var h = source.Height;
    var output = new Image<Rgba32>(w, h);
    output.ProcessPixelRows(source, (outAcc, srcAcc) => {
      for (var y = 0; y < h; y++) {
        var dst = outAcc.GetRowSpan(y);
        var src = srcAcc.GetRowSpan(y);
        for (var x = 0; x < w; x++) {
          var off = y * w + x;
          var (cr, cg, cb) = LabToRgb(sourceL[off], a[off] * chromaBoost, b[off] * chromaBoost);
          var r = src[x].R / 255f;
          var g = src[x].G / 255f;
          var bl = src[x].B / 255f;
          var oR = r + (cr - r) * strength;
          var oG = g + (cg - g) * strength;
          var oB = bl + (cb - bl) * strength;
          dst[x] = new Rgba32(ToByte(oR), ToByte(oG), ToByte(oB), src[x].A);
        }
      }
    });
    return output;
  }


  // ---------- sRGB ↔ Lab ----------
  // Standard CIE Lab with D65 illuminant. Matches OpenCV's float32
  // BGR2Lab / LAB2BGR exactly (the convention the network was trained
  // against): L in [0..100], a/b in approximately [-128..128].

  private static readonly float[] _D65 = { 0.95047f, 1.00000f, 1.08883f };
  private const float Delta = 6f / 29f;
  private const float Delta2 = Delta * Delta;
  private const float Delta3 = Delta * Delta * Delta;

  private static (float L, float a, float b) RgbToLab(float r, float g, float b) {
    var rl = SrgbToLinear(r);
    var gl = SrgbToLinear(g);
    var bl = SrgbToLinear(b);
    var X = 0.4124564f * rl + 0.3575761f * gl + 0.1804375f * bl;
    var Y = 0.2126729f * rl + 0.7151522f * gl + 0.0721750f * bl;
    var Z = 0.0193339f * rl + 0.1191920f * gl + 0.9503041f * bl;
    var fx = LabF(X / _D65[0]);
    var fy = LabF(Y / _D65[1]);
    var fz = LabF(Z / _D65[2]);
    return (116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
  }

  private static (float r, float g, float b) LabToRgb(float L, float a, float b) {
    var fy = (L + 16f) / 116f;
    var fx = a / 500f + fy;
    var fz = fy - b / 200f;
    var X = LabFInv(fx) * _D65[0];
    var Y = LabFInv(fy) * _D65[1];
    var Z = LabFInv(fz) * _D65[2];
    var rl =  3.2404542f * X - 1.5371385f * Y - 0.4985314f * Z;
    var gl = -0.9692660f * X + 1.8760108f * Y + 0.0415560f * Z;
    var bl =  0.0556434f * X - 0.2040259f * Y + 1.0572252f * Z;
    return (LinearToSrgb(rl), LinearToSrgb(gl), LinearToSrgb(bl));
  }

  private static float SrgbToLinear(float c)
    => c <= 0.04045f ? c / 12.92f : (float)Math.Pow((c + 0.055f) / 1.055f, 2.4f);

  private static float LinearToSrgb(float c)
    => c <= 0.0031308f ? 12.92f * c : 1.055f * (float)Math.Pow(c, 1f / 2.4f) - 0.055f;

  private static float LabF(float t)
    => t > Delta3 ? (float)Math.Pow(t, 1.0 / 3.0) : t / (3f * Delta2) + 4f / 29f;

  private static float LabFInv(float t)
    => t > Delta ? t * t * t : 3f * Delta2 * (t - 4f / 29f);

  private static byte ToByte(float v) {
    if (float.IsNaN(v) || v <= 0f) return 0;
    if (v >= 1f) return 255;
    return (byte)Math.Round(v * 255f);
  }

  // ---------- Session ----------

  private static SessionInfo? TryOpenSession(FileInfo modelFile) {
    // Missing model file = graceful no-op. Any other failure surfaces
    // as an exception so the recolour stage doesn't silently degrade
    // to grayscale output.
    if (!modelFile.Exists)
      return null;
    try {
      // Cached session: when the user drags the colour slider, the UI
      // spawns multiple Task.Run pipelines in rapid succession. Each
      // would otherwise call `new InferenceSession(modelFile.FullName)`
      // and load the 258 MB DDColor model independently — concurrent
      // same-model session construction in ORT can race in ways the
      // model's output disagrees with itself between calls (one task
      // sees correct chroma, another sees near-zero). The shared
      // cached session removes the race: ORT's
      // <c>InferenceSession.Run</c> is documented thread-safe, so
      // multiple concurrent inferences against ONE session are well-
      // defined. preferCpu=true matches the original direct-construction
      // EP behavior on the Intel ORT build (default options = CPU EP).
      var session = OnnxAcceleration.CreateSession(modelFile.FullName, preferCpu: true);
      var inputName = session.InputMetadata.Keys.First();
      var inputDims = session.InputMetadata[inputName].Dimensions;
      // DDColor exports are fixed-shape (256 for paper-tiny, 512 for artistic / modelscope).
      var size = inputDims.Length >= 4 && inputDims[2] > 0 ? inputDims[2] : FallbackInputSize;
      return new SessionInfo(session, inputName, size);
    } catch (Exception ex) {
      throw new InvalidOperationException(
        $"DDColor session creation failed for {modelFile.Name}: {ex.Message}", ex);
    }
  }

  public void Dispose() {
    // Sessions live in the OnnxAcceleration cache for the process's
    // lifetime — disposing here would invalidate the cache and force
    // the next caller to re-load 258 MB of weights. Cache teardown
    // happens via OnnxAcceleration.ResetCache() (called from tests).
  }

  private sealed record SessionInfo(InferenceSession Session, string InputName, int InputSize);
}
