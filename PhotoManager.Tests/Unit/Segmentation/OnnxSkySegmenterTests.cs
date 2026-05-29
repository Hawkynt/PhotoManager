using PhotoManager.Core.Models;
using PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Segmentation;

[TestFixture]
public class OnnxSkySegmenterTests {
  [Test]
  public void IsAvailable_ModelMissing_ReturnsFalse() {
    var fakeModel = new FileInfo(Path.Combine(Path.GetTempPath(),
      "nonexistent-sky-" + Guid.NewGuid().ToString("N") + ".onnx"));
    using var segmenter = new OnnxSkySegmenter(fakeModel);
    Assert.That(segmenter.IsAvailable, Is.False);
  }

  [Test]
  public void SegmentSky_ModelMissing_ReturnsNull() {
    var fakeModel = new FileInfo(Path.Combine(Path.GetTempPath(),
      "nonexistent-sky-" + Guid.NewGuid().ToString("N") + ".onnx"));
    using var segmenter = new OnnxSkySegmenter(fakeModel);

    using var source = new Image<Rgba32>(64, 64);
    var result = segmenter.SegmentSky(source);

    Assert.That(result, Is.Null);
  }

  [Test]
  public async Task SegmentSkyAsync_ModelMissing_ReturnsNull() {
    var fakeModel = new FileInfo(Path.Combine(Path.GetTempPath(),
      "nonexistent-sky-" + Guid.NewGuid().ToString("N") + ".onnx"));
    using var segmenter = new OnnxSkySegmenter(fakeModel);

    using var source = new Image<Rgba32>(64, 64);
    var result = await segmenter.SegmentSkyAsync(source);

    Assert.That(result, Is.Null);
  }

  [Test]
  public void SegmentSky_ModelAvailable_OutputMatchesSourceDimensions() {
    var modelFile = ModelRegistry.SkySegmenter.ResolveDestination();
    if (!modelFile.Exists) {
      Assert.Inconclusive("Sky segmenter model not installed — skipping integration test.");
      return;
    }

    using var segmenter = new OnnxSkySegmenter(modelFile);
    if (!segmenter.IsAvailable) {
      Assert.Inconclusive("Sky segmenter model could not be loaded.");
      return;
    }

    using var source = new Image<Rgba32>(200, 150);
    using var result = segmenter.SegmentSky(source);

    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Width, Is.EqualTo(200));
    Assert.That(result.Height, Is.EqualTo(150));
  }

  [Test]
  public void SegmentSky_ModelAvailable_OutputIsValidBinaryMask() {
    var modelFile = ModelRegistry.SkySegmenter.ResolveDestination();
    if (!modelFile.Exists) {
      Assert.Inconclusive("Sky segmenter model not installed — skipping integration test.");
      return;
    }

    using var segmenter = new OnnxSkySegmenter(modelFile);
    if (!segmenter.IsAvailable) {
      Assert.Inconclusive("Sky segmenter model could not be loaded.");
      return;
    }

    using var source = new Image<Rgba32>(100, 100);
    using var result = segmenter.SegmentSky(source);

    Assert.That(result, Is.Not.Null);

    // Every pixel in the mask must be either 0 or 255.
    result!.ProcessPixelRows(accessor => {
      for (var y = 0; y < result.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < result.Width; x++)
          Assert.That(row[x].PackedValue, Is.EqualTo(0).Or.EqualTo(255),
            $"Pixel at ({x},{y}) has unexpected value {row[x].PackedValue}");
      }
    });
  }

  [Test]
  public void BuildInputTensor_ProducesCorrectSize() {
    using var image = new Image<Rgba32>(64, 64);
    var tensor = OnnxSkySegmenter.BuildInputTensor(image, 64);

    Assert.That(tensor.Length, Is.EqualTo(1 * 3 * 64 * 64));
  }

  [Test]
  public void BuildInputTensor_ValuesInZeroOneRange() {
    using var image = new Image<Rgba32>(32, 32);
    // Fill with known colour.
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < 32; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < 32; x++)
          row[x] = new Rgba32(128, 64, 255, 255);
      }
    });

    var tensor = OnnxSkySegmenter.BuildInputTensor(image, 32);

    foreach (var v in tensor) {
      Assert.That(v, Is.GreaterThanOrEqualTo(0f));
      Assert.That(v, Is.LessThanOrEqualTo(1f));
    }
  }

  [Test]
  public void Dispose_DoesNotThrow() {
    var fakeModel = new FileInfo(Path.Combine(Path.GetTempPath(),
      "nonexistent-sky-" + Guid.NewGuid().ToString("N") + ".onnx"));
    var segmenter = new OnnxSkySegmenter(fakeModel);
    Assert.DoesNotThrow(() => segmenter.Dispose());
  }
}
