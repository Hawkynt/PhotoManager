using NUnit.Framework;
using Hawkynt.PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Develop;

[TestFixture]
[Category("Unit")]
public sealed class ContentAwareFillTests {
  [Test]
  public void DilateMask_ExpandsMaskedRegion() {
    // Create a 20x20 mask with a single white pixel at (10,10).
    using var mask = new Image<Rgba32>(20, 20, new Rgba32(0, 0, 0, 255));
    mask[10, 10] = new Rgba32(255, 255, 255, 255);

    ContentAwareFill.DilateMask(mask, radius: 3);

    // The original pixel should still be set.
    Assert.That(mask[10, 10].R, Is.GreaterThanOrEqualTo(128), "original pixel should remain set");

    // Pixels within the dilation radius should now be set.
    Assert.That(mask[10, 11].R, Is.GreaterThanOrEqualTo(128), "adjacent pixel should be dilated");
    Assert.That(mask[10, 9].R, Is.GreaterThanOrEqualTo(128), "adjacent pixel should be dilated");
    Assert.That(mask[11, 10].R, Is.GreaterThanOrEqualTo(128), "adjacent pixel should be dilated");
    Assert.That(mask[9, 10].R, Is.GreaterThanOrEqualTo(128), "adjacent pixel should be dilated");
    Assert.That(mask[12, 12].R, Is.GreaterThanOrEqualTo(128), "diagonal within radius should be dilated");
    Assert.That(mask[13, 13].R, Is.GreaterThanOrEqualTo(128), "corner of Chebyshev radius=3 should be dilated");

    // Pixels outside the dilation radius should still be unset.
    Assert.That(mask[14, 14].R, Is.LessThan(128), "pixel outside radius should remain unset");
    Assert.That(mask[5, 5].R, Is.LessThan(128), "distant pixel should remain unset");
  }

  [Test]
  public void DilateMask_ZeroRadius_NoChange() {
    using var mask = new Image<Rgba32>(20, 20, new Rgba32(0, 0, 0, 255));
    mask[10, 10] = new Rgba32(255, 255, 255, 255);
    var before = SnapshotPixels(mask);

    ContentAwareFill.DilateMask(mask, radius: 0);

    var after = SnapshotPixels(mask);
    Assert.That(after, Is.EqualTo(before), "zero radius dilation should not change the mask");
  }

  [Test]
  public void DilateMask_NegativeRadius_NoChange() {
    using var mask = new Image<Rgba32>(20, 20, new Rgba32(0, 0, 0, 255));
    mask[10, 10] = new Rgba32(255, 255, 255, 255);
    var before = SnapshotPixels(mask);

    ContentAwareFill.DilateMask(mask, radius: -5);

    var after = SnapshotPixels(mask);
    Assert.That(after, Is.EqualTo(before), "negative radius dilation should not change the mask");
  }

  [Test]
  public void RasteriseMask_ProducesCorrectDimensions() {
    var dabs = new[] { new BrushDab(0.5, 0.5, 0.1, 1.0) };
    using var mask = ContentAwareFill.RasteriseMask(dabs, 100, 80);

    Assert.That(mask.Width, Is.EqualTo(100));
    Assert.That(mask.Height, Is.EqualTo(80));
  }

  [Test]
  public void RasteriseMask_CentreDabSetsMaskPixels() {
    // Place a dab at the centre covering roughly 10% of the image radius.
    var dabs = new[] { new BrushDab(0.5, 0.5, 0.1, 1.0) };
    using var mask = ContentAwareFill.RasteriseMask(dabs, 100, 100);

    // The centre pixel should be masked.
    Assert.That(mask[50, 50].R, Is.GreaterThanOrEqualTo(128),
      "centre of dab should be masked");

    // A corner far from the dab should not be masked.
    Assert.That(mask[0, 0].R, Is.LessThan(128),
      "corner far from dab should not be masked");
  }

