using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Imaging;

/// <summary>
/// Contrast Limited Adaptive Histogram Equalization on the luminance
/// channel (Y of YCbCr). The image is divided into a grid of tiles; each
/// tile builds its own luminance histogram, applies a clip-limit cap to
/// suppress noise amplification in flat regions, and remaps that tile's
/// Y values via the clipped CDF. Pixel-level remapping bilinearly
/// interpolates between the four neighbouring tile remappings so tile
/// boundaries don't show.
///
/// CLAHE is the classic "make a flat snapshot pop" tool — it adds local
/// contrast without globally crushing highlights / shadows, which a
/// straight histogram stretch always does. The clip limit keeps noise
/// from being amplified into visible grain in flat-coloured regions.
///
/// Pure C#, no model. ~O(W × H) per pixel + a small constant per tile.
/// </summary>
public static class Clahe {
  public const int DefaultTileCount = 8;
  public const int DefaultClipLimit = 4;

  /// <summary>Apply CLAHE and return a freshly allocated image. Source not mutated.</summary>
  /// <param name="tileCount">Grid is tileCount × tileCount tiles. 8 is the OpenCV default; 4 gives broader regions, 16 more local.</param>
  /// <param name="clipLimit">Per-bin histogram cap multiplier (vs the per-tile mean). 4 = mild, 8 = aggressive. Lower = less noise amplification but less contrast lift.</param>
  public static Image<Rgba32> Apply(Image<Rgba32> source, int tileCount = DefaultTileCount, int clipLimit = DefaultClipLimit) {
    ArgumentNullException.ThrowIfNull(source);
    if (tileCount < 1)
      throw new ArgumentOutOfRangeException(nameof(tileCount), tileCount, "Must be >= 1.");
    if (clipLimit < 1)
      throw new ArgumentOutOfRangeException(nameof(clipLimit), clipLimit, "Must be >= 1.");

    var w = source.Width;
    var h = source.Height;
    var src = new Rgba32[w * h];
    source.CopyPixelDataTo(src);

    // 1. Compute per-pixel Y and per-tile remap LUTs.
    var luminance = new byte[w * h];
    for (var i = 0; i < src.Length; i++)
      luminance[i] = RgbToLuma(src[i]);

    var tileW = Math.Max(1, w / tileCount);
    var tileH = Math.Max(1, h / tileCount);
    var lut = new byte[tileCount, tileCount, 256];

    for (var ty = 0; ty < tileCount; ty++) {
      var y0 = ty * tileH;
      var y1 = ty == tileCount - 1 ? h : y0 + tileH;
      for (var tx = 0; tx < tileCount; tx++) {
        var x0 = tx * tileW;
        var x1 = tx == tileCount - 1 ? w : x0 + tileW;
        var histogram = new int[256];
        var pixels = 0;
        for (var py = y0; py < y1; py++) {
          var rowOff = py * w;
          for (var px = x0; px < x1; px++) {
            histogram[luminance[rowOff + px]]++;
            pixels++;
          }
        }
        ClipHistogram(histogram, pixels, clipLimit);
        BuildLut(histogram, pixels, lut, tx, ty);
      }
    }

    // 2. Remap each pixel by bilinear-interpolating the 4 nearest tile LUTs.
    //    This is what kills the tile-boundary visible seams that a naive
    //    per-tile remap would produce.
    var dst = new Rgba32[w * h];
    for (var y = 0; y < h; y++) {
      // Tile centres are at (tx+0.5)*tileW, (ty+0.5)*tileH. Find the two
      // bracketing tiles in each axis and the interpolation fraction.
      var ty0 = (int)Math.Floor((double)y / tileH - 0.5);
      var ty1 = ty0 + 1;
      var fy = (double)y / tileH - 0.5 - ty0;
      ty0 = Math.Clamp(ty0, 0, tileCount - 1);
      ty1 = Math.Clamp(ty1, 0, tileCount - 1);

      for (var x = 0; x < w; x++) {
        var tx0 = (int)Math.Floor((double)x / tileW - 0.5);
        var tx1 = tx0 + 1;
        var fx = (double)x / tileW - 0.5 - tx0;
        tx0 = Math.Clamp(tx0, 0, tileCount - 1);
        tx1 = Math.Clamp(tx1, 0, tileCount - 1);

        var i = y * w + x;
        var y0 = luminance[i];
        var v00 = lut[tx0, ty0, y0];
        var v01 = lut[tx1, ty0, y0];
        var v10 = lut[tx0, ty1, y0];
        var v11 = lut[tx1, ty1, y0];
        var v0 = v00 * (1 - fx) + v01 * fx;
        var v1 = v10 * (1 - fx) + v11 * fx;
        var newY = v0 * (1 - fy) + v1 * fy;

        // Scale RGB channels by the Y change ratio. Same approach Lightroom
        // uses for its "Clarity"-family local contrast — keeps colour ratios
        // stable so the chromatic look of the source is preserved.
        var src1 = src[i];
        var oldY = Math.Max(1, (double)y0);
        var ratio = newY / oldY;
        dst[i] = new Rgba32(
          ClampByte(src1.R * ratio),
          ClampByte(src1.G * ratio),
          ClampByte(src1.B * ratio),
          src1.A);
      }
    }

    var output = new Image<Rgba32>(w, h);
    output.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++)
        dst.AsSpan(y * w, w).CopyTo(accessor.GetRowSpan(y));
    });
    return output;
  }

  /// <summary>BT.601 luminance — integer fast path. Matches the formula used elsewhere in the segmentation pipeline.</summary>
  private static byte RgbToLuma(Rgba32 p) => (byte)((77 * p.R + 150 * p.G + 29 * p.B) >> 8);

  private static byte ClampByte(double v) {
    if (v <= 0) return 0;
    if (v >= 255) return 255;
    return (byte)Math.Round(v);
  }

  /// <summary>
  /// Caps every histogram bin at <c>clipLimit × mean</c>, then
  /// redistributes the excess uniformly across all bins. This is the
  /// "Contrast Limited" part of CLAHE — without it, flat regions get
  /// their noise amplified into ugly grain.
  /// </summary>
  private static void ClipHistogram(int[] hist, int pixels, int clipLimit) {
    var avg = (double)pixels / 256;
    var threshold = (int)(clipLimit * avg);
    var excess = 0;
    for (var i = 0; i < 256; i++) {
      if (hist[i] > threshold) {
        excess += hist[i] - threshold;
        hist[i] = threshold;
      }
    }
    var distribute = excess / 256;
    for (var i = 0; i < 256; i++)
      hist[i] += distribute;
  }

  /// <summary>Build the per-tile cumulative-distribution remapping LUT.</summary>
  private static void BuildLut(int[] hist, int pixels, byte[,,] lut, int tx, int ty) {
    var cum = 0;
    for (var i = 0; i < 256; i++) {
      cum += hist[i];
      lut[tx, ty, i] = ClampByte(cum * 255.0 / Math.Max(1, pixels));
    }
  }
}
