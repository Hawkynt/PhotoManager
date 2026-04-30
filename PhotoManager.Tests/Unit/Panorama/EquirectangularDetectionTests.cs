using PhotoManager.Core.Metadata;
using PhotoManager.Core.Panorama;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Panorama;

[TestFixture]
public class EquirectangularDetectionTests {

  [Test]
  public void TwoToOneAspect_ReturnsTrue() {
    using var img = new Image<Rgba32>(2000, 1000);
    Assert.That(EquirectangularDetection.LooksEquirectangular(img, null), Is.True);
  }

  [Test]
  public void NearlyTwoToOneAspect_WithinFivePercent_ReturnsTrue() {
    // 2.05:1 is within tolerance.
    using var img = new Image<Rgba32>(2050, 1000);
    Assert.That(EquirectangularDetection.LooksEquirectangular(img, null), Is.True);
  }

  [Test]
  public void ThreeToTwoAspect_ReturnsFalse() {
    using var img = new Image<Rgba32>(3000, 2000);
    Assert.That(EquirectangularDetection.LooksEquirectangular(img, null), Is.False);
  }

  [Test]
  public void FourToThreeAspect_ReturnsFalse() {
    using var img = new Image<Rgba32>(2000, 1500);
    Assert.That(EquirectangularDetection.LooksEquirectangular(img, null), Is.False);
  }

  [Test]
  public void GpanoProjectionTypeOverride_ReturnsTrueRegardlessOfAspect() {
    Assert.That(
      EquirectangularDetection.LooksEquirectangular(3000, 2000, "equirectangular"),
      Is.True);
  }

  [Test]
  public void GpanoProjectionTypeOverride_CaseInsensitive() {
    Assert.That(
      EquirectangularDetection.LooksEquirectangular(640, 480, "EquiRectangular"),
      Is.True);
  }

  [Test]
  public void HugeImage_AtLeastPanoramicAspect_ReturnsTrue() {
    // 5000x2000 = 2.5:1 — wider than 2:1 but the >=4096 width signal kicks in.
    using var img = new Image<Rgba32>(5000, 2000);
    Assert.That(EquirectangularDetection.LooksEquirectangular(img, null), Is.True);
  }

  [Test]
  public void HugeImageButTallAspect_ReturnsFalse() {
    // 5000x4500 — width crosses the 4096 threshold but aspect is far from
    // panoramic; we want to reject portrait/square mega-shots.
    using var img = new Image<Rgba32>(5000, 4500);
    Assert.That(EquirectangularDetection.LooksEquirectangular(img, null), Is.False);
  }

  [Test]
  public void ZeroDimensions_WithoutOverride_ReturnsFalse() {
    Assert.That(EquirectangularDetection.LooksEquirectangular(0, 0, null), Is.False);
  }

  [Test]
  public void MetadataKeywordHint_TreatedAsProjectionMarker() {
    var meta = new FullMetadata { Keywords = new[] { "GPano:ProjectionType=equirectangular" } };
    using var img = new Image<Rgba32>(640, 480);
    Assert.That(EquirectangularDetection.LooksEquirectangular(img, meta), Is.True);
  }
}
