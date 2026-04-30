using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Panorama;

/// <summary>
/// Re-projects a flat (rectilinear) source image onto the surface of a
/// cylinder. This is the standard pre-warp for tripod-based panoramas: a
/// horizontal pan around a fixed nodal point becomes a horizontal translation
/// in cylindrical space, so naive translation-only stitching can line frames
/// up without solving for full homographies.
/// </summary>
public static class CylindricalProjector {
  /// <summary>
  /// Project <paramref name="source"/> onto a cylinder with focal length
  /// <paramref name="focalPixels"/> (defaults to image width ≈ 90° FOV).
  /// For each output pixel (u, v) the inverse map computes the 3D ray on the
  /// cylinder surface, projects it back to the source plane, and bilinearly
  /// samples. Out-of-source pixels stay transparent.
  /// </summary>
  public static Image<Rgba32> Project(Image<Rgba32> source, double focalPixels) {
    ArgumentNullException.ThrowIfNull(source);
    if (focalPixels <= 0)
      throw new ArgumentOutOfRangeException(nameof(focalPixels));

    var width = source.Width;
    var height = source.Height;
    var cx = (width - 1) / 2.0;
    var cy = (height - 1) / 2.0;

    // Output canvas matches the source pixel-for-pixel: the cylinder is
    // unwrapped over the same horizontal angular extent, so the X axis stays
    // proportional to atan(x/f). For modest fields of view this is within a
    // few percent of source width.
    var output = new Image<Rgba32>(width, height);

    // Snapshot source rows into a flat array once: ImageSharp's pixel
    // accessor isn't safe to use from a tight inner loop on a separate
    // image, and we sample with bilinear weights so we hit four neighbours
    // per output pixel.
    var srcPixels = new Rgba32[width * height];
    source.CopyPixelDataTo(srcPixels);

    output.ProcessPixelRows(accessor => {
      for (var v = 0; v < accessor.Height; v++) {
        var row = accessor.GetRowSpan(v);
        var yC = v - cy;
        for (var u = 0; u < row.Length; u++) {
          var theta = (u - cx) / focalPixels;
          var sinT = Math.Sin(theta);
          var cosT = Math.Cos(theta);
          // Inverse map: (u,v) on cylinder → ray (sinT, y/f, cosT) →
          // perspective project to source plane (x = f*tan(theta),
          // y = v / cos(theta)).
          if (cosT <= 1e-6) {
            row[u] = default;
            continue;
          }
          var srcX = focalPixels * sinT / cosT + cx;
          var srcY = yC / cosT + cy;
          row[u] = SampleBilinear(srcPixels, width, height, srcX, srcY);
        }
      }
    });

    return output;
  }

  internal static Rgba32 SampleBilinear(Rgba32[] pixels, int width, int height, double x, double y) {
    if (x < 0 || y < 0 || x > width - 1 || y > height - 1)
      return default;

    var x0 = (int)Math.Floor(x);
    var y0 = (int)Math.Floor(y);
    var x1 = Math.Min(x0 + 1, width - 1);
    var y1 = Math.Min(y0 + 1, height - 1);
    var fx = x - x0;
    var fy = y - y0;

    var p00 = pixels[y0 * width + x0];
    var p10 = pixels[y0 * width + x1];
    var p01 = pixels[y1 * width + x0];
    var p11 = pixels[y1 * width + x1];

    var w00 = (1 - fx) * (1 - fy);
    var w10 = fx * (1 - fy);
    var w01 = (1 - fx) * fy;
    var w11 = fx * fy;

    var r = p00.R * w00 + p10.R * w10 + p01.R * w01 + p11.R * w11;
    var g = p00.G * w00 + p10.G * w10 + p01.G * w01 + p11.G * w11;
    var b = p00.B * w00 + p10.B * w10 + p01.B * w01 + p11.B * w11;
    var a = p00.A * w00 + p10.A * w10 + p01.A * w01 + p11.A * w11;

    return new Rgba32((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255),
                      (byte)Math.Clamp(b, 0, 255), (byte)Math.Clamp(a, 0, 255));
  }
}
