using NUnit.Framework;
using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
[Category("Unit")]
public sealed class HealingBrushTests {
  [Test]
  public void Apply_SourceEqualsDestination_ImageUnchanged() {
    // When source == destination the clone copies pixels onto themselves,
    // so the image should remain identical.
    using var image = CreateCheckerboard(64, 64);
    var before = SnapshotPixels(image);
    HealingBrush.Apply(image, 32, 32, 32, 32, 10);
    var after = SnapshotPixels(image);
    Assert.That(after, Is.EqualTo(before), "source == destination should leave the image unchanged");
  }

  [Test]
  public void Apply_CopiesPixelsFromSourceToDestination() {
    // Paint the left half red and the right half blue, then clone from
    // the red region to the blue region. The destination centre should
    // become red.
    using var image = new Image<Rgba32>(100, 100);
    var red = new Rgba32(255, 0, 0, 255);
    var blue = new Rgba32(0, 0, 255, 255);
    image.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = x < 50 ? red : blue;
      }
    });

    // Clone from centre of the red half (25,50) to centre of the blue half (75,50)
    HealingBrush.Apply(image, srcX: 25, srcY: 50, dstX: 75, dstY: 50, radius: 10);

    // The destination centre should now be red (fully inside the disc, no feather).
    var pixel = image[75, 50];
    Assert.That(pixel.R, Is.EqualTo(255), "destination centre R should be cloned from source");
    Assert.That(pixel.G, Is.EqualTo(0), "destination centre G should be cloned from source");
    Assert.That(pixel.B, Is.EqualTo(0), "destination centre B should be cloned from source");
  }

  [Test]
  public void Apply_FeatheredEdge_CentreMatchesSource_EdgeIsBlended() {
    // Use a large enough radius so the feather zone is meaningful.
    using var image = new Image<Rgba32>(200, 200);
    var white = new Rgba32(255, 255, 255, 255);
    var black = new Rgba32(0, 0, 0, 255);
    // Fill entire image black.
    image.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = black;
      }
    });
    // Paint source region white.
    image.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < 100; x++)
          row[x] = white;
      }
    });

    var radius = 20;
    HealingBrush.Apply(image, srcX: 50, srcY: 100, dstX: 150, dstY: 100, radius: radius);

    // Centre of destination disc: should be pure white (fully inside, no feather).
    var centre = image[150, 100];
    Assert.That(centre.R, Is.EqualTo(255), "disc centre should be fully cloned (white)");

    // Edge of destination disc: a pixel at radius-1 distance should be partially blended.
    // The feather starts at 0.7*radius. At radius-1, the smoothstep should give a
    // non-trivial weight (between 0 and 1).
    var edgeX = 150 + radius - 1;
    if (edgeX < image.Width) {
      var edge = image[edgeX, 100];
      // The pixel should be between pure white (255) and pure black (0).
      // With feather, white * (1-weight) + black * weight, so the result
      // is less than 255 but greater than 0.
      Assert.That(edge.R, Is.GreaterThan(0).And.LessThan(255),
        $"edge pixel at ({edgeX},100) should be partially blended, got R={edge.R}");
    }
  }

  [Test]
  public void Apply_OutOfBoundsSource_DoesNotThrow() {
    using var image = CreateCheckerboard(50, 50);
    // Source is way off the image — should clamp gracefully.
    Assert.DoesNotThrow(() => HealingBrush.Apply(image, srcX: -100, srcY: -100, dstX: 25, dstY: 25, radius: 5));
  }

  [Test]
  public void Apply_OutOfBoundsDestination_DoesNotThrow() {
    using var image = CreateCheckerboard(50, 50);
    // Destination is mostly off the image — should clamp gracefully.
    Assert.DoesNotThrow(() => HealingBrush.Apply(image, srcX: 25, srcY: 25, dstX: 48, dstY: 48, radius: 10));
  }

  [Test]
  public void Apply_FullyOutOfBoundsDestination_DoesNotThrow() {
    using var image = CreateCheckerboard(50, 50);
    // Destination is completely off the image — should be a no-op.
    Assert.DoesNotThrow(() => HealingBrush.Apply(image, srcX: 25, srcY: 25, dstX: 200, dstY: 200, radius: 5));
  }

  [Test]
  public void Apply_ZeroRadius_IsNoOp() {
    using var image = CreateCheckerboard(64, 64);
    var before = SnapshotPixels(image);
    HealingBrush.Apply(image, 10, 10, 50, 50, 0);
    var after = SnapshotPixels(image);
    Assert.That(after, Is.EqualTo(before), "zero radius should be a no-op");
  }

  [Test]
  public void Apply_NegativeRadius_IsNoOp() {
    using var image = CreateCheckerboard(64, 64);
    var before = SnapshotPixels(image);
    HealingBrush.Apply(image, 10, 10, 50, 50, -5);
    var after = SnapshotPixels(image);
    Assert.That(after, Is.EqualTo(before), "negative radius should be a no-op");
  }

  [Test]
  public void Stroke_ApplyAndUndo_RestoresOriginal() {
    using var image = new Image<Rgba32>(100, 100);
    var red = new Rgba32(255, 0, 0, 255);
    var blue = new Rgba32(0, 0, 255, 255);
    image.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = x < 50 ? red : blue;
      }
    });
    var before = SnapshotPixels(image);

    var stroke = new HealingBrushStroke(deltaX: -50, deltaY: 0);
    stroke.AddStamp(75, 50, 10);
    stroke.CaptureUndo(image);
    stroke.Apply(image);

    // After Apply, the destination should have changed.
    var after = SnapshotPixels(image);
    Assert.That(after, Is.Not.EqualTo(before), "stroke should modify the image");

    // Undo should restore the original.
    stroke.Undo(image);
    var restored = SnapshotPixels(image);
    Assert.That(restored, Is.EqualTo(before), "undo should restore the original pixels");
  }

  // --- helpers ---

  private static Image<Rgba32> CreateCheckerboard(int w, int h) {
    var img = new Image<Rgba32>(w, h);
    img.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var v = (byte)(((x / 8) + (y / 8)) % 2 == 0 ? 200 : 50);
          row[x] = new Rgba32(v, v, v, 255);
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
