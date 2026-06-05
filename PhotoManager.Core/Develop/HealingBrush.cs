using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Pure pixel-copy clone-stamp engine. Copies a circular disc of pixels
/// from a source region to a destination region with a soft-edge feather
/// (Gaussian-ish falloff at the disc boundary) so the clone blends into
/// the surrounding area. Works in-place on the image. Thread-safe for
/// non-overlapping regions.
/// </summary>
public static class HealingBrush {
  /// <summary>
  /// Stamp a single feathered disc: copy pixels from the source circle
  /// centred at (<paramref name="srcX"/>,<paramref name="srcY"/>) onto
  /// the destination circle centred at
  /// (<paramref name="dstX"/>,<paramref name="dstY"/>).
  /// </summary>
  /// <param name="image">Target image (mutated in-place).</param>
  /// <param name="srcX">Source centre X.</param>
  /// <param name="srcY">Source centre Y.</param>
  /// <param name="dstX">Destination centre X.</param>
  /// <param name="dstY">Destination centre Y.</param>
  /// <param name="radius">Disc radius in pixels. Zero or negative is a no-op.</param>
  public static void Apply(Image<Rgba32> image, int srcX, int srcY, int dstX, int dstY, int radius) {
    if (radius <= 0)
      return;
    if (image.Width == 0 || image.Height == 0)
      return;

    var w = image.Width;
    var h = image.Height;

    // Bounding box of the destination disc, clamped to image bounds.
    var dMinX = Math.Max(0, dstX - radius);
    var dMaxX = Math.Min(w - 1, dstX + radius);
    var dMinY = Math.Max(0, dstY - radius);
    var dMaxY = Math.Min(h - 1, dstY + radius);

    if (dMinX > dMaxX || dMinY > dMaxY)
      return;

    var r2 = (double)radius * radius;
    // Feather starts at 70% of the radius — inner pixels are fully opaque,
    // outer pixels fade out smoothly.
    var featherStart = 0.7 * radius;
    var featherRange = radius - featherStart;

    // Read source pixels first so overlapping src/dst don't corrupt the copy.
    // Build a flat buffer of (weight, srcPixel) for every destination pixel.
    var bw = dMaxX - dMinX + 1;
    var bh = dMaxY - dMinY + 1;
    var stamps = new (float weight, Rgba32 pixel)[bw * bh];
    var hasAny = false;

    image.ProcessPixelRows(accessor => {
      for (var ly = 0; ly < bh; ly++) {
        var dy = dMinY + ly;
        var fy = dy - dstY;
        var rowOff = ly * bw;
        for (var lx = 0; lx < bw; lx++) {
          var dx = dMinX + lx;
          var fx = dx - dstX;
          var dist2 = fx * fx + fy * fy;
          if (dist2 > r2) {
            stamps[rowOff + lx] = (0f, default);
            continue;
          }

          var dist = Math.Sqrt(dist2);
          var weight = (float)Smoothstep(featherStart, featherStart + featherRange, dist);

          // Corresponding source pixel.
          var sx = srcX + fx;
          var sy = srcY + fy;
          // Clamp source to image bounds.
          sx = Math.Clamp(sx, 0, w - 1);
          sy = Math.Clamp(sy, 0, h - 1);

          var srcPixel = accessor.GetRowSpan(sy)[sx];
          stamps[rowOff + lx] = (weight, srcPixel);
          hasAny = true;
        }
      }
    });

    if (!hasAny)
      return;

    // Write blended pixels to the destination.
    image.ProcessPixelRows(accessor => {
      for (var ly = 0; ly < bh; ly++) {
        var dy = dMinY + ly;
        var row = accessor.GetRowSpan(dy);
        var rowOff = ly * bw;
        for (var lx = 0; lx < bw; lx++) {
          var (weight, srcPixel) = stamps[rowOff + lx];
          if (weight <= 0f) {
            // weight == 0 means either outside disc or full-strength clone
            // Check if this pixel is inside the disc at all
            var dx = dMinX + lx;
            var fx = dx - dstX;
            var fy = (dMinY + ly) - dstY;
            if (fx * fx + fy * fy <= r2) {
              // Inside disc but weight=0 means feather=0 => full clone
              row[dx] = srcPixel;
            }
            continue;
          }
          // weight is the smoothstep fade: 0 at centre (full clone), 1 at edge (keep dst).
          var dx2 = dMinX + lx;
          ref var dst = ref row[dx2];
          dst = new Rgba32(
            (byte)Math.Clamp(srcPixel.R * (1f - weight) + dst.R * weight + 0.5f, 0, 255),
            (byte)Math.Clamp(srcPixel.G * (1f - weight) + dst.G * weight + 0.5f, 0, 255),
            (byte)Math.Clamp(srcPixel.B * (1f - weight) + dst.B * weight + 0.5f, 0, 255),
            (byte)Math.Clamp(srcPixel.A * (1f - weight) + dst.A * weight + 0.5f, 0, 255)
          );
        }
      }
    });
  }

  /// <summary>
  /// Hermite smoothstep: returns 0 when <paramref name="x"/> &lt;= <paramref name="edge0"/>,
  /// 1 when <paramref name="x"/> &gt;= <paramref name="edge1"/>, and a smooth
  /// S-curve in between.
  /// </summary>
  internal static double Smoothstep(double edge0, double edge1, double x) {
    if (edge1 <= edge0)
      return x < edge0 ? 0 : 1;
    var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
    return t * t * (3 - 2 * t);
  }
}