  [Test]
  public void RasteriseMask_EmptyDabs_ProducesEmptyMask() {
    var dabs = Array.Empty<BrushDab>();
    using var mask = ContentAwareFill.RasteriseMask(dabs, 50, 50);

    Assert.That(ContentAwareFill.HasAnyMaskedPixels(mask), Is.False,
      "empty dab list should produce an empty mask");
  }

  [Test]
  public void Apply_EmptyDabs_ReturnsNull() {
    using var image = new Image<Rgba32>(64, 64, new Rgba32(128, 128, 128, 255));
    var result = ContentAwareFill.Apply(image, Array.Empty<BrushDab>());

    Assert.That(result, Is.Null, "empty dabs should return null (no-op)");
  }

  [Test]
  public void Apply_NullDabs_ReturnsNull() {
    using var image = new Image<Rgba32>(64, 64, new Rgba32(128, 128, 128, 255));
    var result = ContentAwareFill.Apply(image, null!);

    Assert.That(result, Is.Null, "null dabs should return null (no-op)");
  }

  [Test]
  public void Apply_WithDabs_DoesNotCrash() {
    // When the LaMa model is not installed, Apply should return null
    // gracefully rather than throwing. When it IS installed, it should
    // return a valid image. Either way, no crash.
    using var image = CreateSyntheticImage(128, 128);
    var dabs = new[] { new BrushDab(0.5, 0.5, 0.15, 1.0) };

    Image<Rgba32>? result = null;
    Assert.DoesNotThrow(() => {
      result = ContentAwareFill.Apply(image, dabs);
    }, "Apply with valid dabs should not throw regardless of model availability");

    // If the model was available and produced a result, it should match
    // the source dimensions.
    if (result is not null) {
      Assert.That(result.Width, Is.EqualTo(image.Width));
      Assert.That(result.Height, Is.EqualTo(image.Height));
      result.Dispose();
    }
  }

  [Test]
  public void HasAnyMaskedPixels_AllBlack_ReturnsFalse() {
    using var mask = new Image<Rgba32>(32, 32, new Rgba32(0, 0, 0, 255));
    Assert.That(ContentAwareFill.HasAnyMaskedPixels(mask), Is.False);
  }

  [Test]
  public void HasAnyMaskedPixels_SingleWhitePixel_ReturnsTrue() {
    using var mask = new Image<Rgba32>(32, 32, new Rgba32(0, 0, 0, 255));
    mask[16, 16] = new Rgba32(255, 255, 255, 255);
    Assert.That(ContentAwareFill.HasAnyMaskedPixels(mask), Is.True);
  }

  [Test]
  public void InpaintMaskType_NotConsideredIdentity_InDevelopSettings() {
    // A DevelopSettings with a single Inpaint local adjustment should
    // NOT be considered identity, even though the adjustment's sliders
    // are all zero.
    var settings = new DevelopSettings(LocalAdjustments: new[] {
      new LocalAdjustment(
        Mask: new LocalMask(Type: LocalMaskType.Inpaint,
          BrushDabs: new[] { new BrushDab(0.5, 0.5, 0.1, 1.0) }),
        Name: "Remove 1")
    });

    Assert.That(settings.IsIdentity, Is.False,
      "DevelopSettings with Inpaint adjustment should not be identity");
  }

  // --- helpers ---

  private static Image<Rgba32> CreateSyntheticImage(int w, int h) {
    var img = new Image<Rgba32>(w, h);
    img.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          // Simple gradient so the inpainter has some context to work with.
          var r = (byte)(x * 255 / w);
          var g = (byte)(y * 255 / h);
          var b = (byte)(128);
          row[x] = new Rgba32(r, g, b, 255);
        }
      }
    });
    return img;
  }

  private static Rgba32[] SnapshotPixels(Image<Rgba32> image) {
    var pixels = new Rgba32[image.Width * image.Height];
    image.CopyPixelDataTo(pixels);
    return pixels;
  }
}
