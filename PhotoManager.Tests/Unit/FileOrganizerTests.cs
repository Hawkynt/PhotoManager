using PhotoManager.Core.Models;
using PhotoManager.Core.Services;

namespace PhotoManager.Tests.Unit;

[TestFixture]
public class FileOrganizerTests {
  private FileOrganizer _organizer;
  private string _testDirectory;

  [SetUp]
  public void Setup() {
    this._organizer = new FileOrganizer();
    this._testDirectory = Path.Combine(Path.GetTempPath(), $"PhotoManagerTest_{Guid.NewGuid()}");
    Directory.CreateDirectory(this._testDirectory);
  }

  [TearDown]
  public void TearDown() {
    if (Directory.Exists(this._testDirectory))
      Directory.Delete(this._testDirectory, true);
  }

  [Test]
  public async Task GenerateTargetPath_DefaultPattern_CreatesCorrectPath() {
    // Arrange
    var fileInfo = new FileInfo(Path.Combine(this._testDirectory, "test.jpg"));
    File.WriteAllText(fileInfo.FullName, "test");
    var file = new FileToImport(fileInfo);
    var dateTime = new DateTime(2024, 1, 15, 14, 30, 22);
    var settings = new ImportSettings {
      SourceDirectory = new DirectoryInfo(this._testDirectory),
      DateFormatPattern = "yyyy/yyyyMMdd/HHmmss"
    };

    // Act
    var targetPath = await this._organizer.GenerateTargetPath(file, dateTime, settings);

    // Assert
    Assert.That(targetPath, Does.Contain("2024"));
    Assert.That(targetPath, Does.Contain("20240115"));
    Assert.That(targetPath, Does.EndWith("143022.jpg"));
  }

  [Test]
  public async Task GenerateTargetPath_CustomPattern_CreatesCorrectPath() {
    // Arrange
    var fileInfo = new FileInfo(Path.Combine(this._testDirectory, "photo.png"));
    File.WriteAllText(fileInfo.FullName, "test");
    var file = new FileToImport(fileInfo);
    var dateTime = new DateTime(2023, 12, 25, 18, 45, 30);
    var settings = new ImportSettings {
      SourceDirectory = new DirectoryInfo(this._testDirectory),
      DateFormatPattern = "yyyy-MM/dd/HH-mm-ss"
    };

    // Act
    var targetPath = await this._organizer.GenerateTargetPath(file, dateTime, settings);

    // Assert
    Assert.That(targetPath, Does.Contain("2023-12"));
    Assert.That(targetPath, Does.Contain("25"));
    Assert.That(targetPath, Does.EndWith("18-45-30.png"));
  }

  [Test]
  public async Task MoveFileAsync_ValidPaths_MovesFile() {
    // Arrange
    var sourcePath = Path.Combine(this._testDirectory, "source.txt");
    var targetDir = Path.Combine(this._testDirectory, "target");
    var targetPath = Path.Combine(targetDir, "moved.txt");
    File.WriteAllText(sourcePath, "test content");

    // Act
    var result = await this._organizer.MoveFileAsync(sourcePath, targetPath);

    // Assert
    Assert.That(result, Is.True);
    Assert.That(File.Exists(targetPath), Is.True);
    Assert.That(File.Exists(sourcePath), Is.False);
    Assert.That(File.ReadAllText(targetPath), Is.EqualTo("test content"));
  }

  [Test]
  public async Task CopyFileAsync_ValidPaths_CopiesFile() {
    // Arrange
    var sourcePath = Path.Combine(this._testDirectory, "source.txt");
    var targetDir = Path.Combine(this._testDirectory, "target");
    var targetPath = Path.Combine(targetDir, "copied.txt");
    File.WriteAllText(sourcePath, "test content");

    // Act
    var result = await this._organizer.CopyFileAsync(sourcePath, targetPath);

    // Assert
    Assert.That(result, Is.True);
    Assert.That(File.Exists(targetPath), Is.True);
    Assert.That(File.Exists(sourcePath), Is.True);
    Assert.That(File.ReadAllText(targetPath), Is.EqualTo("test content"));
  }

  [Test]
  public async Task MoveFileAsync_FileExists_NoOverwrite_ReturnsFalse() {
    // Arrange
    var sourcePath = Path.Combine(this._testDirectory, "source.txt");
    var targetPath = Path.Combine(this._testDirectory, "target.txt");
    File.WriteAllText(sourcePath, "source content");
    File.WriteAllText(targetPath, "existing content");

    // Act
    var result = await this._organizer.MoveFileAsync(sourcePath, targetPath, overwrite: false);

    // Assert
    Assert.That(result, Is.False);
    Assert.That(File.Exists(sourcePath), Is.True);
    Assert.That(File.ReadAllText(targetPath), Is.EqualTo("existing content"));
  }
}
