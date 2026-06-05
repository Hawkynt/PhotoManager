namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// A named film-simulation preset that maps to a pre-configured
/// <see cref="DevelopSettings"/>. Selecting a preset overlays its
/// characteristic look (contrast, saturation, colour grading, tone
/// curve) on top of the user's current exposure / white-balance
/// settings via <c>with</c> expressions on the record.
/// </summary>
public sealed record FilmPreset(string Name, string Description, DevelopSettings Adjustments) {
  /// <summary>Registry of all built-in film presets.</summary>
  public static IReadOnlyList<FilmPreset> All { get; } = BuildAll();

  private static List<FilmPreset> BuildAll() => [
    // ---- Velvia 50 ----
    // Fuji's legendary slide film: punchy, vivid, contrasty. Greens
    // and blues pop, shadows go warm, slight red push.
    new("Velvia 50",
      "High saturation, deep contrast, vivid greens/blues, warm shadows",
      new DevelopSettings(
        ContrastPercent: 30,
        SaturationPercent: 40,
        VibrancePercent: 25,
        TemperatureShift: 8,
        BlacksPercent: -15,
        ShadowsPercent: -10,
        HslSaturationShifts: [5, 10, 10, 30, 20, 25, 10, 5],   // boost G/Aqua/Blue
        HslLuminanceShifts:  [0, 0, 5, -10, -5, -10, 0, 0],    // deepen G/Blue
        RedGain: 8,
        GrainAmount: 8,
        GrainSize: 15,
        GrainFrequency: 40
      )),

    // ---- Kodachrome 64 ----
    // Warm midtones, muted greens, punchy reds/yellows, moderate
    // contrast. The "golden hour all day" film.
    new("Kodachrome 64",
      "Warm midtones, muted greens, strong reds/yellows, moderate contrast",
      new DevelopSettings(
        ContrastPercent: 20,
        SaturationPercent: 15,
        VibrancePercent: 20,
        TemperatureShift: 15,
        TintShift: 5,
        HslSaturationShifts: [25, 20, 25, -20, -10, 5, 10, 5], // reds/yellows up, greens down
        HslHueShifts:        [5, 5, 0, 10, 0, 0, 0, 0],        // nudge greens toward yellow
        ShadowsPercent: 5,
        BlacksPercent: -10,
        GrainAmount: 10,
        GrainSize: 18,
        GrainFrequency: 45
      )),

    // ---- Portra 400 ----
    // Kodak's portrait workhorse: low contrast, warm skin tones,
    // muted shadows, desaturated highlights. Forgiving, flattering.
    new("Portra 400",
      "Low contrast, warm skin tones, muted shadows, desaturated highlights",
      new DevelopSettings(
        ContrastPercent: -10,
        SaturationPercent: -5,
        VibrancePercent: 15,
        TemperatureShift: 10,
        ShadowsPercent: 15,
        HighlightsPercent: -15,
        BlacksPercent: 10,
        HslSaturationShifts: [10, 15, 5, -10, -5, -10, 5, 10], // warm hues up, cool down
        HslLuminanceShifts:  [5, 10, 5, 0, 0, 0, 0, 5],        // lift skin tones
        GrainAmount: 12,
        GrainSize: 20,
        GrainFrequency: 50
      )),

    // ---- Tri-X 400 ----
    // Kodak's iconic B&W: high contrast, deep blacks, visible grain.
    // Gray mixer weighted to classic panchromatic sensitivity.
    new("Tri-X 400",
      "B&W with high contrast, deep blacks, enhanced grain",
      new DevelopSettings(
        ConvertToGrayscale: true,
        ContrastPercent: 35,
        BlacksPercent: -25,
        WhitesPercent: 15,
        ShadowsPercent: -10,
        ClarityPercent: 20,
        GrayMixerRed: 15,
        GrayMixerOrange: 25,
        GrayMixerYellow: 30,
        GrayMixerGreen: 10,
        GrayMixerAqua: -5,
        GrayMixerBlue: -15,
        GrayMixerPurple: -10,
        GrayMixerMagenta: 5,
        GrainAmount: 30,
        GrainSize: 25,
        GrainFrequency: 60
      )),

    // ---- Cinematic Teal-Orange ----
    // Hollywood colour-grade staple: teal shadows, orange highlights,
    // lifted blacks, desaturated midtones.
    new("Cinematic Teal-Orange",
      "Split-tone teal shadows / orange highlights, lifted blacks, desaturated midtones",
      new DevelopSettings(
        ContrastPercent: 15,
        SaturationPercent: -15,
        VibrancePercent: 10,
        BlacksPercent: 15,
        ShadowsPercent: 10,
        SplitToningShadowHue: 195,         // teal
        SplitToningShadowSaturation: 40,
        SplitToningHighlightHue: 30,       // orange
        SplitToningHighlightSaturation: 35,
        SplitToningBalance: -15,
        HslSaturationShifts: [-10, 15, 10, -15, 15, 10, -10, -10],
        VignetteAmount: -25,
        VignetteMidpoint: 40,
        VignetteFeather: 60
      )),

    // ---- Cross-Process ----
    // Green shadows, yellow highlights, high saturation, high contrast.
    // The "wrong chemistry" darkroom accident turned intentional style.
    new("Cross-Process",
      "Green shadows, yellow highlights, high saturation, high contrast",
      new DevelopSettings(
        ContrastPercent: 30,
        SaturationPercent: 30,
        VibrancePercent: 15,
        GreenGain: 15,
        BlueGain: -20,
        SplitToningShadowHue: 120,         // green shadows
        SplitToningShadowSaturation: 35,
        SplitToningHighlightHue: 50,       // yellow-ish highlights
        SplitToningHighlightSaturation: 30,
        HslHueShifts:        [10, 0, -10, -15, 10, 15, 5, 0],
        HslSaturationShifts: [10, 15, 20, 15, -5, -10, 5, 10],
        BlacksPercent: -10
      )),

    // ---- Faded Vintage ----
    // Lifted blacks, desaturated, warm tone, low contrast. The "old
    // Polaroid left in a drawer" aesthetic.
    new("Faded Vintage",
      "Lifted blacks, desaturated, warm tone, low contrast",
      new DevelopSettings(
        ContrastPercent: -15,
        SaturationPercent: -20,
        VibrancePercent: -10,
        TemperatureShift: 12,
        TintShift: 5,
        BlacksPercent: 15,
        ShadowsPercent: 10,
        HighlightsPercent: -10,
        WhitesPercent: -5,
        GrainAmount: 15,
        GrainSize: 22,
        GrainFrequency: 55
      )),

    // ---- Infrared B&W ----
    // B&W with red channel boosted, greens brightened, blue crushed.
    // Simulates infrared film where foliage glows white and skies go
    // black.
    new("Infrared B&W",
      "B&W with boosted reds, bright greens, crushed blues (infrared look)",
      new DevelopSettings(
        ConvertToGrayscale: true,
        ContrastPercent: 25,
        BlacksPercent: -20,
        WhitesPercent: 20,
        ClarityPercent: 15,
        GrayMixerRed: 60,
        GrayMixerOrange: 40,
        GrayMixerYellow: 30,
        GrayMixerGreen: 70,
        GrayMixerAqua: -20,
        GrayMixerBlue: -60,
        GrayMixerPurple: -40,
        GrayMixerMagenta: 10,
        GrainAmount: 20,
        GrainSize: 20,
        GrainFrequency: 50
      )),
  ];

