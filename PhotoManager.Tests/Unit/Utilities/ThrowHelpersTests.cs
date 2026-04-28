using PhotoManager.Core.Utilities;

namespace PhotoManager.Tests.Unit.Utilities;

[TestFixture]
public class ThrowHelpersTests {
  [Test]
  public void ThrowDirectoryNotFoundException_AlwaysThrows_WithPathInMessage() {
    var ex = Assert.Throws<DirectoryNotFoundException>(() =>
      ThrowHelpers.ThrowDirectoryNotFoundException(@"C:\nope"));
    Assert.That(ex!.Message, Does.Contain(@"C:\nope"));
  }

  [Test]
  public void ThrowFileNotFoundException_AlwaysThrows_WithPathPropertyAndMessage() {
    var ex = Assert.Throws<FileNotFoundException>(() =>
      ThrowHelpers.ThrowFileNotFoundException(@"C:\missing.jpg"));
    Assert.Multiple(() => {
      Assert.That(ex!.Message, Does.Contain("missing.jpg"));
      Assert.That(ex.FileName, Is.EqualTo(@"C:\missing.jpg"));
    });
  }

  [Test]
  public void ThrowIfDirectoryNotExists_NullDirectory_ThrowsArgumentNull() {
    Assert.Throws<ArgumentNullException>(() =>
      ThrowHelpers.ThrowIfDirectoryNotExists(null!));
  }

  [Test]
  public void ThrowIfDirectoryNotExists_MissingDirectory_ThrowsDirectoryNotFound() {
    var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
    Assert.Throws<DirectoryNotFoundException>(() =>
      ThrowHelpers.ThrowIfDirectoryNotExists(dir));
  }

  [Test]
  public void ThrowIfDirectoryNotExists_ExistingDirectory_DoesNotThrow() {
    var dir = new DirectoryInfo(Path.GetTempPath());
    Assert.DoesNotThrow(() => ThrowHelpers.ThrowIfDirectoryNotExists(dir));
  }

  [Test]
  public void ThrowIfFileNotExists_NullFile_ThrowsArgumentNull() {
    Assert.Throws<ArgumentNullException>(() => ThrowHelpers.ThrowIfFileNotExists(null!));
  }

  [Test]
  public void ThrowIfFileNotExists_MissingFile_ThrowsFileNotFound() {
    var file = new FileInfo(Path.Combine(Path.GetTempPath(), $"definitely-missing-{Guid.NewGuid():N}.jpg"));
    Assert.Throws<FileNotFoundException>(() => ThrowHelpers.ThrowIfFileNotExists(file));
  }

  [Test]
  public void ThrowIfFileNotExists_ExistingFile_DoesNotThrow() {
    var path = Path.Combine(Path.GetTempPath(), $"throw-helper-{Guid.NewGuid():N}.tmp");
    File.WriteAllText(path, "x");
    try {
      Assert.DoesNotThrow(() => ThrowHelpers.ThrowIfFileNotExists(new FileInfo(path)));
    } finally {
      File.Delete(path);
    }
  }
}
