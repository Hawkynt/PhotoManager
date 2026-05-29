using PhotoManager.UI.Views;

namespace PhotoManager.Tests.Unit.UI;

[TestFixture]
public sealed class CompareGridLayoutTests {
  [Test]
  public void TwoPhotos_Returns2Columns1Row() {
    var (cols, rows) = CompareGridLayout.ComputeGridSize(2);
    Assert.Multiple(() => {
      Assert.That(cols, Is.EqualTo(2));
      Assert.That(rows, Is.EqualTo(1));
    });
  }

  [Test]
  public void ThreePhotos_Returns2Columns2Rows() {
    var (cols, rows) = CompareGridLayout.ComputeGridSize(3);
    Assert.Multiple(() => {
      Assert.That(cols, Is.EqualTo(2));
      Assert.That(rows, Is.EqualTo(2));
    });
  }

  [Test]
  public void FourPhotos_Returns2x2() {
    var (cols, rows) = CompareGridLayout.ComputeGridSize(4);
    Assert.Multiple(() => {
      Assert.That(cols, Is.EqualTo(2));
      Assert.That(rows, Is.EqualTo(2));
    });
  }

  [Test]
  public void FivePhotos_Returns2Columns3Rows() {
    var (cols, rows) = CompareGridLayout.ComputeGridSize(5);
    Assert.Multiple(() => {
      Assert.That(cols, Is.EqualTo(2));
      Assert.That(rows, Is.EqualTo(3));
    });
  }

  [Test]
  public void SixPhotos_Returns2Columns3Rows() {
    var (cols, rows) = CompareGridLayout.ComputeGridSize(6);
    Assert.Multiple(() => {
      Assert.That(cols, Is.EqualTo(2));
      Assert.That(rows, Is.EqualTo(3));
    });
  }

  [Test]
  public void SevenPhotos_Returns3x3() {
    var (cols, rows) = CompareGridLayout.ComputeGridSize(7);
    Assert.Multiple(() => {
      Assert.That(cols, Is.EqualTo(3));
      Assert.That(rows, Is.EqualTo(3));
    });
  }

  [Test]
  public void EightPhotos_Returns3x3() {
    var (cols, rows) = CompareGridLayout.ComputeGridSize(8);
    Assert.Multiple(() => {
      Assert.That(cols, Is.EqualTo(3));
      Assert.That(rows, Is.EqualTo(3));
    });
  }

  [Test]
  public void NinePhotos_Returns3x3() {
    var (cols, rows) = CompareGridLayout.ComputeGridSize(9);
    Assert.Multiple(() => {
      Assert.That(cols, Is.EqualTo(3));
      Assert.That(rows, Is.EqualTo(3));
    });
  }

  [Test]
  public void BelowMinimum_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(() => CompareGridLayout.ComputeGridSize(1));
    Assert.Throws<ArgumentOutOfRangeException>(() => CompareGridLayout.ComputeGridSize(0));
    Assert.Throws<ArgumentOutOfRangeException>(() => CompareGridLayout.ComputeGridSize(-1));
  }

  [Test]
  public void AboveMaximum_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(() => CompareGridLayout.ComputeGridSize(10));
    Assert.Throws<ArgumentOutOfRangeException>(() => CompareGridLayout.ComputeGridSize(100));
  }

  [TestCase(2, 2)]
  [TestCase(3, 4)]
  [TestCase(4, 4)]
  [TestCase(5, 6)]
  [TestCase(6, 6)]
  [TestCase(7, 9)]
  [TestCase(8, 9)]
  [TestCase(9, 9)]
  public void GridHasEnoughCells(int photoCount, int expectedCells) {
    var (cols, rows) = CompareGridLayout.ComputeGridSize(photoCount);
    Assert.That(cols * rows, Is.EqualTo(expectedCells));
  }

  [TestCase(2)]
  [TestCase(3)]
  [TestCase(4)]
  [TestCase(5)]
  [TestCase(6)]
  [TestCase(7)]
  [TestCase(8)]
  [TestCase(9)]
  public void GridHasEnoughCellsForAllPhotos(int photoCount) {
    var (cols, rows) = CompareGridLayout.ComputeGridSize(photoCount);
    Assert.That(cols * rows, Is.GreaterThanOrEqualTo(photoCount));
  }
}
