using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Panorama;

/// <summary>
/// Pinhole-camera projection of an equirectangular 360° panorama. For each
/// output pixel a ray is built in camera space, rotated by the user's yaw +
/// pitch, then converted to (longitude, latitude) and sampled out of the
/// source with bilinear interpolation. Longitude wraps so panning across
/// the seam doesn't show a black band; latitudes outside [-90°, 90°] are
/// clamped because the source has no data there.
/// </summary>
public static class Equirectangular {
  public const double DefaultFovDegrees = 90.0;
  public const double MinFovDegrees = 50.0;
  public const double MaxFovDegrees = 120.0;

  /// <summary>
  /// Render a perspective view from <paramref name="source"/>.
  /// </summary>
  /// <param name="source">Equirectangular source — width should be ~2x height.</param>
  /// <param name="outWidth">Output width in pixels.</param>
  /// <param name="outHeight">Output height in pixels.</param>
  /// <param name="yaw">Camera heading in degrees, 0 = centre, +right.</param>
  /// <param name="pitch">Camera pitch in degrees, 0 = horizon, +up.</param>
  /// <param name="fovDeg">Horizontal field of view in degrees.</param>
  public static Image<Rgba32> Render(Image<Rgba32> source, int outWidth, int outHeight, double yaw, double pitch, double fovDeg) {
    ArgumentNullException.ThrowIfNull(source);
    if (outWidth <= 0 || outHeight <= 0)
      throw new ArgumentOutOfRangeException(nameof(outWidth), "Output dimensions must be positive.");

    var sw = source.Width;
    var sh = source.Height;
    if (sw <= 0 || sh <= 0)
      throw new ArgumentException("Source image is empty.", nameof(source));

    var fovRad = DegToRad(Math.Clamp(fovDeg, 1.0, 179.0));
    // Yaw is left-handed about the world +y axis: positive yaw pans the
    // camera to the right, which means the centre samples a longitude to
    // the LEFT of forward in equirectangular x-space (x=0 → longitude=-180,
    // x=W → longitude=+180).
    var yawRad = -DegToRad(WrapDegrees(yaw));
    var pitchRad = DegToRad(Math.Clamp(pitch, -89.9, 89.9));

    var focal = 0.5 * outWidth / Math.Tan(0.5 * fovRad);
    var cx = 0.5 * outWidth;
    var cy = 0.5 * outHeight;

    var sinYaw = Math.Sin(yawRad);
    var cosYaw = Math.Cos(yawRad);
    var sinPitch = Math.Sin(pitchRad);
    var cosPitch = Math.Cos(pitchRad);

    var output = new Image<Rgba32>(outWidth, outHeight);

    // Snapshot source pixels into a flat array — ImageSharp's per-row
    // pixel-span access is fine but pulling a contiguous buffer once lets
    // the inner loop avoid the per-row bookkeeping entirely.
    var sourceBuffer = new Rgba32[sw * sh];
    source.CopyPixelDataTo(sourceBuffer);

    output.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          // Build a ray in camera space (z forward, +x right, +y up).
          var dx = x + 0.5 - cx;
          var dy = cy - (y + 0.5);
          var dz = focal;

          // Pitch about the +x axis, then yaw about the world +y axis.
          var py = dy * cosPitch - dz * sinPitch;
          var pz = dy * sinPitch + dz * cosPitch;

          var rx = dx * cosYaw + pz * sinYaw;
          var ry = py;
          var rz = -dx * sinYaw + pz * cosYaw;

          var len = Math.Sqrt(rx * rx + ry * ry + rz * rz);
          if (len <= 0) {
            row[x] = new Rgba32(0, 0, 0, 255);
            continue;
          }

          // atan2 is scale-invariant; only latitude needs the normalised y.
          var longitude = Math.Atan2(rx, rz);
          var latitude = Math.Asin(Math.Clamp(ry / len, -1.0, 1.0));

          var u = (longitude / (2.0 * Math.PI) + 0.5) * sw;
          var v = (0.5 - latitude / Math.PI) * sh;

          row[x] = SampleBilinear(sourceBuffer, sw, sh, u, v);
        }
      }
    });

    return output;
  }

  /// <summary>Bilinear sample with wraparound on x and clamp on y.</summary>
  internal static Rgba32 SampleBilinear(Rgba32[] pixels, int width, int height, double u, double v) {
    u -= Math.Floor(u / width) * width;
    if (u >= width) u = 0;  // floating-point edge case where Floor falls just short.
    if (v < 0) v = 0;
    if (v > height - 1) v = height - 1;

    var x0 = (int)u;
    var y0 = (int)v;
    var fx = u - x0;
    var fy = v - y0;

    var x1 = x0 + 1 == width ? 0 : x0 + 1;
    var y1 = y0 + 1 >= height ? height - 1 : y0 + 1;

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

    return new Rgba32(
      (byte)Math.Clamp(Math.Round(r), 0, 255),
      (byte)Math.Clamp(Math.Round(g), 0, 255),
      (byte)Math.Clamp(Math.Round(b), 0, 255),
      (byte)Math.Clamp(Math.Round(a), 0, 255));
  }

  /// <summary>Normalise a yaw value to [0, 360).</summary>
  public static double WrapDegrees(double degrees) {
    var r = degrees % 360.0;
    if (r < 0) r += 360.0;
    return r;
  }

  private static double DegToRad(double deg) => deg * Math.PI / 180.0;
}
