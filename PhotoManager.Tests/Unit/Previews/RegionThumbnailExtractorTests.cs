using Hawkynt.PhotoManager.Core.Detection;
using Hawkynt.PhotoManager.Core.Previews;
using Hawkynt.PhotoManager.Tests.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Previews;

[TestFixture]
public class RegionThumbnailExtractorTests {
  private DirectoryInfo _tempDir = null!;

  [SetUp]
  public void SetUp() {
    this._tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-thumb-" + Guid.NewGuid().ToString("N")));
    this._tempDir.Create();
  }

  [TearDown]
  public void TearDown() {
    if (this._tempDir.Exists)
      this._tempDir.Delete(recursive: true);
  }

  [Test]
  public async Task CropAsync_ReturnsValidJpegBytes() {
    var path = Path.Combine(this._tempDir.FullName, "big.jpg");
    TestJpegFactory.Write(path, width: 100, height: 100);

    var bytes = await RegionThumbnailExtractor.CropAsync(
      new FileInfo(path),
      new NormalizedBoundingBox(0.25f, 0.25f, 0.5f, 0.5f));

    Assert.That(bytes, Is.Not.Null);
    Assert.That(bytes!.Length, Is.GreaterThan(0));
    // JPEG SOI marker.
    Assert.That(bytes[0], Is.EqualTo(0xFF));
    Assert.That(bytes[1], Is.EqualTo(0xD8));
  }

  [Test]
  public async Task CropAsync_ScalesDownLargeImage_ToMaxEdgePixels() {
    // Source 1000x1000; full-image box → crop is also 1000x1000 + margin,
    // resized so the longest edge ≤ MaxEdgePixels (240).
    var path = Path.Combine(this._tempDir.FullName, "huge.jpg");
    TestJpegFactory.Write(path, width: 1000, height: 1000);

    var bytes = await RegionThumbnailExtractor.CropAsync(
      new FileInfo(path),
      new NormalizedBoundingBox(0, 0, 1, 1));

    Assert.That(bytes, Is.Not.Null);
    using var ms = new MemoryStream(bytes!);
    using var resized = await Image.LoadAsync<Rgb24>(ms);
    Assert.That(Math.Max(resized.Width, resized.Height),
      Is.LessThanOrEqualTo(RegionThumbnailExtractor.MaxEdgePixels));
  }

  [Test]
  public async Task CropAsync_MissingFile_ReturnsNull() {
    var path = Path.Combine(this._tempDir.FullName, "ghost.jpg");
    var bytes = await RegionThumbnailExtractor.CropAsync(
      new FileInfo(path),
      new NormalizedBoundingBox(0, 0, 0.5f, 0.5f));
    Assert.That(bytes, Is.Null);
  }

  [Test]
  public async Task CropAsync_DegenerateBox_ReturnsNull() {
    var path = Path.Combine(this._tempDir.FullName, "tiny.jpg");
    TestJpegFactory.Write(path, width: 50, height: 50);

    // A box with zero width/height collapses to a 0x0 crop after margin
    // and is rejected.
    var bytes = await RegionThumbnailExtractor.CropAsync(
      new FileInfo(path),
      new NormalizedBoundingBox(0, 0, 0, 0));
    Assert.That(bytes, Is.Null);
  }
}
