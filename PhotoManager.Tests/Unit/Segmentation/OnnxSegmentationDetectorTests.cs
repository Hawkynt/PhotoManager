using PhotoManager.Core.Segmentation;

namespace PhotoManager.Tests.Unit.Segmentation;

[TestFixture]
public class OnnxSegmentationDetectorTests {
  [Test]
  public void IsAvailable_ModelMissing_ReturnsFalse() {
    var fakeModel = new FileInfo(Path.Combine(Path.GetTempPath(),
      "nonexistent-modnet-" + Guid.NewGuid().ToString("N") + ".onnx"));
    using var detector = new OnnxSegmentationDetector(fakeModel);
    Assert.That(detector.IsAvailable, Is.False);
  }

  [Test]
  public async Task SegmentAsync_ModelMissing_ReturnsNull() {
    var fakeModel = new FileInfo(Path.Combine(Path.GetTempPath(),
      "nonexistent-modnet-" + Guid.NewGuid().ToString("N") + ".onnx"));
    using var detector = new OnnxSegmentationDetector(fakeModel);

    var fakeImage = new FileInfo(Path.Combine(Path.GetTempPath(),
      "nonexistent-" + Guid.NewGuid().ToString("N") + ".jpg"));
    var result = await detector.SegmentAsync(fakeImage);

    Assert.That(result, Is.Null);
  }

  [Test]
  public async Task SegmentAsync_ImageMissing_ReturnsNull() {
    // Even without a real model file, a non-existent image short-circuits
    // before the session is touched.
    var fakeModel = new FileInfo(Path.Combine(Path.GetTempPath(),
      "nonexistent-modnet-" + Guid.NewGuid().ToString("N") + ".onnx"));
    using var detector = new OnnxSegmentationDetector(fakeModel);

    var fakeImage = new FileInfo(Path.Combine(Path.GetTempPath(),
      "nonexistent-image-" + Guid.NewGuid().ToString("N") + ".jpg"));
    var result = await detector.SegmentAsync(fakeImage);

    Assert.That(result, Is.Null);
  }
}