  /// <summary>
  /// Merge a film preset's adjustments onto a user's base settings.
  /// User exposure, white balance, crop, rotation, AI, LUT, and local
  /// adjustments are preserved; the preset's tone / colour / effects
  /// overlay on top.
  /// </summary>
  public static DevelopSettings MergeOnto(DevelopSettings userBase, DevelopSettings preset) =>
    userBase with {
      // Tone — additive
      ContrastPercent   = Clamp(userBase.ContrastPercent   + preset.ContrastPercent,   -100, 100),
      HighlightsPercent = Clamp(userBase.HighlightsPercent + preset.HighlightsPercent, -100, 100),
      ShadowsPercent    = Clamp(userBase.ShadowsPercent    + preset.ShadowsPercent,    -100, 100),
      WhitesPercent     = Clamp(userBase.WhitesPercent     + preset.WhitesPercent,     -100, 100),
      BlacksPercent     = Clamp(userBase.BlacksPercent     + preset.BlacksPercent,     -100, 100),

      // Presence — additive
      SaturationPercent = Clamp(userBase.SaturationPercent + preset.SaturationPercent, -100, 100),
      VibrancePercent   = Clamp(userBase.VibrancePercent   + preset.VibrancePercent,   -100, 100),
      ClarityPercent    = Clamp(userBase.ClarityPercent    + preset.ClarityPercent,    -100, 100),

      // Colour channels — additive
      RedGain   = Clamp(userBase.RedGain   + preset.RedGain,   -100, 100),
      GreenGain = Clamp(userBase.GreenGain + preset.GreenGain, -100, 100),
      BlueGain  = Clamp(userBase.BlueGain  + preset.BlueGain,  -100, 100),

      // Split toning — preset wins (overlay, not additive)
      SplitToningShadowHue              = preset.SplitToningShadowSaturation > 0 ? preset.SplitToningShadowHue              : userBase.SplitToningShadowHue,
      SplitToningShadowSaturation       = preset.SplitToningShadowSaturation > 0 ? preset.SplitToningShadowSaturation       : userBase.SplitToningShadowSaturation,
      SplitToningHighlightHue           = preset.SplitToningHighlightSaturation > 0 ? preset.SplitToningHighlightHue        : userBase.SplitToningHighlightHue,
      SplitToningHighlightSaturation    = preset.SplitToningHighlightSaturation > 0 ? preset.SplitToningHighlightSaturation : userBase.SplitToningHighlightSaturation,
      SplitToningBalance                = (preset.SplitToningShadowSaturation > 0 || preset.SplitToningHighlightSaturation > 0)
                                            ? preset.SplitToningBalance : userBase.SplitToningBalance,

      // HSL — additive per band
      HslHueShifts        = MergeBandLists(userBase.HslHueShifts,        preset.HslHueShifts,        -100, 100),
      HslSaturationShifts = MergeBandLists(userBase.HslSaturationShifts, preset.HslSaturationShifts, -100, 100),
      HslLuminanceShifts  = MergeBandLists(userBase.HslLuminanceShifts,  preset.HslLuminanceShifts,  -100, 100),

      // B&W — preset wins when it converts to grayscale
      ConvertToGrayscale = preset.ConvertToGrayscale || userBase.ConvertToGrayscale,
      GrayMixerRed     = preset.ConvertToGrayscale ? preset.GrayMixerRed     : userBase.GrayMixerRed,
      GrayMixerOrange  = preset.ConvertToGrayscale ? preset.GrayMixerOrange  : userBase.GrayMixerOrange,
      GrayMixerYellow  = preset.ConvertToGrayscale ? preset.GrayMixerYellow  : userBase.GrayMixerYellow,
      GrayMixerGreen   = preset.ConvertToGrayscale ? preset.GrayMixerGreen   : userBase.GrayMixerGreen,
      GrayMixerAqua    = preset.ConvertToGrayscale ? preset.GrayMixerAqua    : userBase.GrayMixerAqua,
      GrayMixerBlue    = preset.ConvertToGrayscale ? preset.GrayMixerBlue    : userBase.GrayMixerBlue,
      GrayMixerPurple  = preset.ConvertToGrayscale ? preset.GrayMixerPurple  : userBase.GrayMixerPurple,
      GrayMixerMagenta = preset.ConvertToGrayscale ? preset.GrayMixerMagenta : userBase.GrayMixerMagenta,

      // Effects — preset wins (these are stylistic, not corrective)
      VignetteAmount   = preset.VignetteAmount   != 0 ? preset.VignetteAmount   : userBase.VignetteAmount,
      VignetteMidpoint = preset.VignetteAmount   != 0 ? preset.VignetteMidpoint : userBase.VignetteMidpoint,
      VignetteFeather  = preset.VignetteAmount   != 0 ? preset.VignetteFeather  : userBase.VignetteFeather,
      GrainAmount    = preset.GrainAmount    != 0 ? preset.GrainAmount    : userBase.GrainAmount,
      GrainSize      = preset.GrainAmount    != 0 ? preset.GrainSize      : userBase.GrainSize,
      GrainFrequency = preset.GrainAmount    != 0 ? preset.GrainFrequency : userBase.GrainFrequency,
    };

  private static double Clamp(double value, double min, double max) =>
    value < min ? min : value > max ? max : value;

  private static IReadOnlyList<double>? MergeBandLists(
      IReadOnlyList<double>? userBands,
      IReadOnlyList<double>? presetBands,
      double min, double max) {
    if (presetBands is null || presetBands.Count == 0)
      return userBands;
    if (userBands is null || userBands.Count == 0)
      return presetBands;
    var count = Math.Max(userBands.Count, presetBands.Count);
    var result = new double[count];
    for (var i = 0; i < count; i++) {
      var u = i < userBands.Count   ? userBands[i]   : 0;
      var p = i < presetBands.Count ? presetBands[i] : 0;
      result[i] = Clamp(u + p, min, max);
    }
    return result;
  }
}
