using PhotoManager.Core.Panorama;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Panorama;

[TestFixture]
public class CylindricalProjectorTests {
  private static Image<Rgba32> Gradient(int width, int height) {
    var img = new Image<Rgba32>(width, height);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var r = (byte)(x * 255 / Math.Max(1, accessor.Width - 1));
          var g = (byte)(y * 255 / Math.Max(1, accessor.Height - 1));
          row[x] = new Rgba32(r, g, 128, 255);
        }
      }
    });
    return img;
  }

  [Test]
  public void Project_OutputDimensionsMatchSource() {
    using var src = Gradient(80, 40);
    using var output = CylindricalProjector.Project(src, src.Width);
    Assert.That(output.Width, Is.GreaterThanOrEqualTo(src.Width));
    Assert.That(output.Height, Is.GreaterThanOrEqualTo(src.Height));
  }

  [Test]
  public void Project_CenterPixel_IsPreservedThroughRoundTrip() {
    using var src = Gradient(101, 51);
    using var output = CylindricalProjector.Project(src, src.Width);

    // The cylinder mapping is identity at theta=0 (the optical axis),
    // which falls on the central column. The central pixel should match
    // its source counterpart exactly (modulo 1-pixel bilinear noise).
    var srcCenter = ReadPixel(src, src.Width / 2, src.Height / 2);
    var outCenter = ReadPixel(output, output.Width / 2, output.Height / 2);
    Assert.That(Math.Abs(outCenter.R - srcCenter.R), Is.LessThanOrEqualTo(2));
    Assert.That(Math.Abs(outCenter.G - srcCenter.G), Is.LessThanOrEqualTo(2));
    Assert.That(Math.Abs(outCenter.B - srcCenter.B), Is.LessThanOrEqualTo(2));
  }

  [Test]
  public void Project_LongFocalLength_BehavesNearIdentity() {
    // At very long focal length the cylinder is locally flat ⇒ output ≈ input.
    using var src = Gradient(20, 20);
    using var output = CylindricalProjector.Project(src, src.Width * 50.0);

    var srcMid = ReadPixel(src, 10, 10);
    var outMid = ReadPixel(output, 10, 10);
    Assert.That(outMid.R, Is.EqualTo(srcMid.R));
    Assert.That(outMid.G, Is.EqualTo(srcMid.G));
  }

  [Test]
  public void Project_RejectsNonPositiveFocalLength() {
    using var src = Gradient(10, 10);
    Assert.Throws<ArgumentOutOfRangeException>(() => CylindricalProjector.Project(src, 0));
  }

  private static Rgba32 ReadPixel(Image<Rgba32> img, int x, int y) {
    Rgba32 picked = default;
    img.ProcessPixelRows(accessor => { picked = accessor.GetRowSpan(y)[x]; });
    return picked;
  }
}
