using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Develop;

/// <summary>
/// One-click derivations of <see cref="DevelopSettings"/> from image
/// statistics. Each method takes the current settings and returns an
/// updated copy — never overwrites unrelated fields, so the user can
/// stack Auto-Tone + Auto-WB without one wiping the other.
/// </summary>
public static class AutoDeveloper {
  /// <summary>
  /// "Make it look better" tone preset. Maps the histogram's 0.5 / 99.5
  /// percentiles onto Whites/Blacks (clip the rare extremes), then lifts
  /// shadows / pulls highlights based on how compressed each end is.
  /// </summary>
  public static DevelopSettings AutoTone(DevelopSettings current, ImageHistogram histogram) {
    ArgumentNullException.ThrowIfNull(histogram);

    var blackPoint = ImageHistogram.Percentile(histogram.Luminance, histogram.TotalPixels, 0.005);
    var whitePoint = ImageHistogram.Percentile(histogram.Luminance, histogram.TotalPixels, 0.995);

    // Whites / Blacks lift extremes — strength is "how far from full
    // range we currently are" mapped to ±50 of the slider scale.
    var blacksLift = (-blackPoint / 255.0) * 100;     // negative if underused
    var whitesLift = ((255 - whitePoint) / 255.0) * 100;  // positive if underused

    // Shadow recovery: if the 5th percentile is still very dark (<40), lift.
    var shadowsLift = (40 - Math.Min(40, ImageHistogram.Percentile(histogram.Luminance, histogram.TotalPixels, 0.05))) * 1.5;

    // Highlight recovery: if the 95th percentile is very bright (>215), pull.
    var highlightsPull = -(Math.Max(215, ImageHistogram.Percentile(histogram.Luminance, histogram.TotalPixels, 0.95)) - 215) * 1.5;

    return current with {
      WhitesPercent = Clamp(whitesLift, -100, 100),
      BlacksPercent = Clamp(blacksLift, -100, 100),
      ShadowsPercent = Clamp(shadowsLift, -100, 100),
      HighlightsPercent = Clamp(highlightsPull, -100, 100)
    };
  }

  /// <summary>
  /// Grey-world auto white balance. Pushes the average channel ratios
  /// toward 1:1:1 by deriving Temperature (red↔blue) and Tint (green vs
  /// the others) shifts. Cheap, no scene-classification.
  /// </summary>
  public static DevelopSettings AutoWhiteBalance(DevelopSettings current, ImageHistogram histogram) {
    ArgumentNullException.ThrowIfNull(histogram);
    var rAvg = ChannelAverage(histogram.Red);
    var gAvg = ChannelAverage(histogram.Green);
    var bAvg = ChannelAverage(histogram.Blue);
    var globalAvg = (rAvg + gAvg + bAvg) / 3.0;
    if (globalAvg <= 0)
      return current;

    // Temperature: positive when the image is too blue (blue avg > red avg).
    var tempShift = (bAvg - rAvg) / globalAvg * 50.0;
    // Tint: positive when image is too green (G dominates). Pull toward magenta.
    var tintShift = -(gAvg - (rAvg + bAvg) / 2.0) / globalAvg * 50.0;

    return current with {
      TemperatureShift = Clamp(tempShift, -100, 100),
      TintShift = Clamp(tintShift, -100, 100)
    };
  }

  /// <summary>
  /// Per-channel auto stretch. For each of R / G / B, find the value at
  /// which 99.5% of the pixels are darker, and derive a gain that pushes
  /// that point up to 255. Result: each channel uses its full dynamic
  /// range, which corrects subtle colour casts AND lifts overall contrast
  /// in one move.
  /// </summary>
  public static DevelopSettings AutoChannelStretch(DevelopSettings current, ImageHistogram histogram) {
    ArgumentNullException.ThrowIfNull(histogram);

    static double GainFor(int[] channel, int total) {
      var top = ImageHistogram.Percentile(channel, total, 0.995);
      if (top <= 0) return 0;
      // gain (-100..+100) maps to multiplier 0..2 ; we want top → 255,
      // i.e. multiplier = 255/top → gain = (multiplier - 1) * 100.
      var mult = 255.0 / top;
      return Math.Clamp((mult - 1) * 100, -100, 100);
    }

    return current with {
      RedGain   = GainFor(histogram.Red,   histogram.TotalPixels),
      GreenGain = GainFor(histogram.Green, histogram.TotalPixels),
      BlueGain  = GainFor(histogram.Blue,  histogram.TotalPixels)
    };
  }

  /// <summary>Click a pixel that should saturate the red channel.</summary>
  public static DevelopSettings PickRedPoint(DevelopSettings current, byte r, byte g, byte b) {
    if (r == 0) return current;
    var mult = 255.0 / r;
    return current with { RedGain = Math.Clamp((mult - 1) * 100, -100, 100) };
  }

  public static DevelopSettings PickGreenPoint(DevelopSettings current, byte r, byte g, byte b) {
    if (g == 0) return current;
    var mult = 255.0 / g;
    return current with { GreenGain = Math.Clamp((mult - 1) * 100, -100, 100) };
  }

  public static DevelopSettings PickBluePoint(DevelopSettings current, byte r, byte g, byte b) {
    if (b == 0) return current;
    var mult = 255.0 / b;
    return current with { BlueGain = Math.Clamp((mult - 1) * 100, -100, 100) };
  }

  /// <summary>
  /// User clicked a pixel they want to be pure black. Compute the
  /// exposure / blacks shift that would map that pixel to (0,0,0).
  /// Conservative — only adjusts Blacks, leaves white balance alone.
  /// </summary>
  public static DevelopSettings PickBlackPoint(DevelopSettings current, byte r, byte g, byte b) {
    var lum = 0.299 * r + 0.587 * g + 0.114 * b;
    // Aggressive: -100 means crush ~38% darker, the strongest the slider goes.
    var blacks = -lum / 255.0 * 100.0;
    return current with { BlacksPercent = Clamp(blacks, -100, 100) };
  }

  /// <summary>
  /// User clicked a pixel they want to be pure white. Lift Whites so
  /// that pixel saturates the channel scale.
  /// </summary>
  public static DevelopSettings PickWhitePoint(DevelopSettings current, byte r, byte g, byte b) {
    var lum = 0.299 * r + 0.587 * g + 0.114 * b;
    var whites = (255 - lum) / 255.0 * 100.0;
    return current with { WhitesPercent = Clamp(whites, -100, 100) };
  }

  /// <summary>
  /// User clicked a pixel they want to be neutral grey. Derive Temperature
  /// + Tint shifts so the channels equalise on that pixel — the standard
  /// grey-card eyedropper move from any real DAM.
  /// </summary>
  public static DevelopSettings PickGreyPoint(DevelopSettings current, byte r, byte g, byte b) {
    var avg = (r + g + b) / 3.0;
    if (avg <= 0)
      return current;
    var tempShift = (b - r) / avg * 50.0;
    var tintShift = -(g - (r + b) / 2.0) / avg * 50.0;
    return current with {
      TemperatureShift = Clamp(tempShift, -100, 100),
      TintShift = Clamp(tintShift, -100, 100)
    };
  }

  private static double ChannelAverage(int[] counts) {
    long sum = 0;
    long total = 0;
    for (var i = 0; i < counts.Length; i++) {
      sum += (long)counts[i] * i;
      total += counts[i];
    }
    return total == 0 ? 0 : (double)sum / total;
  }

  private static double Clamp(double v, double min, double max) => Math.Min(max, Math.Max(min, v));
}
