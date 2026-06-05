using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Hdr;

/// <summary>
/// Pure (Dx, Dy) shift in pixels of one bracket frame relative to a
/// reference frame. Positive Dx shifts the candidate to the right, positive
/// Dy shifts it down (i.e. the candidate sits Dx px right and Dy px below
/// where it should be).
/// </summary>
public readonly record struct Translation(int Dx, int Dy);

/// <summary>
/// Median-threshold-bitmap alignment for hand-held bracket sets, after
/// Greg Ward's "Fast, robust image registration for compositing high
/// dynamic range photographs from hand-held exposures". Each frame is
/// reduced to a 1-bit threshold map at its luminance median; comparisons
/// are an XOR pop-count (with a small exclusion zone around the median to
/// suppress noise). A pyramid lets a few bits of search at each level
/// cover ±64 px shifts in O(log range) time.
/// </summary>
public static class ExposureAlignment {
  /// <summary>Maximum pyramid depth; at level k each pixel covers 2^k of the source.</summary>
  public const int DefaultMaxLevels = 6;

  /// <summary>Pixels around the median excluded from the comparison so noise near zero doesn't drown the signal.</summary>
  public const int DefaultNoiseTolerance = 4;

  public static Translation Align(Image<Rgba32> reference, Image<Rgba32> candidate)
    => Align(reference, candidate, DefaultMaxLevels, DefaultNoiseTolerance);

  public static Translation Align(
    Image<Rgba32> reference,
    Image<Rgba32> candidate,
    int maxLevels,
    int noiseTolerance
  ) {
    if (reference.Width != candidate.Width || reference.Height != candidate.Height)
      throw new ArgumentException("Bracket frames must share dimensions for MTB alignment.");

    var refLuma = ToLuma(reference);
    var candLuma = ToLuma(candidate);

    var levels = ComputePyramidLevels(reference.Width, reference.Height, maxLevels);

    var refPyramid = BuildPyramid(refLuma, reference.Width, reference.Height, levels);
    var candPyramid = BuildPyramid(candLuma, reference.Width, reference.Height, levels);

    var (dx, dy) = (0, 0);
    for (var level = levels - 1; level >= 0; level--) {
      dx <<= 1;
      dy <<= 1;
      var (refMtb, refExcl) = ThresholdMap(refPyramid[level].Pixels, refPyramid[level].Width, refPyramid[level].Height, noiseTolerance);
      var (candMtb, candExcl) = ThresholdMap(candPyramid[level].Pixels, candPyramid[level].Width, candPyramid[level].Height, noiseTolerance);

      long bestErr = ShiftedDifference(
        refMtb, refExcl, candMtb, candExcl,
        refPyramid[level].Width, refPyramid[level].Height, dx, dy);
      var bestDx = dx;
      var bestDy = dy;
      for (var oy = -1; oy <= 1; oy++) {
        for (var ox = -1; ox <= 1; ox++) {
          if (ox == 0 && oy == 0)
            continue;
          var trialDx = dx + ox;
          var trialDy = dy + oy;
          var err = ShiftedDifference(
            refMtb, refExcl,
            candMtb, candExcl,
            refPyramid[level].Width, refPyramid[level].Height,
            trialDx, trialDy);
          if (err >= bestErr)
            continue;
          bestErr = err;
          bestDx = trialDx;
          bestDy = trialDy;
        }
      }
      dx = bestDx;
      dy = bestDy;
    }

    return new Translation(dx, dy);
  }

