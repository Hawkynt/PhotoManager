using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Imaging;

/// <summary>
/// Reinhard global + local tone mapping (Reinhard, Stark, Shirley, Ferwerda;
/// SIGGRAPH 2002). Compresses an image's dynamic range while preserving
/// local contrast — useful for recovering blown highlights AND crushed
/// shadows in the same shot. Phone HDR exports are an obvious fit, but it
/// also rescues "harsh sunny day" snapshots where neither end is usable.
///
/// Pipeline (per pixel):
///   1. Convert RGB to luminance.
///   2. Compute the geometric mean of luminance ("log-average L").
///   3. Scale each pixel's L by the key value K (0.18 ≈ middle grey).
///   4. Apply Reinhard's mapping: L' = L / (1 + L), then optionally L' = L * (1 + L / Lwhite^2) / (1 + L) which preserves max-white.
///   5. Multiply RGB by L' / L to keep colour ratios.
///
/// Pure C#, no model. ~O(W × H) with one pre-pass for the log-average.
/// </summary>
public static class ReinhardToneMap {
  public const double DefaultKey = 0.18;
  public const double DefaultWhite = 1.0;

  /// <summary>Apply Reinhard tone mapping and return a freshly allocated image. Source not mutated.</summary>
  /// <param name="key">Target mean luminance after mapping. 0.18 (middle grey) is the photographer's default.</param>
  /// <param name="whitePoint">Luminance value (post-scaling) that should map to pure white. Higher = more compression of highlights.</param>
  public static Image<Rgba32> Apply(Image<Rgba32> source, double key = DefaultKey, double whitePoint = DefaultWhite) {
    ArgumentNullException.ThrowIfNull(source);
    key = Math.Clamp(key, 0.01, 1.0);
    whitePoint = Math.Max(0.1, whitePoint);

    var w = source.Width;
    var h = source.Height;
    var src = new Rgba32[w * h];
    source.CopyPixelDataTo(src);

    // 1. Log-average luminance (per Reinhard's paper, in [0,1] linear space).
    //    The epsilon avoids log(0) on pure-black pixels.
    var n = src.Length;
    double logSum = 0;
    var lum = new double[n];
    for (var i = 0; i < n; i++) {
      var p = src[i];
      var l = (0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B) / 255.0;
      lum[i] = l;
      logSum += Math.Log(l + 1e-4);
    }
    var logAvg = Math.Exp(logSum / n);

    // 2. Scale each pixel's luminance toward the key.
    var scale = key / Math.Max(1e-6, logAvg);
    var whiteSq = whitePoint * whitePoint;
    var dst = new Rgba32[n];
    for (var i = 0; i < n; i++) {
      var l = lum[i] * scale;
      // Modified Reinhard mapping: L' = L * (1 + L / Lw^2) / (1 + L).
      var lMapped = l * (1.0 + l / whiteSq) / (1.0 + l);
      var ratio = lum[i] > 1e-6 ? lMapped / lum[i] : 0.0;
      var p = src[i];
      dst[i] = new Rgba32(
        ClampByte(p.R * ratio),
        ClampByte(p.G * ratio),
        ClampByte(p.B * ratio),
        p.A);
    }

    var output = new Image<Rgba32>(w, h);
    output.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++)
        dst.AsSpan(y * w, w).CopyTo(accessor.GetRowSpan(y));
    });
    return output;
  }

  private static byte ClampByte(double v) {
    if (v <= 0) return 0;
    if (v >= 255) return 255;
    return (byte)Math.Round(v);
  }
}
