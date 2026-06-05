using Hawkynt.PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Segmentation;

[TestFixture]
public class OnnxArtifactRemoverTests {
  private static FileInfo NonexistentModel() => new(Path.Combine(
    Path.GetTempPath(),
    "nonexistent-fbcnn-model-" + Guid.NewGuid().ToString("N") + ".onnx"));

  [Test]
  public void ArtifactRemover_NoModel_IsNotAvailable() {
    using var remover = new OnnxArtifactRemover(NonexistentModel());
    Assert.That(remover.IsAvailable, Is.False);
  }

  [Test]
  public void Remove_NoModel_ReturnsNull() {
    using var remover = new OnnxArtifactRemover(NonexistentModel());
    using var image = new Image<Rgba32>(64, 64, new Rgba32(128, 128, 128));

    var result = remover.Remove(image, strength: 1.0);

    Assert.That(result, Is.Null);
  }
}
