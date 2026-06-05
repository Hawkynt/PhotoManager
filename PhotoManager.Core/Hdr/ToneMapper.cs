using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Hdr;

public enum ToneMapOperator {
  Reinhard,
  Drago
}

/// <summary>
/// LDR projection of an HDR radiance map. Reinhard global compresses the
/// luminance L to L/(1+L) (with optional white-point clamp); Drago is the
/// adaptive logarithmic operator from Drago et al. 2003 with bias b
/// controlling shadow / highlight balance (default 0.85, range 0.7..0.9).
/// Both preserve chrominance: only Y is compressed, the C channels carry
/// through and are recombined and gamma-encoded for sRGB output.
/// </summary>
public static class ToneMapper {
  /// <summary>Float-radiance gray-channel for tests / single-channel checks.</summary>
  public static double ReinhardGlobal(double luminance, double whitePoint) {
    if (luminance <= 0)
      return 0;
    if (whitePoint <= 0)
      return luminance / (1 + luminance);
    var w2 = whitePoint * whitePoint;
    return (luminance * (1 + luminance / w2)) / (1 + luminance);
  }

  public static double DragoLogarithmic(double luminance, double maxLuminance, double bias) {
    if (luminance <= 0 || maxLuminance <= 0)
      return 0;
    var exponent = Math.Log(bias) / Math.Log(0.5);
    var ratio = luminance / maxLuminance;
    var num = Math.Log(luminance + 1.0);
    var denom = Math.Log(maxLuminance + 1.0) * Math.Log(2 + Math.Pow(ratio, exponent) * 8);
    return num / denom;
  }

  public static Image<Rgba32> Map(
    HdrRadianceMap radiance,
    ToneMapOperator op,
    double whitePoint = 0,
    double dragoBias = 0.85,
    double saturation = 1.0
  ) {
    var w = radiance.Width;
    var h = radiance.Height;
    var n = w * h;

    var lum = new double[n];
    var maxLum = 0.0;
    for (var i = 0; i < n; i++) {
      var l = 0.2126 * radiance.Red[i] + 0.7152 * radiance.Green[i] + 0.0722 * radiance.Blue[i];
      if (l < 0) l = 0;
      lum[i] = l;
      if (l > maxLum)
        maxLum = l;
    }

    var compressed = new double[n];
    if (op == ToneMapOperator.Drago) {
      var lMax = maxLum > 0 ? maxLum : 1.0;
      for (var i = 0; i < n; i++)
        compressed[i] = DragoLogarithmic(lum[i], lMax, dragoBias);
    } else {
      for (var i = 0; i < n; i++)
        compressed[i] = ReinhardGlobal(lum[i], whitePoint);
    }

    var image = new Image<Rgba32>(w, h);
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        var off = y * w;
        for (var x = 0; x < w; x++) {
          var i = off + x;
          var lOld = lum[i];
          var lNew = compressed[i];
          double r, g, b;
          if (lOld <= 1e-9) {
            r = g = b = lNew;
          } else {
            var scale = lNew / lOld;
            r = radiance.Red[i] * scale;
            g = radiance.Green[i] * scale;
            b = radiance.Blue[i] * scale;
          }

          if (saturation != 1.0) {
            var grey = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            r = grey + (r - grey) * saturation;
            g = grey + (g - grey) * saturation;
            b = grey + (b - grey) * saturation;
          }

          row[x] = new Rgba32(
            ToByte(GammaEncode(r)),
            ToByte(GammaEncode(g)),
            ToByte(GammaEncode(b)),
            (byte)255);
        }
      }
    });
    return image;
  }

  internal static double GammaEncode(double linear) {
    if (linear <= 0) return 0;
    if (linear >= 1) return 1;
    return linear <= 0.0031308
      ? 12.92 * linear
      : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
  }

  internal static byte ToByte(double v) {
    var s = v * 255.0;
    if (s < 0) return 0;
    if (s > 255) return 255;
    return (byte)Math.Round(s);
  }
}
