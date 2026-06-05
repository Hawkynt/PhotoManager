using Hawkynt.PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Develop;

[TestFixture]
public class AutoWhiteBalanceTests {
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
  public void NeutralImage_ReturnsNearZero() {
    using var img = SolidColor(100, 100, new Rgba32(128, 128, 128, 255));
    var (temp, tint) = AutoWhiteBalance.Estimate(img);
    Assert.That(temp, Is.EqualTo(0).Within(1.0), "Temperature should be near zero for neutral image");
    Assert.That(tint, Is.EqualTo(0).Within(1.0), "Tint should be near zero for neutral image");
  }

  [Test]
  public void WarmImage_ReturnsNegativeTemperature() {
    // R > G > B: warm cast — the correction should cool it (negative temperature).
    using var img = SolidColor(100, 100, new Rgba32(200, 150, 100, 255));
    var (temp, tint) = AutoWhiteBalance.Estimate(img);
    Assert.That(temp, Is.LessThan(0), "Warm image (R>B) should produce negative (cooling) temperature shift");
  }

  [Test]
  public void CoolImage_ReturnsPositiveTemperature() {
    // B > G > R: cool cast — the correction should warm it (positive temperature).
    using var img = SolidColor(100, 100, new Rgba32(100, 150, 200, 255));
    var (temp, tint) = AutoWhiteBalance.Estimate(img);
    Assert.That(temp, Is.GreaterThan(0), "Cool image (B>R) should produce positive (warming) temperature shift");
  }

  [Test]
  public void GreenCast_ReturnsNegativeTint() {
    // Green-dominant: G much higher than R and B average.
    using var img = SolidColor(100, 100, new Rgba32(100, 200, 100, 255));
    var (temp, tint) = AutoWhiteBalance.Estimate(img);
    Assert.That(tint, Is.LessThan(0), "Green cast should produce negative tint correction (push toward magenta)");
  }

  [Test]
  public void MagentaCast_ReturnsPositiveTint() {
    // Magenta-dominant: R and B are high, G is low.
    using var img = SolidColor(100, 100, new Rgba32(200, 100, 200, 255));
    var (temp, tint) = AutoWhiteBalance.Estimate(img);
    Assert.That(tint, Is.GreaterThan(0), "Magenta cast should produce positive tint correction (push toward green)");
  }

  [Test]
  public void ResultsAlwaysInRange() {
    // Extremely skewed channels should still clamp to [-100, +100].
    using var img = SolidColor(100, 100, new Rgba32(255, 0, 0, 255));
    var (temp, tint) = AutoWhiteBalance.Estimate(img);
    Assert.That(temp, Is.InRange(-100, 100), "Temperature must be in [-100, +100]");
    Assert.That(tint, Is.InRange(-100, 100), "Tint must be in [-100, +100]");
  }

  [Test]
  public void ResultsAlwaysInRange_AllBlue() {
    using var img = SolidColor(100, 100, new Rgba32(0, 0, 255, 255));
    var (temp, tint) = AutoWhiteBalance.Estimate(img);
    Assert.That(temp, Is.InRange(-100, 100), "Temperature must be in [-100, +100]");
    Assert.That(tint, Is.InRange(-100, 100), "Tint must be in [-100, +100]");
  }

  [Test]
  public void ResultsAlwaysInRange_AllGreen() {
    using var img = SolidColor(100, 100, new Rgba32(0, 255, 0, 255));
    var (temp, tint) = AutoWhiteBalance.Estimate(img);
    Assert.That(temp, Is.InRange(-100, 100), "Temperature must be in [-100, +100]");
    Assert.That(tint, Is.InRange(-100, 100), "Tint must be in [-100, +100]");
  }

  [Test]
  public void BlackImage_ReturnsZero() {
    using var img = SolidColor(50, 50, new Rgba32(0, 0, 0, 255));
    var (temp, tint) = AutoWhiteBalance.Estimate(img);
    Assert.That(temp, Is.EqualTo(0), "All-black image should return 0 temperature");
    Assert.That(tint, Is.EqualTo(0), "All-black image should return 0 tint");
  }

  [Test]
  public void WhiteImage_ReturnsNearZero() {
    using var img = SolidColor(100, 100, new Rgba32(255, 255, 255, 255));
    var (temp, tint) = AutoWhiteBalance.Estimate(img);
    Assert.That(temp, Is.EqualTo(0).Within(1.0), "All-white image should return near-zero temperature");
    Assert.That(tint, Is.EqualTo(0).Within(1.0), "All-white image should return near-zero tint");
  }

  [Test]
  public void WarmAndCoolAreOpposite() {
    using var warm = SolidColor(100, 100, new Rgba32(200, 150, 100, 255));
    using var cool = SolidColor(100, 100, new Rgba32(100, 150, 200, 255));
    var (warmTemp, _) = AutoWhiteBalance.Estimate(warm);
    var (coolTemp, _) = AutoWhiteBalance.Estimate(cool);
    // The two corrections should have opposite signs.
    Assert.That(warmTemp * coolTemp, Is.LessThan(0), "Warm and cool images should produce opposite temperature corrections");
    // And they should be roughly symmetric given the symmetric input.
    Assert.That(Math.Abs(warmTemp), Is.EqualTo(Math.Abs(coolTemp)).Within(2.0));
  }
}
