using Hawkynt.PhotoManager.Core;

namespace Hawkynt.PhotoManager.Tests.Unit;

[TestFixture]
public class AppDataPathsTests {
  [Test]
  public void Root_ReturnsExistingDirectory() {
    var dir = AppDataPaths.Root();
    Assert.That(dir.Exists, Is.True);
    Assert.That(dir.FullName, Does.EndWith(AppDataPaths.ProductName));
  }

  [Test]
  public void SubDirectory_CreatesOnDemand() {
    var name = "test-subdir-" + Guid.NewGuid().ToString("N");
    var dir = AppDataPaths.SubDirectory(name);
    try {
      Assert.That(dir.Exists, Is.True);
      Assert.That(dir.Parent!.FullName, Is.EqualTo(AppDataPaths.Root().FullName));
    } finally {
      dir.Delete();
    }
  }

  [Test]
  public void ModelFile_ReturnsUnderModelsSubdirectory() {
    var file = AppDataPaths.ModelFile("sample.onnx");
    Assert.That(file.Directory!.Name, Is.EqualTo("models"));
    Assert.That(file.Name, Is.EqualTo("sample.onnx"));
  }
}
