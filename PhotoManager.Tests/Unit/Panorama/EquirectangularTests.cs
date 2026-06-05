using Hawkynt.PhotoManager.Core.Panorama;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Panorama;

[TestFixture]
public class EquirectangularTests {

  /// <summary>
  /// Build a 360x180 equirectangular reference where each pixel encodes its
  /// (longitude, latitude) so we can compare arbitrary samples after a
  /// render. R = floor(longitude_deg) wrapped to [0, 360) → 0–255 mod, but
  /// for our simple tests we use plain colour bands keyed on x.
  /// </summary>
  private static Image<Rgba32> BuildBandSource(int width = 360, int height = 180) {
    var image = new Image<Rgba32>(width, height);
    image.ProcessPixelRows(rows => {
      for (var y = 0; y < height; y++) {
        var row = rows.GetRowSpan(y);
        for (var x = 0; x < width; x++) {
          // Encode x in red and y in green so SampleBilinear roundtrips
          // are easy to verify.
          row[x] = new Rgba32(
            (byte)(x * 255 / Math.Max(1, width - 1)),
            (byte)(y * 255 / Math.Max(1, height - 1)),
            0,
            255);
        }
      }
    });
    return image;
  }

  [Test]
  public void Render_IdentityYawZeroPitchWidefov_CenterPixelMatchesSourceCentre() {
    using var source = BuildBandSource(360, 180);
    using var output = Equirectangular.Render(source, outWidth: 90, outHeight: 45,
      yaw: 0, pitch: 0, fovDeg: 120);

    var centre = output[output.Width / 2, output.Height / 2];
    var sourceCentre = source[source.Width / 2, source.Height / 2];

    // Allow 1 LSB drift from the bilinear sampler hitting a half-pixel.
    Assert.That(Math.Abs(centre.R - sourceCentre.R), Is.LessThanOrEqualTo(1));
    Assert.That(Math.Abs(centre.G - sourceCentre.G), Is.LessThanOrEqualTo(1));
  }

  [Test]
  public void Render_Yaw90_CenterPixelShiftsBySourceWidthQuarter() {
    using var source = BuildBandSource(360, 180);
    using var output = Equirectangular.Render(source, outWidth: 90, outHeight: 45,
      yaw: 90, pitch: 0, fovDeg: 120);

    var centre = output[output.Width / 2, output.Height / 2];

    // At yaw=90 the centre ray points to source.Width/4 (90° east of forward).
    var expected = source[source.Width / 4, source.Height / 2];

    Assert.That(Math.Abs(centre.R - expected.R), Is.LessThanOrEqualTo(2));
    Assert.That(Math.Abs(centre.G - expected.G), Is.LessThanOrEqualTo(2));
  }

  [Test]
  public void Render_FullYawWrap_359PlusOneEquals0() {
    using var source = BuildBandSource(360, 180);
    using var a = Equirectangular.Render(source, 60, 30, yaw: 0,   pitch: 0, fovDeg: 90);
    using var b = Equirectangular.Render(source, 60, 30, yaw: 360, pitch: 0, fovDeg: 90);

    var pa = a[a.Width / 2, a.Height / 2];
    var pb = b[b.Width / 2, b.Height / 2];

    Assert.That(pa.R, Is.EqualTo(pb.R));
    Assert.That(pa.G, Is.EqualTo(pb.G));
    Assert.That(pa.B, Is.EqualTo(pb.B));
  }

  [Test]
  public void WrapDegrees_NormalisesToZeroTo360() {
    Assert.That(Equirectangular.WrapDegrees(0),   Is.EqualTo(0).Within(1e-9));
    Assert.That(Equirectangular.WrapDegrees(360), Is.EqualTo(0).Within(1e-9));
    Assert.That(Equirectangular.WrapDegrees(540), Is.EqualTo(180).Within(1e-9));
    Assert.That(Equirectangular.WrapDegrees(-90), Is.EqualTo(270).Within(1e-9));
    Assert.That(Equirectangular.WrapDegrees(-720),Is.EqualTo(0).Within(1e-9));
  }

  [Test]
  public void Render_SampleRoundtrip_KnownYawHitsExpectedSourceColumn() {
    // Source x increases linearly with longitude. yaw=+45° rotates the
    // camera right, so the centre ray samples a longitude 45° west of
    // forward (mirror of the yaw=+90 → x=W/4 contract): x ≈ 0.375 * width.
    using var source = BuildBandSource(360, 180);
    using var output = Equirectangular.Render(source, outWidth: 60, outHeight: 30,
      yaw: 45, pitch: 0, fovDeg: 60);

    var centre = output[output.Width / 2, output.Height / 2];

    var expectedX = (int)(0.375 * source.Width);
    var expected = source[expectedX, source.Height / 2];

    Assert.That(Math.Abs(centre.R - expected.R), Is.LessThanOrEqualTo(2));
  }

  [Test]
  public void Render_OutputDimensions_AreRespected() {
    using var source = BuildBandSource(360, 180);
    using var output = Equirectangular.Render(source, outWidth: 200, outHeight: 100,
      yaw: 0, pitch: 0, fovDeg: 90);

    Assert.That(output.Width, Is.EqualTo(200));
    Assert.That(output.Height, Is.EqualTo(100));
  }

  [Test]
  public void Render_NullSource_Throws() {
    Assert.Throws<ArgumentNullException>(()
      => Equirectangular.Render(null!, 100, 50, 0, 0, 90));
  }

  [Test]
  public void Render_ZeroOutputDimensions_Throws() {
    using var source = BuildBandSource(360, 180);
    Assert.Throws<ArgumentOutOfRangeException>(()
      => Equirectangular.Render(source, 0, 50, 0, 0, 90));
  }
}
