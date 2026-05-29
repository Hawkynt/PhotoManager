namespace PhotoManager.Core.Develop;

/// <summary>
/// Non-destructive develop parameters applied by <see cref="ImageDeveloper"/>.
/// Modeled on Lightroom's Basic + Detail panels but kept lightweight so the
/// CPU-bound preview stays interactive on big RAWs.
///
/// All adjustments default to a no-op so callers and templates can fill in
/// only what they want to override.
/// </summary>
/// <param name="RotationDegrees">Multiples of 90° (0, 90, 180, 270).</param>
/// <param name="ExposureStops">EV shift; ±1 doubles/halves brightness. Range roughly ±5.</param>
/// <param name="ContrastPercent">-100..+100 about midgray. +50 boosts S-curve.</param>
/// <param name="HighlightsPercent">-100..+100. Negative recovers blown highlights; positive lifts them further.</param>
/// <param name="ShadowsPercent">-100..+100. Positive lifts dark areas; negative crushes them.</param>
/// <param name="WhitesPercent">-100..+100. Anchors / lifts the white point.</param>
/// <param name="BlacksPercent">-100..+100. Anchors / lifts the black point.</param>
/// <param name="SaturationPercent">-100 (grayscale) to +100 (double saturation), uniform.</param>
/// <param name="VibrancePercent">-100..+100. Boosts low-saturation colors more than already-saturated ones.</param>
/// <param name="ClarityPercent">-100..+100. Local-contrast boost in the midtones (unsharp mask).</param>
/// <param name="TexturePercent">-100..+100. High-frequency detail boost; smaller spatial scale than Clarity.</param>
/// <param name="SmoothnessPercent">0..100. Gaussian-blur strength applied before sharpening; doubles as a basic noise reducer.</param>
/// <param name="SharpeningAmount">0..150. Edge enhancement applied last.</param>
/// <param name="TemperatureShift">-100..+100. Negative pushes blue (cooler), positive red (warmer).</param>
/// <param name="TintShift">-100..+100. Negative pushes green, positive magenta.</param>
/// <param name="RedGain">-100..+100. Per-channel multiplier on the red channel (after exposure / WB).</param>
/// <param name="GreenGain">Same for green.</param>
/// <param name="BlueGain">Same for blue.</param>
/// <param name="ToneCurvePoints">User-placed control points for the master tone curve, normalized 0..1. Empty / one-point / null = identity.</param>
/// <param name="ToneCurveInterpolation">Interpolation between tone-curve points: piecewise <see cref="CurveInterpolation.Linear"/>, smooth <see cref="CurveInterpolation.CatmullRom"/> through every point, or <see cref="CurveInterpolation.Bezier"/> with auto-derived tangents.</param>
/// <param name="RedCurvePoints">Per-channel tone curve for the red channel; null = identity. Applied AFTER the master curve so users can tune individual colors without disturbing luminance.</param>
/// <param name="GreenCurvePoints">Same, green channel.</param>
/// <param name="BlueCurvePoints">Same, blue channel.</param>
/// <param name="DehazePercent">-100..+100. Positive removes atmospheric haze (boosts midtone contrast + saturation); negative softens / adds haze.</param>
/// <param name="SharpenRadius">0..3 pixels. 0 = derive from <see cref="SharpeningAmount"/> for backwards compat; otherwise the explicit Gaussian sigma.</param>
/// <param name="SharpenDetail">0..100. Strength of an extra fine-radius sharpening pass for high-frequency detail.</param>
/// <param name="SharpenMasking">0..100. How aggressively edge masking suppresses sharpening on flat areas. Currently emitted to crs: but not yet applied to pixels.</param>
/// <param name="HslHueShifts">Eight per-band hue shifts in degrees, one per <see cref="HslBand"/>. Null / empty = identity. Range -100..+100; ±100 nudges the band hue by ~30°.</param>
/// <param name="HslSaturationShifts">Eight per-band saturation shifts -100..+100; -100 desaturates the band, +100 doubles its saturation.</param>
/// <param name="HslLuminanceShifts">Eight per-band luminance shifts -100..+100; brightens or darkens pixels whose hue lands in the band without changing colour.</param>
/// <param name="CropAngleDegrees">Free rotation in degrees, applied AFTER <see cref="RotationDegrees"/>. Range ±45 in the UI; ImageSharp does the trig.</param>
/// <param name="CropLeft">Left crop edge in normalised image coords (0..1). 0 = keep all of left side.</param>
/// <param name="CropTop">Top crop edge (0..1).</param>
/// <param name="CropRight">Right crop edge (0..1). 1 = keep all of right side.</param>
/// <param name="CropBottom">Bottom crop edge (0..1).</param>
public sealed record DevelopSettings(
  int RotationDegrees = 0,
  double ExposureStops = 0,
  double ContrastPercent = 0,
  double HighlightsPercent = 0,
  double ShadowsPercent = 0,
  double WhitesPercent = 0,
  double BlacksPercent = 0,
  double SaturationPercent = 0,
  double VibrancePercent = 0,
  double ClarityPercent = 0,
  double TexturePercent = 0,
  double SmoothnessPercent = 0,
  double SharpeningAmount = 0,
  double TemperatureShift = 0,
  double TintShift = 0,
  double RedGain = 0,
  double GreenGain = 0,
  double BlueGain = 0,
  IReadOnlyList<CurvePoint>? ToneCurvePoints = null,
  CurveInterpolation ToneCurveInterpolation = CurveInterpolation.Linear,
  IReadOnlyList<CurvePoint>? RedCurvePoints = null,
  IReadOnlyList<CurvePoint>? GreenCurvePoints = null,
  IReadOnlyList<CurvePoint>? BlueCurvePoints = null,
  double DehazePercent = 0,
  double SharpenRadius = 0,
  double SharpenDetail = 0,
  double SharpenMasking = 0,
  IReadOnlyList<double>? HslHueShifts = null,
  IReadOnlyList<double>? HslSaturationShifts = null,
  IReadOnlyList<double>? HslLuminanceShifts = null,
  double CropAngleDegrees = 0,
  double CropLeft = 0,
  double CropTop = 0,
  double CropRight = 1,
  double CropBottom = 1,
  // Color Grading — three luminance-band wheels + a global wheel.
  // Hue is 0..360°, Sat 0..100%, Lum -100..+100%. All zero = no grade.
  double GradeShadowHue = 0,
  double GradeShadowSat = 0,
  double GradeShadowLum = 0,
  double GradeMidtoneHue = 0,
  double GradeMidtoneSat = 0,
  double GradeMidtoneLum = 0,
  double GradeHighlightHue = 0,
  double GradeHighlightSat = 0,
  double GradeHighlightLum = 0,
  double GradeGlobalHue = 0,
  double GradeGlobalSat = 0,
  double GradeGlobalLum = 0,
  // Vignette / grain (post-crop). Amount is the gating slider; the rest only matter when amount != 0.
  double VignetteAmount = 0,
  double VignetteMidpoint = 50,
  double VignetteFeather = 50,
  double VignetteRoundness = 0,
  double VignetteHighlightContrast = 0,
  double GrainAmount = 0,
  double GrainSize = 25,
  double GrainFrequency = 50,
  // Noise reduction — luminance amount lives in SmoothnessPercent above.
  // These extras refine the look (detail/contrast) and add chroma NR.
  double LuminanceNrDetail = 0,
  double LuminanceNrContrast = 0,
  double ColorNoiseReduction = 0,
  double ColorNrDetail = 0,
  double ColorNrSmoothness = 0,
  // Black & White conversion. ConvertToGrayscale gates the whole block;
  // the eight GrayMixer values match the HSL bands and control how each
  // hue contributes to the gray output (-100 darker / +100 brighter).
  bool ConvertToGrayscale = false,
  double GrayMixerRed = 0,
  double GrayMixerOrange = 0,
  double GrayMixerYellow = 0,
  double GrayMixerGreen = 0,
  double GrayMixerAqua = 0,
  double GrayMixerBlue = 0,
  double GrayMixerPurple = 0,
  double GrayMixerMagenta = 0,
  // Split Toning — Adobe's predecessor to Color Grading. Shadows + Highlights
  // each get a tinted hue + saturation; Balance shifts the lum split point.
  double SplitToningShadowHue = 0,
  double SplitToningShadowSaturation = 0,
  double SplitToningHighlightHue = 0,
  double SplitToningHighlightSaturation = 0,
  double SplitToningBalance = 0,
  // Parametric tone curve — 4 luminance bands with adjustable split points.
  // Lightroom users reach for this when the master tone curve isn't precise
  // enough but they don't want to hand-place control points.
  double ParametricShadows = 0,
  double ParametricDarks = 0,
  double ParametricLights = 0,
  double ParametricHighlights = 0,
  double ParametricShadowSplit = 25,
  double ParametricMidtoneSplit = 50,
  double ParametricHighlightSplit = 75,
  // Lens corrections. Manual distortion bends the image radially (negative
  // = barrel correction, positive = pincushion). Chromatic aberration R/B
  // are tiny per-channel scale tweaks that align fringes around edges.
  double LensManualDistortion = 0,
  double ChromaticAberrationR = 0,
  double ChromaticAberrationB = 0,
  // Defringe — narrow-range desaturation of purple / green halos on edges.
  // Adobe ranges 0..20 (small) so a setting of 4–8 is normal.
  double DefringePurpleAmount = 0,
  double DefringeGreenAmount = 0,
  // Camera calibration — small per-primary hue rotation + saturation scale.
  // Used by colour scientists to fine-tune the camera's primary mapping.
  double CalibrationRedHue = 0,
  double CalibrationRedSaturation = 0,
  double CalibrationGreenHue = 0,
  double CalibrationGreenSaturation = 0,
  double CalibrationBlueHue = 0,
  double CalibrationBlueSaturation = 0,
  // Perspective / Upright transforms — projective warp implemented in a
  // single coord-map pass. Vertical / Horizontal are keystone corrections
  // (pull top vs bottom / left vs right), Scale is uniform zoom, Aspect
  // stretches Y vs X, X / Y translate, Rotate is a small in-plane rotate
  // applied within the projective stage. Adobe defaults Scale to 100.
  double PerspectiveVertical = 0,
  double PerspectiveHorizontal = 0,
  double PerspectiveRotate = 0,
  double PerspectiveScale = 100,
  double PerspectiveAspect = 0,
  double PerspectiveX = 0,
  double PerspectiveY = 0,
  // Local adjustments — each entry is one masked correction (linear or
  // radial gradient with its own develop sliders). Round-trips through
  // crs:MaskGroupBasedCorrections so Lightroom sees the same masks.
  // Foreign mask li's (Brush, AI subject masks) survive saves because
  // SaveAsync re-derives them from the existing XMP rather than from
  // this list — keeps the in-memory model lean.
  IReadOnlyList<LocalAdjustment>? LocalAdjustments = null,
  /// <summary>
  /// Skin-tone-protective vibrance. Acts like Vibrance but weights down
  /// orange / yellow hues so people don't get plastic-y when bumping
  /// colour overall. Adobe's "Vibrance" before 2023 then became this
  /// dedicated slider. Range -100..+100.
  /// </summary>
  double ColorEnhancement = 0,
  /// <summary>
  /// Filename (without extension) of a 3D LUT under
  /// <c>%APPDATA%/PhotoManager/luts/</c> applied at render time as a
  /// "creative look". Round-trips through <c>crs:LookName</c> so 3rd-party
  /// tools can label the look.
  /// </summary>
  string? LookName = null,
  /// <summary>
  /// Opacity (0..1) of the creative-look LUT blend. 1.0 = full LUT effect,
  /// 0.0 = LUT skipped. Has no effect when <see cref="LookName"/> is null.
  /// </summary>
  double LookOpacity = 1.0,
  // Watermark layer rendered after locals and before the crop. Null text
  // disables the stage. No Adobe analogue — round-tripped via the pm: extras.
  string? WatermarkText = null,
  double WatermarkOpacity = 0.5,
  string WatermarkPosition = "BottomRight",
  int WatermarkFontSize = 24,
  /// <summary>AI denoise strength 0..1; 0 = off. Runs BEFORE the sharpening pass.</summary>
  double AiDenoiseStrength = 0,
  /// <summary>Optional model file-name override for the denoiser; null = default ("denoise.onnx").</summary>
  string? AiDenoiseModel = null,
  /// <summary>AI upscale factor — 1 = off, 2 / 4 / 16 / 64 = output dimensions multiplied. Runs AFTER tone/curves/HSL, BEFORE crop.</summary>
  int AiUpscaleFactor = 1,
  /// <summary>Optional model file-name override for the upscaler; null = default ("upscale.onnx"). Lets the user pick between Real-ESRGAN / SwinIR / etc.</summary>
  string? AiUpscaleModel = null,
  /// <summary>AI colorize blend 0..1; 0 = off (B&amp;W untouched), 1 = full colorised. Runs BEFORE creative look so LUTs operate on the colorised image.</summary>
  double AiColorizeAmount = 0,
  /// <summary>Optional model file-name override for the colorizer; null = default ("colorize-deoldify-artistic.onnx"). Pick between DeOldify Artistic / Stable / etc.</summary>
  string? AiColorizeModel = null
) {
  public bool IsIdentity =>
    this.RotationDegrees == 0
    && Math.Abs(this.ExposureStops) < 1e-6
    && Math.Abs(this.ContrastPercent) < 1e-6
    && Math.Abs(this.HighlightsPercent) < 1e-6
    && Math.Abs(this.ShadowsPercent) < 1e-6
    && Math.Abs(this.WhitesPercent) < 1e-6
    && Math.Abs(this.BlacksPercent) < 1e-6
    && Math.Abs(this.SaturationPercent) < 1e-6
    && Math.Abs(this.VibrancePercent) < 1e-6
    && Math.Abs(this.ClarityPercent) < 1e-6
    && Math.Abs(this.TexturePercent) < 1e-6
    && Math.Abs(this.SmoothnessPercent) < 1e-6
    && Math.Abs(this.SharpeningAmount) < 1e-6
    && Math.Abs(this.TemperatureShift) < 1e-6
    && Math.Abs(this.TintShift) < 1e-6
    && Math.Abs(this.RedGain) < 1e-6
    && Math.Abs(this.GreenGain) < 1e-6
    && Math.Abs(this.BlueGain) < 1e-6
    && Math.Abs(this.DehazePercent) < 1e-6
    && Math.Abs(this.SharpenRadius) < 1e-6
    && Math.Abs(this.SharpenDetail) < 1e-6
    && Math.Abs(this.SharpenMasking) < 1e-6
    && Math.Abs(this.CropAngleDegrees) < 1e-6
    && Math.Abs(this.CropLeft) < 1e-6
    && Math.Abs(this.CropTop) < 1e-6
    && Math.Abs(this.CropRight - 1) < 1e-6
    && Math.Abs(this.CropBottom - 1) < 1e-6
    && IsZeroBandList(this.HslHueShifts)
    && IsZeroBandList(this.HslSaturationShifts)
    && IsZeroBandList(this.HslLuminanceShifts)
    // Color grading: a section is identity when both Sat and Lum are zero,
    // since Hue alone has no visible effect at zero saturation.
    && Math.Abs(this.GradeShadowSat)    < 1e-6 && Math.Abs(this.GradeShadowLum)    < 1e-6
    && Math.Abs(this.GradeMidtoneSat)   < 1e-6 && Math.Abs(this.GradeMidtoneLum)   < 1e-6
    && Math.Abs(this.GradeHighlightSat) < 1e-6 && Math.Abs(this.GradeHighlightLum) < 1e-6
    && Math.Abs(this.GradeGlobalSat)    < 1e-6 && Math.Abs(this.GradeGlobalLum)    < 1e-6
    && Math.Abs(this.VignetteAmount)         < 1e-6
    && Math.Abs(this.GrainAmount)            < 1e-6
    && Math.Abs(this.ColorNoiseReduction)    < 1e-6
    && Math.Abs(this.LuminanceNrDetail)      < 1e-6
    && Math.Abs(this.LuminanceNrContrast)    < 1e-6
    && Math.Abs(this.ColorNrDetail)          < 1e-6
    && Math.Abs(this.ColorNrSmoothness)      < 1e-6
    && !this.ConvertToGrayscale
    && Math.Abs(this.GrayMixerRed)     < 1e-6 && Math.Abs(this.GrayMixerOrange)  < 1e-6
    && Math.Abs(this.GrayMixerYellow)  < 1e-6 && Math.Abs(this.GrayMixerGreen)   < 1e-6
    && Math.Abs(this.GrayMixerAqua)    < 1e-6 && Math.Abs(this.GrayMixerBlue)    < 1e-6
    && Math.Abs(this.GrayMixerPurple)  < 1e-6 && Math.Abs(this.GrayMixerMagenta) < 1e-6
    && Math.Abs(this.SplitToningShadowSaturation)    < 1e-6
    && Math.Abs(this.SplitToningHighlightSaturation) < 1e-6
    && Math.Abs(this.ParametricShadows)    < 1e-6
    && Math.Abs(this.ParametricDarks)      < 1e-6
    && Math.Abs(this.ParametricLights)     < 1e-6
    && Math.Abs(this.ParametricHighlights) < 1e-6
    && Math.Abs(this.LensManualDistortion) < 1e-6
    && Math.Abs(this.ChromaticAberrationR) < 1e-6
    && Math.Abs(this.ChromaticAberrationB) < 1e-6
    && Math.Abs(this.DefringePurpleAmount) < 1e-6
    && Math.Abs(this.DefringeGreenAmount)  < 1e-6
    && Math.Abs(this.CalibrationRedHue)         < 1e-6
    && Math.Abs(this.CalibrationRedSaturation)  < 1e-6
    && Math.Abs(this.CalibrationGreenHue)       < 1e-6
    && Math.Abs(this.CalibrationGreenSaturation) < 1e-6
    && Math.Abs(this.CalibrationBlueHue)        < 1e-6
    && Math.Abs(this.CalibrationBlueSaturation) < 1e-6
    && Math.Abs(this.PerspectiveVertical)   < 1e-6
    && Math.Abs(this.PerspectiveHorizontal) < 1e-6
    && Math.Abs(this.PerspectiveRotate)     < 1e-6
    && Math.Abs(this.PerspectiveScale - 100) < 1e-6
    && Math.Abs(this.PerspectiveAspect)     < 1e-6
    && Math.Abs(this.PerspectiveX)          < 1e-6
    && Math.Abs(this.PerspectiveY)          < 1e-6
    && (this.LocalAdjustments is null || this.LocalAdjustments.Count == 0
        || this.LocalAdjustments.All(a => a.Mask.Type != LocalMaskType.Inpaint && a.IsZero))
    && Math.Abs(this.ColorEnhancement) < 1e-6
    && string.IsNullOrEmpty(this.LookName)
    && string.IsNullOrEmpty(this.WatermarkText)
    && Math.Abs(this.AiDenoiseStrength) < 1e-6
    && this.AiUpscaleFactor <= 1
    && Math.Abs(this.AiColorizeAmount) < 1e-6
    && this.IsCurveIdentity
    && IsChannelCurveIdentity(this.RedCurvePoints)
    && IsChannelCurveIdentity(this.GreenCurvePoints)
    && IsChannelCurveIdentity(this.BlueCurvePoints);

  /// <summary>True when the tone curve is the identity (or unset).</summary>
  public bool IsCurveIdentity => IsChannelCurveIdentity(this.ToneCurvePoints);

  private static bool IsChannelCurveIdentity(IReadOnlyList<CurvePoint>? points) {
    if (points is null || points.Count < 2)
      return true;
    foreach (var p in points)
      if (Math.Abs(p.X - p.Y) > 1e-6)
        return false;
    return true;
  }

  private static bool IsZeroBandList(IReadOnlyList<double>? values) {
    if (values is null || values.Count == 0)
      return true;
    foreach (var v in values)
      if (Math.Abs(v) > 1e-6)
        return false;
    return true;
  }
}

/// <summary>
/// The eight HSL adjustment bands Adobe uses (Red / Orange / Yellow / Green
/// / Aqua / Blue / Purple / Magenta). Centre hues are evenly distributed
/// around the wheel; pixels get weighted contributions from the two
/// nearest bands so adjacent-band tweaks blend smoothly.
/// </summary>
public enum HslBand {
  Red = 0,
  Orange = 1,
  Yellow = 2,
  Green = 3,
  Aqua = 4,
  Blue = 5,
  Purple = 6,
  Magenta = 7
}

/// <summary>A point on the tone curve. Both axes are 0..1.</summary>
public readonly record struct CurvePoint(double X, double Y);

/// <summary>How the tone curve is interpolated between user-placed points.</summary>
public enum CurveInterpolation {
  /// <summary>Piecewise-linear segments. Cheap, predictable, can produce visible kinks.</summary>
  Linear,
  /// <summary>Catmull-Rom spline through every control point — smooth, no overshoot at the anchors.</summary>
  CatmullRom,
  /// <summary>Cubic Bezier with auto-derived tangents (Catmull-Rom-style). Slightly looser than CatmullRom; can overshoot in steep regions.</summary>
  Bezier
}
