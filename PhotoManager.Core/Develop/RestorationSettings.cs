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
  /// <summary>0 = leave colour alone, 1 = full DeOldify recolour. Most useful for B&amp;W sources.</summary>
  double RecolourStrength = 0.0,
  /// <summary>True = run AutoTone before any AI stages; false = leave tone alone.</summary>
  bool AutoTone = false,
  /// <summary>1 = no upscale; 2 / 4 / 16 / 64 = output size multiplier.</summary>
  int UpscaleFactor = 1,
  /// <summary>Optional model file-name override for the denoiser (matches DevelopSettings.AiDenoiseModel).</summary>
  string? DenoiseModel = null,
  /// <summary>Optional model file-name override for the colorizer.</summary>
  string? ColorizeModel = null,
  /// <summary>Optional model file-name override for the upscaler.</summary>
  string? UpscaleModel = null
) {
  public bool IsIdentity =>
    this.FaceRestoreStrength <= 1e-6
    && this.DenoiseStrength <= 1e-6
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
