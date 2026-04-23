using PhotoManager.Core.Faces;

namespace PhotoManager.Tests.Unit.Faces;

[TestFixture]
public class OnnxFaceEmbedderTests {
  [Test]
  public void L2Normalize_ZeroVector_ReturnsUnchanged() {
    var zero = new float[] { 0, 0, 0 };
    var result = OnnxFaceEmbedder.L2Normalize(zero);
    Assert.That(result, Is.EqualTo(zero).AsCollection);
  }

  [Test]
  public void L2Normalize_UnitVector_StaysUnit() {
    var unit = new float[] { 1f, 0f, 0f };
    var result = OnnxFaceEmbedder.L2Normalize(unit);
    Assert.That(result, Is.EqualTo(unit).AsCollection);
  }

  [Test]
  public void L2Normalize_ScalesToUnitMagnitude() {
    var v = new float[] { 3f, 4f, 0f };  // magnitude 5
    var result = OnnxFaceEmbedder.L2Normalize(v);

    double magnitude = 0;
    foreach (var x in result)
      magnitude += x * x;
    magnitude = Math.Sqrt(magnitude);

    Assert.That(magnitude, Is.EqualTo(1.0).Within(1e-6));
    Assert.That(result[0], Is.EqualTo(0.6f).Within(1e-6));
    Assert.That(result[1], Is.EqualTo(0.8f).Within(1e-6));
  }

  [Test]
  public void Embedder_NoModel_IsNotAvailable() {
    using var embedder = new OnnxFaceEmbedder(new FileInfo(Path.Combine(Path.GetTempPath(), "nonexistent-face-model-" + Guid.NewGuid().ToString("N") + ".onnx")));
    Assert.That(embedder.IsAvailable, Is.False);
  }

  [Test]
  public async Task EmbedFaceAsync_NoModel_ReturnsNull() {
    using var embedder = new OnnxFaceEmbedder(new FileInfo(Path.Combine(Path.GetTempPath(), "nonexistent-face-model-" + Guid.NewGuid().ToString("N") + ".onnx")));

    var fakeImage = new FileInfo(Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N") + ".jpg"));
    var result = await embedder.EmbedFaceAsync(fakeImage, new PhotoManager.Core.Detection.NormalizedBoundingBox(0, 0, 1, 1));

    Assert.That(result, Is.Null);
  }
}