  internal static byte[] ToLuma(Image<Rgba32> image) {
    var w = image.Width;
    var h = image.Height;
    var luma = new byte[w * h];
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        var off = y * w;
        for (var x = 0; x < w; x++) {
          var p = row[x];
          luma[off + x] = (byte)((54 * p.R + 183 * p.G + 19 * p.B) >> 8);
        }
      }
    });
    return luma;
  }

  internal static int ComputePyramidLevels(int width, int height, int maxLevels) {
    var minDim = Math.Min(width, height);
    var levels = 1;
    while (levels < maxLevels && (minDim >> levels) >= 8)
      levels++;
    return levels;
  }

  internal static (byte[] Pixels, int Width, int Height)[] BuildPyramid(
    byte[] level0,
    int width,
    int height,
    int levels
  ) {
    var result = new (byte[] Pixels, int Width, int Height)[levels];
    result[0] = (level0, width, height);
    for (var i = 1; i < levels; i++) {
      var (parent, pw, ph) = result[i - 1];
      var nw = pw / 2;
      var nh = ph / 2;
      if (nw < 1) nw = 1;
      if (nh < 1) nh = 1;
      var down = new byte[nw * nh];
      for (var y = 0; y < nh; y++) {
        var sy = y * 2;
        var nextSy = Math.Min(sy + 1, ph - 1);
        for (var x = 0; x < nw; x++) {
          var sx = x * 2;
          var nextSx = Math.Min(sx + 1, pw - 1);
          var sum = parent[sy * pw + sx] + parent[sy * pw + nextSx]
                    + parent[nextSy * pw + sx] + parent[nextSy * pw + nextSx];
          down[y * nw + x] = (byte)(sum >> 2);
        }
      }
      result[i] = (down, nw, nh);
    }
    return result;
  }

  internal static (byte[] Mtb, byte[] Exclusion) ThresholdMap(byte[] luma, int width, int height, int noiseTolerance) {
    var median = Median(luma);
    var lo = (byte)Math.Max(0, median - noiseTolerance);
    var hi = (byte)Math.Min(255, median + noiseTolerance);
    var mtb = new byte[luma.Length];
    var excl = new byte[luma.Length];
    for (var i = 0; i < luma.Length; i++) {
      var v = luma[i];
      mtb[i] = v > median ? (byte)1 : (byte)0;
      excl[i] = (v >= lo && v <= hi) ? (byte)1 : (byte)0;
    }
    return (mtb, excl);
  }

  internal static byte Median(byte[] luma) {
    Span<int> hist = stackalloc int[256];
    for (var i = 0; i < luma.Length; i++)
      hist[luma[i]]++;
    var half = luma.Length / 2;
    var running = 0;
    for (var v = 0; v < 256; v++) {
      running += hist[v];
      if (running >= half)
        return (byte)v;
    }
    return 255;
  }

  internal static long ShiftedDifference(
    byte[] refMtb, byte[] refExcl,
    byte[] candMtb, byte[] candExcl,
    int width, int height,
    int dx, int dy
  ) {
    long err = 0;
    var yStart = Math.Max(0, dy);
    var yEnd = Math.Min(height, height + dy);
    var xStart = Math.Max(0, dx);
    var xEnd = Math.Min(width, width + dx);
    for (var y = yStart; y < yEnd; y++) {
      var cy = y - dy;
      var refRow = y * width;
      var candRow = cy * width;
      for (var x = xStart; x < xEnd; x++) {
        var cx = x - dx;
        if (refExcl[refRow + x] != 0 || candExcl[candRow + cx] != 0)
          continue;
        if (refMtb[refRow + x] != candMtb[candRow + cx])
          err++;
      }
    }
    return err;
  }

  /// <summary>
  /// Apply a translation to a frame, padding exposed edges with the edge
  /// pixel value. Output has the same dimensions as <paramref name="source"/>.
  /// </summary>
  public static Image<Rgba32> Shift(Image<Rgba32> source, Translation t) {
    var w = source.Width;
    var h = source.Height;
    var output = new Image<Rgba32>(w, h);
    source.ProcessPixelRows(output, (src, dst) => {
      for (var y = 0; y < h; y++) {
        var srcY = Math.Clamp(y - t.Dy, 0, h - 1);
        var srcRow = src.GetRowSpan(srcY);
        var dstRow = dst.GetRowSpan(y);
        for (var x = 0; x < w; x++) {
          var srcX = Math.Clamp(x - t.Dx, 0, w - 1);
          dstRow[x] = srcRow[srcX];
        }
      }
    });
    return output;
  }
}
