using PhotoManager.Core.Develop;
using PhotoManager.Core.Imaging;
using PhotoManager.Core.Models;
using PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Enhance;

/// <summary>
/// One-click "make this smartphone shot look like art" orchestrator.
/// Runs detection (low-light, noise, haze, low-res), assembles a
/// pipeline that includes only the stages that apply, and executes it.
/// The output is paired with the input NIMA score and the post-enhance
/// NIMA score so the caller can show the user "+1.3 aesthetic points"
/// feedback.
///
/// Pipeline order matters and follows the established editing rule:
///   1. Low-light lift  (Zero-DCE++)             — if scene is dark
///   2. Denoise          (NAFNet)                 — after lift, since lift amplifies noise
///   3. Artifact remove  (FBCNN)                  — JPEG ringing / blocking
///   4. Dehaze           (AOD-Net)                — if hazy
///   5. Auto WB          (heuristic)              — neutralise colour cast
///   6. Auto tone        (heuristic)              — exposure / contrast
///   7. CLAHE            (pure C# local contrast) — "pop"
///   8. Upscale          (Real-ESRGAN x4 chained) — if &lt; 2 MP
///
/// Each ONNX-backed stage is OPTIONAL — when the model isn't installed
/// the orchestrator skips it and notes that in the plan log. The pure-C#
/// stages always run when the detector says they apply.
/// </summary>
public static class MagicEnhancer {
  /// <summary>
  /// One-shot orchestration over <paramref name="source"/>. Returns a
  /// freshly allocated enhanced image; caller owns it.
  /// </summary>
  public static async Task<MagicEnhanceResult> EnhanceAsync(
      Image<Rgba32> source,
      MagicEnhanceOptions? options = null,
      CancellationToken ct = default,
      IProgress<string>? progress = null) {
    ArgumentNullException.ThrowIfNull(source);
    var opts = options ?? new MagicEnhanceOptions();

    // 0. Pre-enhance NIMA score.
    double? nimaBefore = null;
    if (opts.ComputeNimaScore && ModelRegistry.NimaMobileNetV2.IsInstalled()) {
      progress?.Report("scoring before");
      using var nima = new OnnxAestheticScorer(ModelRegistry.NimaMobileNetV2.ResolveDestination());
      if (nima.IsAvailable) {
        var s = await nima.ScoreAsync(source, ct);
        nimaBefore = s?.Mean;
      }
    }

    var log = new List<string>();
    var current = source.Clone();
    var disposeCurrent = true;

    void Swap(Image<Rgba32>? newImg, string description) {
      if (newImg == null) {
        log.Add($"× {description} (skipped — model unavailable)");
        return;
      }
      if (disposeCurrent)
        current.Dispose();
      current = newImg;
      disposeCurrent = true;
      log.Add($"✓ {description}");
    }

    // 1. Low-light lift.
    var lowLight = PhotoIssueDetector.LowLightScore(current);
    if (lowLight >= opts.LowLightThreshold) {
      ct.ThrowIfCancellationRequested();
      progress?.Report($"low-light (score {lowLight:F2})");
      if (ModelRegistry.ZeroDcePp.IsInstalled()) {
        using var enhancer = new OnnxLowLightEnhancer(ModelRegistry.ZeroDcePp.ResolveDestination());
        Swap(await enhancer.EnhanceAsync(current, ct), $"low-light lift (Zero-DCE++)");
      } else {
        log.Add("× low-light lift (Zero-DCE++ not installed)");
      }
    }

    // 2. Denoise.
    var noise = PhotoIssueDetector.NoiseScore(current);
    if (noise >= opts.NoiseThreshold) {
      ct.ThrowIfCancellationRequested();
      progress?.Report($"denoise (score {noise:F2})");
      if (ModelRegistry.NafnetSidd.IsInstalled()) {
        using var denoiser = new OnnxDenoiser(ModelRegistry.NafnetSidd.ResolveDestination());
        Swap(await Task.Run(() => denoiser.Denoise(current, strength: 1.0, ct), ct), "denoise (NAFNet)");
      } else {
        log.Add("× denoise (NAFNet not installed)");
      }
    }

    // 3. JPEG artifact removal — opt-in via options; default off because
    //    FBCNN is large (~300 MB) and pointless on non-JPEG sources.
    if (opts.RunArtifactRemoval && ModelRegistry.ArtifactRemoverFbcnnColor.IsInstalled()) {
      ct.ThrowIfCancellationRequested();
      progress?.Report("artifact removal");
      using var arr = new OnnxArtifactRemover(ModelRegistry.ArtifactRemoverFbcnnColor.ResolveDestination());
      Swap(await Task.Run(() => arr.Remove(current, strength: 1.0, ct), ct), "JPEG artifact removal (FBCNN)");
    }

    // 4. Dehaze.
    var haze = PhotoIssueDetector.HazeScore(current);
    if (haze >= opts.HazeThreshold) {
      ct.ThrowIfCancellationRequested();
      progress?.Report($"dehaze (score {haze:F2})");
      if (ModelRegistry.AodNetDehazer.IsInstalled()) {
        using var dehazer = new OnnxDehazer(ModelRegistry.AodNetDehazer.ResolveDestination());
        Swap(await dehazer.DehazeAsync(current, ct), "dehaze (AOD-Net)");
      } else {
        log.Add("× dehaze (AOD-Net not installed)");
      }
    }

    // 5-6. Auto WB + Auto Tone — pure C#, always run unless opted out.
    if (opts.RunAutoCorrections) {
      ct.ThrowIfCancellationRequested();
      progress?.Report("auto white-balance + tone");
      var (tShift, tintShift) = AutoWhiteBalance.Estimate(current);
      var histogram = HistogramAnalyzer.Compute(current);
      var ds = new DevelopSettings { TemperatureShift = tShift, TintShift = tintShift };
      ds = AutoDeveloper.AutoTone(ds, histogram);
      // We piggy-back on the develop pipeline rather than re-implementing
      // tone math here. ImageDeveloper.Apply returns a freshly allocated
      // image we own.
      var toned = ImageDeveloper.Apply(current, ds, previewMode: true);
      Swap(toned, "auto white-balance + auto tone");
    }

    // 7. CLAHE — pure C# local-contrast pop.
    if (opts.RunClahe) {
      ct.ThrowIfCancellationRequested();
      progress?.Report("CLAHE local contrast");
      Swap(Clahe.Apply(current), "CLAHE local contrast");
    }

    // 8. Upscale — only when the source is genuinely low resolution.
    var lowRes = PhotoIssueDetector.LowResolutionScore(current);
    if (opts.RunUpscale && lowRes >= opts.LowResolutionThreshold) {
      ct.ThrowIfCancellationRequested();
      progress?.Report($"upscale (low-res score {lowRes:F2})");
      var upscaleModel = opts.UpscaleModelFileName ?? ModelRegistry.RealEsrganX4.FileName;
      var info = ModelRegistry.All.FirstOrDefault(m => m.FileName == upscaleModel);
      if (info?.IsInstalled() == true) {
        using var upscaler = new OnnxUpscaler(info.ResolveDestination());
        var factor = lowRes >= 0.7 ? 4 : 2;
        Swap(upscaler.Upscale(current, factor, ct), $"upscale ×{factor} ({info.DisplayName})");
      } else {
        log.Add($"× upscale ({upscaleModel} not installed)");
      }
    }

    // Post-enhance NIMA score.
    double? nimaAfter = null;
    if (opts.ComputeNimaScore && ModelRegistry.NimaMobileNetV2.IsInstalled()) {
      progress?.Report("scoring after");
      using var nima = new OnnxAestheticScorer(ModelRegistry.NimaMobileNetV2.ResolveDestination());
      if (nima.IsAvailable) {
        var s = await nima.ScoreAsync(current, ct);
        nimaAfter = s?.Mean;
      }
    }

    return new MagicEnhanceResult(current, log, nimaBefore, nimaAfter);
  }
}

