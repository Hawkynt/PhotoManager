namespace PhotoManager.Core.Develop;

/// <summary>
/// User-facing knobs for the restoration window. Each value is 0..1
/// (slider range / 100) and gets applied as a strength against the
/// corresponding ONNX model — internal pipeline machinery converts to
/// the model-specific parameters so inexperienced users can leave the
/// presets alone and still get superior results.
///
/// <see cref="UpscaleFactor"/> is the only non-strength knob: 1 = off,
/// 2 / 4 / 16 / 64 follow the same set as the develop-window upscaler.
/// </summary>
public sealed record RestorationSettings(
  /// <summary>0 = leave faces alone, 1 = full GFPGAN restoration on every detected face.</summary>
  double FaceRestoreStrength = 0.0,
  /// <summary>0 = no denoise, 1 = full denoise.</summary>
  double DenoiseStrength = 0.0,
  /// <summary>0 = leave artifacts alone, 1 = full FBCNN JPEG / ringing / halo removal. Runs between denoise and recolour.</summary>
  double ArtifactRemoveStrength = 0.0,
  /// <summary>0 = leave colour alone, 1 = full DeOldify recolour. Most useful for B&amp;W sources.</summary>
  double RecolourStrength = 0.0,
  /// <summary>True = run AutoTone before any AI stages; false = leave tone alone.</summary>
  bool AutoTone = false,
  /// <summary>1 = no upscale; 2 / 4 / 16 / 64 = output size multiplier.</summary>
  int UpscaleFactor = 1,
  /// <summary>Optional model file-name override for the face restorer (GFPGAN by default, CodeFormer also supported).</summary>
  string? FaceRestoreModel = null,
  /// <summary>Optional model file-name override for the denoiser (matches DevelopSettings.AiDenoiseModel).</summary>
  string? DenoiseModel = null,
  /// <summary>Optional model file-name override for the JPEG / artifact remover.</summary>
  string? ArtifactRemoveModel = null,
  /// <summary>Optional model file-name override for the colorizer.</summary>
  string? ColorizeModel = null,
  /// <summary>Optional model file-name override for the upscaler.</summary>
  string? UpscaleModel = null,
  /// <summary>Multiplier applied to the colorizer's predicted chroma (a/b channels in Lab).
  /// 1.0 = raw model output (DDColor's predictions are conservative — typical std ≈ 5–10 vs the 30–50 a saturated photo carries),
  /// 1.6 = recommended default (visibly vivid without obvious oversaturation),
  /// 0.0 = grayscale (no colour added). Only consumed by Lab-based colorizers (DDColor); ignored by DeOldify.</summary>
  double ChromaBoost = 1.6,
  /// <summary>True = auto-detect + inpaint scratches before recolour /
  /// face restore / upscale stages run. Scratches are luminance-domain
  /// damage and detect best on the un-modified source — running
  /// detection AFTER colorize has been observed to miss scratches the
  /// detector can find on the original. Default true so Save As gets
  /// the right pipeline order even when the user only set sliders.</summary>
  bool AutoScratchRemoval = true,
  /// <summary>Maximum detect→inpaint iterations the auto-scratch
  /// stage will run before giving up. Default lowered from 5 → 1
  /// because every additional LaMa pass introduces synthesis
  /// artifacts that can collapse DDColor paper-tiny's chroma
  /// prediction. One pass matches what the Auto-detect button +
  /// brush-inpaint stage does, which is the configuration paper-tiny
  /// reliably colorizes after. Bump up for heavily-damaged scans
  /// where residual scratches are more annoying than colour loss.</summary>
  int AutoScratchMaxIterations = 1,
  /// <summary>Stop the auto-scratch loop once detected mask coverage
  /// drops below this fraction of pixels (in %). Mirrors the UI's
  /// "Stop below %" numericupdown.</summary>
  double AutoScratchThresholdPct = 0.3,
  /// <summary>Salt-and-pepper despeckle strength (0..1). Pure-C#
  /// adaptive median filter; cheap. Runs after auto-scratch but
  /// before recolour so dust speckles don't get amplified into
  /// coloured artefacts.</summary>
  double DespeckleStrength = 0.0,
  /// <summary>BOPB detector probability threshold for the
  /// auto-scratch pipeline. 0..1; higher = stricter (fewer false
  /// positives). 0.4 matches BOPB's own default and the Auto-detect
  /// button's threshold — that's the recommended value, especially
  /// when chaining auto-scratch into colorize, because aggressive
  /// thresholds (≤0.3) inpaint subtle real-image detail and can
  /// produce inputs that DDColor paper-tiny silently fails on.</summary>
  double AutoScratchDetectorThreshold = 0.4
) {
  public bool IsIdentity =>
    this.FaceRestoreStrength <= 1e-6
    && this.DenoiseStrength <= 1e-6
    && this.ArtifactRemoveStrength <= 1e-6
    && this.RecolourStrength <= 1e-6
    && !this.AutoTone
    && this.UpscaleFactor <= 1;

  /// <summary>"Old B&W photo" preset — bold defaults aimed at scanned monochrome originals.</summary>
  public static RestorationSettings OldBlackAndWhite { get; } = new(
    FaceRestoreStrength: 0.8,
    DenoiseStrength:     0.7,
    RecolourStrength:    0.8,
    AutoTone:            true,
    UpscaleFactor:       4
  );

  /// <summary>"Damaged colour photo" preset — keeps the existing colour, denoises and restores faces.</summary>
  public static RestorationSettings DamagedColour { get; } = new(
    FaceRestoreStrength: 0.8,
    DenoiseStrength:     0.7,
    RecolourStrength:    0.0,
    AutoTone:            true,
    UpscaleFactor:       2
  );

  /// <summary>"Faded slide / VHS frame" preset — gentle, preserves character.</summary>
  public static RestorationSettings FadedSlide { get; } = new(
    FaceRestoreStrength: 0.5,
    DenoiseStrength:     0.5,
    RecolourStrength:    0.0,
    AutoTone:            true,
    UpscaleFactor:       2
  );

  /// <summary>"Subtle clean-up" preset — light touch for already-decent originals.</summary>
  public static RestorationSettings SubtleCleanup { get; } = new(
    FaceRestoreStrength: 0.4,
    DenoiseStrength:     0.3,
    RecolourStrength:    0.0,
    AutoTone:            true,
    UpscaleFactor:       1
  );
}
