using PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Segmentation;

[TestFixture]
public class BrushDabsFromAlphaMaskTests {
  [Test]
  public void AllZeroAlpha_ReturnsEmpty() {
    using var alpha = new Image<L8>(64, 64, new L8(0));
    var dabs = BrushDabsFromAlphaMask.Build(alpha);
    Assert.That(dabs, Is.Empty);
  }

  [Test]
  public void AllFullAlpha_ProducesGridSquaredDabsAtFullFlow() {
    const int gridSize = 8;
    using var alpha = new Image<L8>(64, 64, new L8(255));
    var dabs = BrushDabsFromAlphaMask.Build(alpha, gridSize: gridSize);

    Assert.That(dabs, Has.Count.EqualTo(gridSize * gridSize));
    foreach (var d in dabs)
      Assert.That(d.Flow, Is.EqualTo(1.0).Within(1e-6));
  }

  [Test]
  public void HalfAlpha_ProducesHalfFlow() {
    const int gridSize = 8;
    using var alpha = new Image<L8>(64, 64, new L8(128));
    var dabs = BrushDabsFromAlphaMask.Build(alpha, gridSize: gridSize);

    Assert.That(dabs, Has.Count.EqualTo(gridSize * gridSize));
    foreach (var d in dabs)
      Assert.That(d.Flow, Is.EqualTo(128.0 / 255.0).Within(1e-3));
  }

  [Test]
  public void BrightSquareInCenter_OnlyCenterCellsHaveDabs() {
    const int gridSize = 8;
    using var alpha = new Image<L8>(64, 64, new L8(0));

    // Paint a 16x16 bright square centred on the image (cells [3..4] in
    // both x and y of an 8x8 grid over a 64x64 image).
    alpha.ProcessPixelRows(accessor => {
      for (var y = 24; y < 40; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 24; x < 40; x++)
          row[x] = new L8(255);
      }
    });

    var dabs = BrushDabsFromAlphaMask.Build(alpha, gridSize: gridSize);

    Assert.That(dabs, Is.Not.Empty);
    foreach (var d in dabs) {
      // Centre two cells span normalised [3/8 .. 5/8] = [0.375 .. 0.625].
      Assert.That(d.X, Is.InRange(0.375, 0.625));
      Assert.That(d.Y, Is.InRange(0.375, 0.625));
    }
  }

  [Test]
  public void AlphaBelowThreshold_ReturnsEmpty() {
    using var alpha = new Image<L8>(32, 32, new L8(4));
    var dabs = BrushDabsFromAlphaMask.Build(alpha, gridSize: 8, threshold: 8);
    Assert.That(dabs, Is.Empty);
  }

  [Test]
  public void DabCoordinatesAreNormalised() {
    const int gridSize = 4;
    using var alpha = new Image<L8>(16, 16, new L8(255));
    var dabs = BrushDabsFromAlphaMask.Build(alpha, gridSize: gridSize);

    Assert.That(dabs, Is.Not.Empty);
    foreach (var d in dabs) {
      Assert.That(d.X, Is.InRange(0.0, 1.0));
      Assert.That(d.Y, Is.InRange(0.0, 1.0));
      Assert.That(d.Radius, Is.EqualTo(1.0 / gridSize).Within(1e-6));
    }

    // First dab is the (col=0, row=0) cell — centre at (0.5/gridSize, 0.5/gridSize).
    Assert.That(dabs[0].X, Is.EqualTo(0.5 / gridSize).Within(1e-6));
    Assert.That(dabs[0].Y, Is.EqualTo(0.5 / gridSize).Within(1e-6));
  }
}
