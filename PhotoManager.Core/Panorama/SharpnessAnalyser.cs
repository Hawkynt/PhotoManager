using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Panorama;

/// <summary>
/// Builds a per-pixel sharpness mask (0 = blurry / uninformative, 255 = locally
/// sharp) for a single panorama frame. Driven by Laplacian-of-luminance variance
/// per non-overlapping patch with an adaptive (median-relative) threshold so
/// dim or low-contrast scenes don't get masked out wholesale.
///
/// <para>The mask is consumed by the panorama stitchers: OpenCV stitchers feed
/// it to <c>cv::Stitcher::stitch(images, masks, ...)</c> as an inclusion mask;
/// the cylindrical/tripod stitcher uses it to weight the linear feathering so
/// blurry pixels in one frame don't bleed across the seam onto sharp pixels
/// from its neighbour.</para>
/// </summary>
public static class SharpnessAnalyser {
  /// <summary>
  /// Build a 0..255 mask where 255 = locally sharp, 0 = blurry / uniform /
  /// obstructed. <paramref name="patchSize"/> is the side length of the
  /// non-overlapping square patches the variance is computed over (defaults
  /// to 32 — a sane balance between locality and sample size for a typical
  /// 12-24 MP frame). <paramref name="minVarianceFraction"/> is the cutoff
  /// fraction relative to the median patch variance: patches with variance
  /// below median*fraction are zero, everything else is 255. Adaptive so a
  /// dim scene with low absolute variance still produces sharp regions.
  /// </summary>
  public static Image<L8> BuildPatchMask(
      Image<Rgba32> source,
      int patchSize = 32,
      double minVarianceFraction = 0.25) {
    ArgumentNullException.ThrowIfNull(source);
    if (patchSize < 2)
      throw new ArgumentOutOfRangeException(nameof(patchSize), patchSize, "patchSize must be >= 2");
    if (minVarianceFraction < 0 || double.IsNaN(minVarianceFraction))
      throw new ArgumentOutOfRangeException(nameof(minVarianceFraction), minVarianceFraction, "minVarianceFraction must be >= 0");

    var width = source.Width;
    var height = source.Height;
    var luma = ToLuma(source);

    var patchesX = (width + patchSize - 1) / patchSize;
    var patchesY = (height + patchSize - 1) / patchSize;
    var variances = new double[patchesY * patchesX];

    for (var py = 0; py < patchesY; py++) {
      var y0 = py * patchSize;
      var y1 = Math.Min(y0 + patchSize, height);
      for (var px = 0; px < patchesX; px++) {
        var x0 = px * patchSize;
        var x1 = Math.Min(x0 + patchSize, width);
        variances[py * patchesX + px] = LaplacianVariance(luma, width, height, x0, y0, x1, y1);
      }
    }

    var threshold = ComputeAdaptiveThreshold(variances, minVarianceFraction);

    var rawMask = new byte[patchesY * patchesX];
    for (var i = 0; i < variances.Length; i++)
      rawMask[i] = variances[i] >= threshold ? (byte)255 : (byte)0;

    var dilated = DilatePatchMask(rawMask, patchesX, patchesY);

    return ToTiledImage(dilated, patchesX, patchesY, patchSize, width, height);
  }

  private static byte[] ToLuma(Image<Rgba32> source) {
    var width = source.Width;
    var height = source.Height;
    var luma = new byte[width * height];
    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var dst = y * width;
        for (var x = 0; x < row.Length; x++) {
          var p = row[x];
          var l = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
          if (l < 0) l = 0;
          if (l > 255) l = 255;
          luma[dst + x] = (byte)l;
        }
      }
    });
    return luma;
  }

  /// <summary>
  /// Variance of the 3x3 Laplacian response inside the rect [x0,y0)-(x1,y1).
  /// Border pixels of the patch that touch the image edge are skipped to
  /// avoid bogus high responses from undefined neighbours.
  /// </summary>
  private static double LaplacianVariance(byte[] luma, int width, int height, int x0, int y0, int x1, int y1) {
    var ix0 = Math.Max(1, x0);
    var iy0 = Math.Max(1, y0);
    var ix1 = Math.Min(width - 1, x1);
    var iy1 = Math.Min(height - 1, y1);
    if (ix1 <= ix0 || iy1 <= iy0)
      return 0;

    long sum = 0;
    long sumSq = 0;
    long count = 0;
    for (var y = iy0; y < iy1; y++) {
      var rowAbove = (y - 1) * width;
      var rowMid = y * width;
      var rowBelow = (y + 1) * width;
      for (var x = ix0; x < ix1; x++) {
        var lap = luma[rowMid + x - 1]
                + luma[rowMid + x + 1]
                + luma[rowAbove + x]
                + luma[rowBelow + x]
                - 4 * luma[rowMid + x];
        sum += lap;
        sumSq += (long)lap * lap;
        count++;
      }
    }
    if (count == 0)
      return 0;
    var mean = sum / (double)count;
    var meanSq = sumSq / (double)count;
    return meanSq - mean * mean;
  }

  /// <summary>
  /// Median patch variance × <paramref name="fraction"/>, with two
  /// fallbacks: when the median is zero but some patches do have variance
  /// (small bright detail on a uniform background) we still want those
  /// detail patches kept, so we drop down to the smallest non-zero variance
  /// as the threshold. When the whole image is genuinely uniform (every
  /// patch variance is zero) the threshold is +∞ so nothing is marked
  /// sharp.
  /// </summary>
  private static double ComputeAdaptiveThreshold(double[] variances, double fraction) {
    if (variances.Length == 0)
      return double.PositiveInfinity;
    var sorted = (double[])variances.Clone();
    Array.Sort(sorted);
    var median = sorted[sorted.Length / 2];
    var threshold = median * fraction;
    if (threshold > 0)
      return threshold;
    foreach (var v in sorted) {
      if (v > 0)
        return v;
    }
    return double.PositiveInfinity;
  }

  /// <summary>
  /// One-pass 3x3 max filter over the per-patch decisions, so the final mask
  /// boundaries are softened by one patch tile and we don't get rectangular
  /// artefacts at every patch seam.
  /// </summary>
  private static byte[] DilatePatchMask(byte[] mask, int width, int height) {
    var result = new byte[mask.Length];
    for (var y = 0; y < height; y++) {
      for (var x = 0; x < width; x++) {
        byte best = 0;
        var y0 = Math.Max(0, y - 1);
        var y1 = Math.Min(height - 1, y + 1);
        var x0 = Math.Max(0, x - 1);
        var x1 = Math.Min(width - 1, x + 1);
        for (var ny = y0; ny <= y1; ny++) {
          for (var nx = x0; nx <= x1; nx++) {
            var v = mask[ny * width + nx];
            if (v > best)
              best = v;
          }
        }
        result[y * width + x] = best;
      }
    }
    return result;
  }

  private static Image<L8> ToTiledImage(byte[] patchMask, int patchesX, int patchesY, int patchSize, int width, int height) {
    var image = new Image<L8>(width, height);
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var py = Math.Min(y / patchSize, patchesY - 1);
        var rowBase = py * patchesX;
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var px = Math.Min(x / patchSize, patchesX - 1);
          row[x] = new L8(patchMask[rowBase + px]);
        }
      }
    });
    return image;
  }
}