/// <summary>
/// Thresholds + opt-ins for <see cref="MagicEnhancer.EnhanceAsync"/>.
/// Defaults reflect "smartphone snap" expectations — adjust when running
/// against pro/already-clean sources.
/// </summary>
public sealed record MagicEnhanceOptions(
  double LowLightThreshold     = 0.3,
  double NoiseThreshold        = 0.3,
  double HazeThreshold         = 0.3,
  double LowResolutionThreshold = 0.4,
  /// <summary>When true, ImageDeveloper applies auto-WB + auto-tone heuristics.</summary>
  bool RunAutoCorrections      = true,
  /// <summary>When true, CLAHE adds local-contrast pop after auto-tone.</summary>
  bool RunClahe                = true,
  /// <summary>When true, Real-ESRGAN upscale fires for very-low-res sources.</summary>
  bool RunUpscale              = true,
  /// <summary>When true, FBCNN JPEG-artifact removal runs unconditionally (defaults off — big model, niche).</summary>
  bool RunArtifactRemoval      = false,
  /// <summary>When true, NIMA scores the source + result so the UI can show the delta.</summary>
  bool ComputeNimaScore        = true,
  /// <summary>Override the upscale model. Null = the registry default.</summary>
  string? UpscaleModelFileName = null
);

/// <summary>Result of Magic Enhance — enhanced image + a per-stage log + NIMA scores.</summary>
public sealed record MagicEnhanceResult(
  Image<Rgba32> Enhanced,
  IReadOnlyList<string> Log,
  double? NimaScoreBefore,
  double? NimaScoreAfter
);
