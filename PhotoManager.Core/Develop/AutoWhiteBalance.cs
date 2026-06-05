using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Pure pixel-level auto white balance estimation. Returns correction values
/// in the same range as <see cref="DevelopSettings.TemperatureShift"/> (-100..+100)
/// and <see cref="DevelopSettings.TintShift"/> (-100..+100).
///
/// Two strategies are combined:
/// 1. **Gray World** — the average R, G, B across the entire image should be
///    equal for a neutrally-lit scene. The deviation from R=G=B tells us the
///    colour cast direction and magnitude.
/// 2. **Brightest-patch refinement** — if a near-white (but not clipped) patch
///    exists, its RGB ratios are a stronger white-reference than the scene
///    average. When such a patch is found, its correction is blended in at 50 %
///    weight so the result is less sensitive to scene content while still
///    anchoring on real highlights.
/// </summary>
public static class AutoWhiteBalance {
  /// <summary>
  /// Analyse <paramref name="image"/> and return suggested
  /// (temperature, tint) corrections that would neutralise the detected
  /// colour cast. Both values are in [-100, +100].
  /// </summary>
  public static (double temperature, double tint) Estimate(Image<Rgba32> image, AutoAdjustOptions? options = null) {
    ArgumentNullException.ThrowIfNull(image);
    var o = options ?? new AutoAdjustOptions();

    var w = image.Width;
    var h = image.Height;
    if (w < 1 || h < 1)
      return (0, 0);

    // Single-pass: collect all pixel luminances + RGB sums for both
    // gray-world and highlight-patch strategies.
    var pixelCount = (long)w * h;
    var allLum = new byte[pixelCount];
    double sumR = 0, sumG = 0, sumB = 0;
    double hiR = 0, hiG = 0, hiB = 0;
    long hiCount = 0;

    image.ProcessPixelRows(accessor => {
      var idx = 0L;
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var px = row[x];
          sumR += px.R;
          sumG += px.G;
          sumB += px.B;
          var lum = (byte)((77 * px.R + 150 * px.G + 29 * px.B) >> 8);
          allLum[idx++] = lum;

          if (lum >= o.WbHighlightLumMin && lum <= o.WbHighlightLumMax
              && px.R < 255 && px.G < 255 && px.B < 255) {
            hiR += px.R;
            hiG += px.G;
            hiB += px.B;
            hiCount++;
          }
        }
      }
    });

    if (pixelCount == 0)
      return (0, 0);

    // Clip extreme percentiles before computing gray-world average so
    // noisy shadows / blown highlights don't skew the result.
    var lowCutoff = 0;
    var highCutoff = 255;
    if (o.WbClipLowPct > 0 || o.WbClipHighPct > 0) {
      var sorted = (byte[])allLum.Clone();
      Array.Sort(sorted);
      if (o.WbClipLowPct > 0)
        lowCutoff = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * o.WbClipLowPct))];
      if (o.WbClipHighPct > 0)
        highCutoff = sorted[Math.Max(0, (int)(sorted.Length * (1.0 - o.WbClipHighPct)))];
    }

    // Re-accumulate with clipping (only when clipping is active).
    if (lowCutoff > 0 || highCutoff < 255) {
      sumR = sumG = sumB = 0;
      long clippedCount = 0;
      image.ProcessPixelRows(accessor => {
        for (var y = 0; y < accessor.Height; y++) {
          var row = accessor.GetRowSpan(y);
          for (var x = 0; x < row.Length; x++) {
            var px = row[x];
            var lum = (77 * px.R + 150 * px.G + 29 * px.B) >> 8;
            if (lum < lowCutoff || lum > highCutoff) continue;
            sumR += px.R; sumG += px.G; sumB += px.B;
            clippedCount++;
          }
        }
      });
      pixelCount = clippedCount;
    }

    if (pixelCount == 0)
      return (0, 0);

    var avgR = sumR / pixelCount;
    var avgG = sumG / pixelCount;
    var avgB = sumB / pixelCount;
    var globalAvg = (avgR + avgG + avgB) / 3.0;
    if (globalAvg <= 0)
      return (0, 0);

    var gwTemp = (avgB - avgR) / globalAvg * o.WbTemperatureSensitivity;
    var gwTint = -(avgG - (avgR + avgB) / 2.0) / globalAvg * o.WbTintSensitivity;

    if (hiCount >= o.WbMinHighlightPixels) {
      var hAvgR = hiR / hiCount;
      var hAvgG = hiG / hiCount;
      var hAvgB = hiB / hiCount;
      var hGlobalAvg = (hAvgR + hAvgG + hAvgB) / 3.0;
      if (hGlobalAvg > 0) {
        var hiTemp = (hAvgB - hAvgR) / hGlobalAvg * o.WbTemperatureSensitivity;
        var hiTint = -(hAvgG - (hAvgR + hAvgB) / 2.0) / hGlobalAvg * o.WbTintSensitivity;
        var w1 = 1.0 - o.WbHighlightBlendWeight;
        gwTemp = gwTemp * w1 + hiTemp * o.WbHighlightBlendWeight;
        gwTint = gwTint * w1 + hiTint * o.WbHighlightBlendWeight;
      }
    }

    return (Math.Clamp(gwTemp, -100, 100), Math.Clamp(gwTint, -100, 100));
  }
}
