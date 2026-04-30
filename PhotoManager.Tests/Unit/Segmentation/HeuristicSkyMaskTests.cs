using PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Segmentation;

[TestFixture]
public class HeuristicSkyMaskTests {
  private static Image<Rgba32> Solid(int w, int h, Rgba32 color) {
    var img = new Image<Rgba32>(w, h);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = color;
      }
    });
    return img;
  }

  private static Image<Rgba32> CheckerBoard(int w, int h, Rgba32 a, Rgba32 b, int cell) {
    var img = new Image<Rgba32>(w, h);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = ((x / cell + y / cell) & 1) == 0 ? a : b;
      }
    });
    return img;
  }

  [Test]
  public void Build_BlueImage_ReturnsManyDabsInTopHalf() {
    using var img = Solid(128, 128, new Rgba32(120, 160, 220, 255));
    var dabs = HeuristicSkyMask.Build(img);
    Assert.That(dabs, Is.Not.Empty);
    // All dabs should land in the top 60% region.
    foreach (var dab in dabs)
      Assert.That(dab.Y, Is.LessThanOrEqualTo(0.65));
  }

  [Test]
  public void Build_BlackImage_ReturnsEmpty() {
    using var img = Solid(64, 64, new Rgba32(0, 0, 0, 255));
    var dabs = HeuristicSkyMask.Build(img);
    Assert.That(dabs, Is.Empty);
  }

  [Test]
  public void Build_RedOrangeImage_ReturnsEmpty() {
    using var img = Solid(64, 64, new Rgba32(220, 120, 60, 255));
    var dabs = HeuristicSkyMask.Build(img);
    Assert.That(dabs, Is.Empty);
  }

  [Test]
  public void Build_TexturedBluePattern_RejectsHighEdgePixels() {
    using var img = CheckerBoard(64, 64,
      new Rgba32(120, 160, 220, 255),
      new Rgba32(40, 60, 90, 255),
      cell: 2);
    // The checker has high edge magnitude everywhere, so the heuristic
    // should mostly reject it. We expect FEWER dabs than a uniform-blue
    // image of the same dimensions.
    using var uniform = Solid(64, 64, new Rgba32(120, 160, 220, 255));
    var checkerDabs = HeuristicSkyMask.Build(img);
    var uniformDabs = HeuristicSkyMask.Build(uniform);
    Assert.That(checkerDabs.Count, Is.LessThan(uniformDabs.Count));
  }

  [Test]
  public void Build_MidSaturationBluePatch_ProducesSomeDabs() {
    using var img = Solid(64, 64, new Rgba32(140, 170, 210, 255));
    var dabs = HeuristicSkyMask.Build(img);
    Assert.That(dabs, Is.Not.Empty);
  }
}
