using Hawkynt.PhotoManager.Core.Detection;
using Hawkynt.PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Develop;

[TestFixture]
public class RedEyeFinderTests {
  private static Image<Rgba32> WithRect(int w, int h, Rgba32 background, int x0, int y0, int x1, int y1, Rgba32 fill) {
    var img = new Image<Rgba32>(w, h);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          row[x] = (x >= x0 && x < x1 && y >= y0 && y < y1) ? fill : background;
        }
      }
    });
    return img;
  }

  [Test]
  public void Find_RedBlobInsideFaceBox_ReturnsExactlyOne() {
    using var img = WithRect(80, 80, new Rgba32(120, 120, 120, 255),
      x0: 30, y0: 30, x1: 38, y1: 38,
      fill: new Rgba32(220, 30, 30, 255));
    var face = new NormalizedBoundingBox(0.2f, 0.2f, 0.6f, 0.6f);
    var hits = RedEyeFinder.Find(img, new[] { face });
    Assert.That(hits.Count, Is.EqualTo(1));
  }

  [Test]
  public void Find_NoRedPixels_ReturnsEmpty() {
    using var img = WithRect(40, 40, new Rgba32(80, 80, 80, 255), 0, 0, 0, 0, default);
    var hits = RedEyeFinder.Find(img);
    Assert.That(hits, Is.Empty);
  }

  [Test]
  public void Find_TinySingleRedPixel_BelowMinClusterSize_Filtered() {
    using var img = WithRect(40, 40, new Rgba32(120, 120, 120, 255),
      x0: 10, y0: 10, x1: 11, y1: 11,
      fill: new Rgba32(220, 20, 20, 255));
    var hits = RedEyeFinder.Find(img);
    Assert.That(hits, Is.Empty);
  }

  [Test]
  public void Find_HugeRedRegion_AboveMaxClusterSize_Filtered() {
    using var img = WithRect(80, 80, new Rgba32(120, 120, 120, 255),
      x0: 5, y0: 5, x1: 60, y1: 60,
      fill: new Rgba32(220, 20, 20, 255));
    var hits = RedEyeFinder.Find(img);
    Assert.That(hits, Is.Empty);
  }

  [Test]
  public void Find_NoFaceBounds_SearchesWholeImage() {
    using var img = WithRect(80, 80, new Rgba32(120, 120, 120, 255),
      x0: 60, y0: 60, x1: 68, y1: 68,
      fill: new Rgba32(220, 20, 20, 255));
    var hits = RedEyeFinder.Find(img);
    Assert.That(hits.Count, Is.EqualTo(1));
  }
}
