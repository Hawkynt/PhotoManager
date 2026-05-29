using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Enhance;

/// <summary>
/// Heuristic detectors for the common "smartphone shot" problems Magic
/// Enhance has to decide whether to fix. Each one returns a 0..1 score
/// (1 = problem definitely present). The orchestrator thresholds these
/// to pick which restoration stages to run — we deliberately do NOT run
/// every stage on every image because each ML stage trades cycles for
/// quality and Real-ESRGAN on a 12-megapixel shot that's already sharp
/// just wastes compute and risks artefacts.
///
/// All detectors are pure C# and operate on a small thumbnail (downscaled
/// to ~256 px on the long axis) so they stay fast on full-res sources.
/// </summary>
public static class PhotoIssueDetector {
  /// <summary>Default working size for detection downscales. 256 keeps it cheap.</summary>
  public const int WorkingSize = 256;

  /// <summary>
  /// 0 = scene is well-exposed (mean luminance ≥ 90), 1 = severely
  /// underexposed (mean ≤ 40). Linear ramp between the two endpoints.
  /// </summary>
  public static double LowLightScore(Image<Rgba32> source) {
    ArgumentNullException.ThrowIfNull(source);
    var mean = MeanLuminance(Downscale(source));
    if (mean >= 90) return 0;
    if (mean <= 40) return 1;
    return (90 - mean) / 50.0;
  }

  /// <summary>
  /// 0 = clean, 1 = heavy noise. Estimated via the standard "Laplacian
  /// of variance on flat patches" trick: divide the image into tiles,
  /// take the patch with the LOWEST gradient activity (assumed flat),
  /// measure its high-frequency residual via a 3-tap Laplacian. High
  /// residual on a flat patch = noise.
  /// </summary>
  public static double NoiseScore(Image<Rgba32> source) {
    ArgumentNullException.ThrowIfNull(source);
    using var small = Downscale(source);
    var w = small.Width;
    var h = small.Height;
    var lum = ExtractLuminance(small);

    // Find the flattest 32×32 tile.
    var bestVariance = double.MaxValue;
    var bestX = 0;
    var bestY = 0;
    for (var ty = 0; ty + 32 <= h; ty += 16) {
      for (var tx = 0; tx + 32 <= w; tx += 16) {
        var variance = TileVariance(lum, w, tx, ty, 32, 32);
        if (variance < bestVariance) {
          bestVariance = variance;
          bestX = tx;
          bestY = ty;
        }
      }
    }

    // Laplacian residual on that tile.
    double residual = 0;
    var count = 0;
    for (var y = bestY + 1; y < bestY + 31; y++) {
      var rowOff = y * w;
      for (var x = bestX + 1; x < bestX + 31; x++) {
        var c = lum[rowOff + x];
        var lap = -4 * c + lum[rowOff + x - 1] + lum[rowOff + x + 1]
                  + lum[rowOff - w + x] + lum[rowOff + w + x];
        residual += lap * lap;
        count++;
      }
    }
    var noiseEnergy = Math.Sqrt(residual / Math.Max(1, count));
    // Empirical mapping: <8 = clean, >30 = heavy noise.
    if (noiseEnergy <= 8) return 0;
    if (noiseEnergy >= 30) return 1;
    return (noiseEnergy - 8) / 22.0;
  }

  /// <summary>
  /// 0 = clear, 1 = heavily hazy. The atmospheric scattering signature
  /// is two-fold: (a) overall RGB histogram is compressed into a narrow
  /// mid-grey band and (b) the dark channel (per-pixel min RGB) is
  /// elevated globally — clean scenes have a dark-channel minimum near
  /// 0, hazy scenes have a dark-channel minimum well above 0.
  /// </summary>
  public static double HazeScore(Image<Rgba32> source) {
    ArgumentNullException.ThrowIfNull(source);
    using var small = Downscale(source);
    long darkChannelSum = 0;
    long n = 0;
    byte darkMin = 255;
    small.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var p = row[x];
          var dc = Math.Min(p.R, Math.Min(p.G, p.B));
          darkChannelSum += dc;
          if (dc < darkMin) darkMin = dc;
          n++;
        }
      }
    });
    if (n == 0) return 0;
    var darkChannelMean = darkChannelSum / (double)n;
    // Clean scenes: dark-channel mean << 50. Hazy: > 100.
    if (darkChannelMean <= 50) return 0;
    if (darkChannelMean >= 130) return 1;
    return (darkChannelMean - 50) / 80.0;
  }

  /// <summary>0 = high resolution (≥ 8 MP), 1 = very low (≤ 0.5 MP).</summary>
  public static double LowResolutionScore(Image<Rgba32> source) {
    ArgumentNullException.ThrowIfNull(source);
    var mp = (source.Width * source.Height) / 1_000_000.0;
    if (mp >= 8) return 0;
    if (mp <= 0.5) return 1;
    return (8 - mp) / 7.5;
  }

  // ---------- helpers ----------

  private static Image<Rgba32> Downscale(Image<Rgba32> source) {
    var scale = (double)WorkingSize / Math.Max(source.Width, source.Height);
    if (scale >= 1.0)
      return source.Clone();
    var w = Math.Max(8, (int)Math.Round(source.Width * scale));
    var h = Math.Max(8, (int)Math.Round(source.Height * scale));
    return source.Clone(c => c.Resize(w, h));
  }

  private static double MeanLuminance(Image<Rgba32> img) {
    long sum = 0;
    long n = 0;
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var p = row[x];
          sum += (77 * p.R + 150 * p.G + 29 * p.B) >> 8;
          n++;
        }
      }
    });
    img.Dispose();
    return n == 0 ? 0 : sum / (double)n;
  }

  private static byte[] ExtractLuminance(Image<Rgba32> img) {
    var w = img.Width;
    var h = img.Height;
    var lum = new byte[w * h];
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        var rowOff = y * w;
        for (var x = 0; x < w; x++) {
          var p = row[x];
          lum[rowOff + x] = (byte)((77 * p.R + 150 * p.G + 29 * p.B) >> 8);
        }
      }
    });
    return lum;
  }

  private static double TileVariance(byte[] lum, int width, int tx, int ty, int tw, int th) {
    long sum = 0, sumSq = 0;
    var n = 0;
    for (var y = ty; y < ty + th; y++) {
      var rowOff = y * width;
      for (var x = tx; x < tx + tw; x++) {
        var v = lum[rowOff + x];
        sum += v;
        sumSq += v * v;
        n++;
      }
    }
    if (n == 0) return 0;
    var mean = sum / (double)n;
    return sumSq / (double)n - mean * mean;
  }
}
