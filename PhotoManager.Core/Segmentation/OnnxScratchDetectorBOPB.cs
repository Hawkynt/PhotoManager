using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Segmentation;

/// <summary>
/// ONNX-backed scratch detector — Microsoft's UNet from
/// "Bringing Old Photos Back to Life" (Wan et al. 2020), trained
/// specifically to find scratches, dust, paper-fold tears, mould, and
/// edge damage in old photo scans. Same degrade-gracefully pattern as
/// the rest of the ONNX engines: <see cref="IsAvailable"/> false when
/// the model is missing and <see cref="Detect"/> returns null in that
/// case so the caller can fall back to the classical Frangi detector
/// without exceptions.
///
/// Network shape:
///   Input  [1, 1, H, W] — single-channel grayscale, normalised to
///                          [-1, +1] (network was trained on
///                          torchvision Normalize(mean=0.5, std=0.5)).
///   Output [1, 1, H, W] — logits (NOT probabilities). Apply sigmoid
///                          to get [0..1] per-pixel probability of
///                          "scratch / damage at this pixel".
///
/// The exported ONNX rounds H and W up to multiples of 16 (UNet has
/// 4 levels of 2× downsampling). We pad on input + crop on output so
/// the user-visible mask exactly matches the source's dimensions.
/// </summary>
public sealed class OnnxScratchDetectorBOPB : IDisposable {
  public const string DefaultModelFileName = "scratch-detector-bopb.onnx";

  /// <summary>Sigmoid threshold: pixel becomes part of the mask when prob ≥ this.</summary>
  private const float DefaultProbabilityThreshold = 0.4f;

  /// <summary>UNet 4-level downsampling: input dimensions are padded to multiples of 16.</summary>
  private const int DimensionMultiple = 16;

  private readonly Lazy<SessionInfo?> _session;

  public OnnxScratchDetectorBOPB(FileInfo? modelFile = null) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._session = new Lazy<SessionInfo?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Run the BOPB scratch detector against <paramref name="source"/>
  /// and return an Rgba32 mask (red+200α where scratch detected,
  /// transparent elsewhere) at the source's resolution. Same mask
  /// convention the brush canvas + LaMa inpaint pipeline expect, so
  /// the output is a drop-in replacement for the classical
  /// <see cref="PhotoManager.Core.Develop.ScratchDetector.Detect"/>.
  /// Returns null when the model isn't installed.
  /// </summary>
  /// <param name="source">Image to analyse. Not mutated.</param>
  /// <param name="threshold">Probability threshold ∈ [0..1]. Pixels
  ///   whose sigmoid(logit) ≥ this end up in the mask. Lower =
  ///   more thorough (catches faint damage at the cost of false
  ///   positives); higher = more conservative.</param>
  /// <param name="ct">Cooperative cancellation around the inference call.</param>
  public Image<Rgba32>? Detect(Image<Rgba32> source, double threshold = DefaultProbabilityThreshold, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    var info = this._session.Value;
    if (info == null)
      return null;

    var srcW = source.Width;
    var srcH = source.Height;

    // Pad dimensions up to the next multiple of 16 — the exported
    // UNet's output dims are rounded that way. Padding with the source's
    // edge pixels keeps Hessian-edge artifacts away from the boundary
    // (vs zero-padding which would create high-contrast edges).
    var padW = ((srcW + DimensionMultiple - 1) / DimensionMultiple) * DimensionMultiple;
    var padH = ((srcH + DimensionMultiple - 1) / DimensionMultiple) * DimensionMultiple;

    ct.ThrowIfCancellationRequested();

    // Build input tensor: [1, 1, padH, padW] grayscale, normalised
    // to [-1, +1] using the torchvision (mean=0.5, std=0.5) convention
    // the BOPB pipeline trained against.
    var pixelCount = padH * padW;
    var input = new float[pixelCount];
    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < padH; y++) {
        var sy = Math.Min(y, srcH - 1);
        var row = accessor.GetRowSpan(sy);
        for (var x = 0; x < padW; x++) {
          var sx = Math.Min(x, srcW - 1);
          var px = row[sx];
          var luma = (0.2126f * px.R + 0.7152f * px.G + 0.0722f * px.B) / 255f;
          // Normalise: (luma - 0.5) / 0.5 → range [-1, +1].
          input[y * padW + x] = (luma - 0.5f) / 0.5f;
        }
      }
    });

    ct.ThrowIfCancellationRequested();

    float[] outputLogits;
    int outH, outW;
    try {
      var tensor = new DenseTensor<float>(input, new[] { 1, 1, padH, padW });
      using var results = info.Session.Run(new[] { NamedOnnxValue.CreateFromTensor(info.InputName, tensor) });
      var first = results.First();
      var dims = first.AsTensor<float>().Dimensions;
      outH = dims.Length >= 4 ? dims[2] : padH;
      outW = dims.Length >= 4 ? dims[3] : padW;
      outputLogits = first.AsTensor<float>().ToArray();
    } catch {
      return null;
    }

    ct.ThrowIfCancellationRequested();

    // Sigmoid + threshold + crop to source resolution.
    var mask = new Image<Rgba32>(srcW, srcH);
    var probThreshold = (float)Math.Clamp(threshold, 0.0, 1.0);
    mask.ProcessPixelRows(accessor => {
      for (var y = 0; y < srcH; y++) {
        var row = accessor.GetRowSpan(y);
        var srcOff = y * outW;
        for (var x = 0; x < srcW; x++) {
          var logit = outputLogits[srcOff + x];
          var prob = Sigmoid(logit);
          row[x] = prob >= probThreshold
            ? new Rgba32((byte)255, (byte)0, (byte)0, (byte)200)
            : default;
        }
      }
    });
    return mask;
  }

  private static float Sigmoid(float x) {
    if (x >= 0) {
      var e = (float)Math.Exp(-x);
      return 1f / (1f + e);
    } else {
      var e = (float)Math.Exp(x);
      return e / (1f + e);
    }
  }

  private static SessionInfo? TryOpenSession(FileInfo modelFile) {
    if (!modelFile.Exists)
      return null;
    try {
      // CPU EP forced (preferCpu: true) — empirical finding: when BOPB
      // ran on OpenVINO and DDColor ran later on CPU, DDColor's
      // inference produced near-zero a/b (silent grayscale output).
      // LaMa between them was unaffected. Forcing every model in the
      // auto-scratch → recolour chain onto CPU EP avoids whatever
      // shared-runtime state OpenVINO leaves behind when stacked
      // before another EP's session. BOPB is small enough that CPU
      // inference is acceptable (~1s per detect pass).
      var session = OnnxAcceleration.CreateSession(modelFile.FullName, preferCpu: true);
      var inputName = session.InputMetadata.Keys.First();
      return new SessionInfo(session, inputName);
    } catch (Exception ex) {
      throw new InvalidOperationException(
        $"BOPB scratch detector session creation failed for {modelFile.Name} (CPU EP): {ex.Message}", ex);
    }
  }

  public void Dispose() {
    // Sessions are cached by OnnxAcceleration and shared across
    // instances; disposing them here would break the cache.
    // OnnxAcceleration.ResetCache() handles teardown if needed.
  }

  private sealed record SessionInfo(InferenceSession Session, string InputName);
}
