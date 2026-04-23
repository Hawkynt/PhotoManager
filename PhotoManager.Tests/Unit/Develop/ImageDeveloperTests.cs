using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
public class ImageDeveloperTests {
  private static Image<Rgba32> SolidColor(int width, int height, Rgba32 color) {
    var img = new Image<Rgba32>(width, height);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = color;
      }
    });
    return img;
  }

  [Test]
  public void Apply_IdentitySettings_ReturnsEquivalentImage() {
    using var src = SolidColor(10, 10, new Rgba32(100, 150, 200, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings());
    Assert.Multiple(() => {
      Assert.That(out_.Width, Is.EqualTo(10));
      Assert.That(out_.Height, Is.EqualTo(10));
      out_.ProcessPixelRows(a => {
        var px = a.GetRowSpan(0)[0];
        Assert.That(px.R, Is.EqualTo(100));
        Assert.That(px.G, Is.EqualTo(150));
        Assert.That(px.B, Is.EqualTo(200));
      });
    });
  }

  [Test]
  public void Apply_Rotate90_SwapsWidthHeight() {
    using var src = SolidColor(20, 10, new Rgba32(1, 2, 3, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(RotationDegrees: 90));
    Assert.Multiple(() => {
      Assert.That(out_.Width, Is.EqualTo(10));
      Assert.That(out_.Height, Is.EqualTo(20));
    });
  }

  [Test]
  public void Apply_ExposurePositive_BrightenPixels() {
    using var src = SolidColor(5, 5, new Rgba32(50, 50, 50, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(ExposureStops: 1.0));
    // +1 stop → roughly doubles brightness → about 100.
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      Assert.That((int)px.R, Is.InRange(98, 102));
    });
  }

  [Test]
  public void Apply_SaturationMinus100_ProducesGrayscale() {
    using var src = SolidColor(5, 5, new Rgba32(200, 50, 50, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(SaturationPercent: -100));
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      Assert.That(px.R, Is.EqualTo(px.G));
      Assert.That(px.G, Is.EqualTo(px.B));
    });
  }

  [Test]
  public void Apply_TemperaturePositive_ShiftsTowardWarm() {
    using var src = SolidColor(5, 5, new Rgba32(128, 128, 128, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(TemperatureShift: 100));
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      Assert.That((int)px.R, Is.GreaterThan(130));
      Assert.That((int)px.B, Is.LessThan(120));
    });
  }

  [Test]
  public void Apply_ContrastPositive_PushesFarFromMidGray() {
    using var src = SolidColor(5, 5, new Rgba32(200, 200, 200, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(ContrastPercent: 50));
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      // Above midgray → contrast should push brighter.
      Assert.That((int)px.R, Is.GreaterThan(200));
    });
  }

  [Test]
  public void IsIdentity_AllZero_True() {
    var s = new DevelopSettings();
    Assert.That(s.IsIdentity, Is.True);
  }

  [Test]
  public void IsIdentity_AnyNonZero_False() {
    var s = new DevelopSettings(ExposureStops: 0.1);
    Assert.That(s.IsIdentity, Is.False);
  }
}
