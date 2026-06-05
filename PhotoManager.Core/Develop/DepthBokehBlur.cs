using Hawkynt.PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Depth-aware portrait/landscape blur. Pixels far from the user-chosen
/// focal plane get a Gaussian blur scaled by their depth distance,
/// pixels at the focal plane stay sharp. The result approximates
/// shallow-depth-of-field bokeh — what a wide-aperture portrait lens
/// would produce.
///
/// Inputs:
///   source   — original RGB image at full resolution.
///   depth    — depth map at source dimensions (from OnnxDepthEstimator).
///   focus    — normalised depth [0..1] (1 = closest) to keep sharp.
///              Auto-detect logic in the UI typically sets this to the
///              face centroid's depth, or to the depth where the user clicked.
///   strength — maximum blur radius in pixels at the farthest plane.
///              Typical values: 4–24. Larger = stronger background separation.
///
/// Pure C#, no ONNX session. ~O(W × H × maxRadius) which is fast enough
/// for the develop preview (~200 ms on a 1920×1280 image at strength=16).
/// </summary>
public static class DepthBokehBlur {
  /// <summary>Convenience overload — picks focus = closest pixel (the subject by default).</summary>
  public static Image<Rgba32> Apply(Image<Rgba32> source, DepthMap depth, double strength = 12.0)
    => Apply(source, depth, focus: 1.0, strength);

  /// <summary>
  /// Apply depth-aware blur. Returns a freshly allocated image; source
  /// is not mutated. The blur kernel for each pixel is computed from
  /// <c>|normalisedDepth - focus|</c> — pixels exactly at the focal
  /// plane stay pin-sharp, pixels at the opposite end of the depth
  /// range get the full strength radius.
  /// </summary>
  public static Image<Rgba32> Apply(Image<Rgba32> source, DepthMap depth, double focus, double strength) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(depth);
    if (depth.Width != source.Width || depth.Height != source.Height)
      throw new ArgumentException($"Depth map dimensions {depth.Width}x{depth.Height} must match source {source.Width}x{source.Height}.", nameof(depth));

    strength = Math.Max(0, strength);
    if (strength < 0.5)
      return source.Clone();

    var maxRadius = (int)Math.Ceiling(strength);
    var w = source.Width;
    var h = source.Height;

    // Snapshot source into a flat array so the row-parallel loop is
    // thread-safe (ImageSharp's accessor isn't).
    var src = new Rgba32[w * h];
    source.CopyPixelDataTo(src);
    var dst = new Rgba32[w * h];

    var depthMin = depth.Min;
    var depthSpan = Math.Max(1e-6f, depth.Max - depthMin);

    Parallel.For(0, h, y => {
      for (var x = 0; x < w; x++) {
        var z = (depth.Values[y * w + x] - depthMin) / depthSpan;
        var distance = Math.Abs(z - focus);
        // Map distance [0..max] to radius [0..maxRadius]. Max focus
        // distance under a single focal plane is 1.0 (focus=1 means
        // farthest pixel has distance 1).
        var radius = (int)Math.Round(distance * strength);
        if (radius <= 0) {
          dst[y * w + x] = src[y * w + x];
          continue;
        }
        var rEff = Math.Min(radius, maxRadius);
        long sumR = 0, sumG = 0, sumB = 0;
        long count = 0;
        var y0 = Math.Max(0, y - rEff);
        var y1 = Math.Min(h - 1, y + rEff);
        var x0 = Math.Max(0, x - rEff);
        var x1 = Math.Min(w - 1, x + rEff);
        for (var yy = y0; yy <= y1; yy++) {
          var rowOff = yy * w;
          for (var xx = x0; xx <= x1; xx++) {
            var p = src[rowOff + xx];
            sumR += p.R; sumG += p.G; sumB += p.B;
            count++;
          }
        }
        dst[y * w + x] = new Rgba32(
          (byte)(sumR / count),
          (byte)(sumG / count),
          (byte)(sumB / count),
          (byte)255);
      }
    });

    var output = new Image<Rgba32>(w, h);
    output.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++)
        dst.AsSpan(y * w, w).CopyTo(accessor.GetRowSpan(y));
    });
    return output;
  }
}
