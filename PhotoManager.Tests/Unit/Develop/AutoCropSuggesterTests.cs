using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
public class AutoCropSuggesterTests {
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

  /// <summary>Image that's flat everywhere except a small high-contrast block at the centre.</summary>
  private static Image<Rgba32> CenteredObject(int width, int height) {
    var img = SolidColor(width, height, new Rgba32(128, 128, 128, 255));
    var blockSize = Math.Min(width, height) / 4;
    var startX = (width - blockSize) / 2;
    var startY = (height - blockSize) / 2;
    img.ProcessPixelRows(accessor => {
      for (var y = startY; y < startY + blockSize; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = startX; x < startX + blockSize; x++)
          row[x] = new Rgba32(255, 0, 0, 255);
      }
    });
    return img;
  }

  [Test]
  public void Suggest_BlankImage_ReturnsZeroScoreAllAspects() {
    using var img = SolidColor(200, 200, new Rgba32(128, 128, 128, 255));
    var aspects = new[] { 1.0, 1.5, 0.8 };
    var results = AutoCropSuggester.Suggest(img, aspects);
    Assert.That(results, Has.Count.EqualTo(aspects.Length));
    foreach (var r in results) {
      Assert.That(r.Score, Is.EqualTo(0).Within(1e-6));
    }
  }

  [Test]
  public void Suggest_CenteredObject_AllAspectsCenterOnIt() {
    using var img = CenteredObject(400, 400);
    var aspects = new[] { 1.0, 1.5, 0.667 };
    var results = AutoCropSuggester.Suggest(img, aspects);
    Assert.That(results, Has.Count.EqualTo(3));

    foreach (var r in results) {
      var cx = (r.Left + r.Right) / 2;
      var cy = (r.Top + r.Bottom) / 2;
      // Centre of crop should sit close to image centre (0.5, 0.5).
      Assert.That(cx, Is.EqualTo(0.5).Within(0.15), $"aspect {r.AspectRatio} cx");
      Assert.That(cy, Is.EqualTo(0.5).Within(0.15), $"aspect {r.AspectRatio} cy");
      Assert.That(r.Score, Is.GreaterThan(0));
    }
  }

  [Test]
  public void Suggest_DifferentAspects_ProduceDifferentBounds() {
    using var img = CenteredObject(600, 400);
    var aspects = new[] { 1.0, 1.778 };  // square vs 16:9
    var results = AutoCropSuggester.Suggest(img, aspects);
    Assert.That(results, Has.Count.EqualTo(2));
    var square = results[0];
    var wide = results[1];

    var squareW = square.Right - square.Left;
    var squareH = square.Bottom - square.Top;
    var wideW = wide.Right - wide.Left;
    var wideH = wide.Bottom - wide.Top;

    // Width of 16:9 crop in normalised coords > width of 1:1 crop on a 600x400.
    Assert.That(wideW, Is.GreaterThan(squareW));
    Assert.That(wideH, Is.LessThanOrEqualTo(squareH));
  }

  [Test]
  public void Suggest_NormalizedCoords_AreInRange() {
    using var img = CenteredObject(320, 240);
    var results = AutoCropSuggester.Suggest(img, AutoCropSuggester.DefaultAspectRatios);
    foreach (var r in results) {
      Assert.That(r.Left, Is.InRange(0, 1));
      Assert.That(r.Right, Is.InRange(0, 1));
      Assert.That(r.Top, Is.InRange(0, 1));
      Assert.That(r.Bottom, Is.InRange(0, 1));
      Assert.That(r.Right, Is.GreaterThan(r.Left));
      Assert.That(r.Bottom, Is.GreaterThan(r.Top));
    }
  }

  [Test]
  public void Suggest_EmptyAspects_ReturnsEmpty() {
    using var img = SolidColor(50, 50, new Rgba32(0, 0, 0, 255));
    var results = AutoCropSuggester.Suggest(img, Array.Empty<double>());
    Assert.That(results, Is.Empty);
  }
}
