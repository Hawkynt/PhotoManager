using PhotoManager.Core.Enums;
using PhotoManager.Core.Models;
using PhotoManager.Core.Services;

namespace PhotoManager.Tests.Unit;

[TestFixture]
public class DuplicateHandlingTests {
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
  public async Task AreFilesIdentical_SameContent_ReturnsTrue() {
    // Arrange
    var file1 = Path.Combine(this._testDirectory, "file1.txt");
    var file2 = Path.Combine(this._testDirectory, "file2.txt");
    var content = "Test content for duplicate detection";
    
    await File.WriteAllTextAsync(file1, content);
    await File.WriteAllTextAsync(file2, content);

    // Act
    var result = await this._organizer.AreFilesIdenticalAsync(new FileInfo(file1), new FileInfo(file2));

    // Assert
    Assert.That(result, Is.True);
  }

  [Test]
  public async Task AreFilesIdentical_DifferentContent_ReturnsFalse() {
    // Arrange
    var file1 = Path.Combine(this._testDirectory, "file1.txt");
    var file2 = Path.Combine(this._testDirectory, "file2.txt");
    
    await File.WriteAllTextAsync(file1, "Content A");
    await File.WriteAllTextAsync(file2, "Content B");

    // Act
    var result = await this._organizer.AreFilesIdenticalAsync(new FileInfo(file1), new FileInfo(file2));

    // Assert
    Assert.That(result, Is.False);
  }

  [Test]
  public async Task AreFilesIdentical_LargeIdenticalFiles_ReturnsTrue() {
    // Arrange
    var file1 = Path.Combine(this._testDirectory, "large1.bin");
    var file2 = Path.Combine(this._testDirectory, "large2.bin");
    var largeContent = new byte[2 * 1024 * 1024]; // 2MB
    for (var i = 0; i < largeContent.Length; i++)
      largeContent[i] = (byte)(i % 256);
    
    await File.WriteAllBytesAsync(file1, largeContent);
    await File.WriteAllBytesAsync(file2, largeContent);

    // Act
    var result = await this._organizer.AreFilesIdenticalAsync(new FileInfo(file1), new FileInfo(file2));

    // Assert
    Assert.That(result, Is.True);
  }

  [Test]
  public async Task ProcessFile_SmartHandling_IdenticalFile_RemovesSource() {
    // Arrange
    var sourceFile = Path.Combine(this._testDirectory, "source.jpg");
    var targetDir = Path.Combine(this._testDirectory, "2024", "20240115");
    var targetFile = Path.Combine(targetDir, "143022.jpg");
    var content = "Identical photo content";
    
    Directory.CreateDirectory(targetDir);
    await File.WriteAllTextAsync(sourceFile, content);
    await File.WriteAllTextAsync(targetFile, content);
    
    var fileToImport = new FileToImport(new FileInfo(sourceFile));
    var dateTime = new DateTime(2024, 1, 15, 14, 30, 22);
    var settings = new ImportSettings {
      SourceDirectory = new DirectoryInfo(this._testDirectory),
      DuplicateHandling = DuplicateHandling.Smart,
      PreserveOriginals = false
    };

    // Act
    var (result, path, message) = await this._organizer.ProcessFileAsync(fileToImport, dateTime, settings);

    // Assert
    Assert.That(result, Is.EqualTo(FileOperationResult.DuplicateRemoved));
    Assert.That(File.Exists(sourceFile), Is.False, "Source file should be deleted");
    Assert.That(File.Exists(targetFile), Is.True, "Target file should remain");
    Assert.That(message, Does.Contain("Identical"));
  }

  [Test]
  public async Task ProcessFile_SmartHandling_DifferentFile_Renames() {
    // Arrange
    var sourceFile = Path.Combine(this._testDirectory, "source.jpg");
    var targetDir = Path.Combine(this._testDirectory, "2024", "20240115");
    var targetFile = Path.Combine(targetDir, "143022.jpg");
    var expectedRenamedFile = Path.Combine(targetDir, "143022 (2).jpg");
    
    Directory.CreateDirectory(targetDir);
    await File.WriteAllTextAsync(sourceFile, "Source content");
    await File.WriteAllTextAsync(targetFile, "Different content");
    
    var fileToImport = new FileToImport(new FileInfo(sourceFile));
    var dateTime = new DateTime(2024, 1, 15, 14, 30, 22);
    var settings = new ImportSettings {
      SourceDirectory = new DirectoryInfo(this._testDirectory),
      DuplicateHandling = DuplicateHandling.Smart,
      PreserveOriginals = false
    };

    // Act
    var (result, path, message) = await this._organizer.ProcessFileAsync(fileToImport, dateTime, settings);

    // Assert
    Assert.That(result, Is.EqualTo(FileOperationResult.Success));
    Assert.That(File.Exists(sourceFile), Is.False, "Source should be moved");
    Assert.That(File.Exists(expectedRenamedFile), Is.True, "File should be renamed");
    Assert.That(path?.FullName, Is.EqualTo(expectedRenamedFile));
  }

  [Test]
  public async Task ProcessFile_SkipHandling_ExistingFile_Skips() {
    // Arrange
    var sourceFile = Path.Combine(this._testDirectory, "source.jpg");
    var targetDir = Path.Combine(this._testDirectory, "2024", "20240115");
    var targetFile = Path.Combine(targetDir, "143022.jpg");
    
    Directory.CreateDirectory(targetDir);
    await File.WriteAllTextAsync(sourceFile, "Source content");
    await File.WriteAllTextAsync(targetFile, "Existing content");
    
    var fileToImport = new FileToImport(new FileInfo(sourceFile));
    var dateTime = new DateTime(2024, 1, 15, 14, 30, 22);
    var settings = new ImportSettings {
      SourceDirectory = new DirectoryInfo(this._testDirectory),
      DuplicateHandling = DuplicateHandling.Skip
    };

    // Act
    var (result, path, message) = await this._organizer.ProcessFileAsync(fileToImport, dateTime, settings);

    // Assert
    Assert.That(result, Is.EqualTo(FileOperationResult.Skipped));
    Assert.That(File.Exists(sourceFile), Is.True, "Source should remain");
    Assert.That(message, Does.Contain("skipped"));
  }

  [Test]
  public async Task ProcessFile_OverwriteHandling_ExistingFile_Overwrites() {
    // Arrange
    var sourceFile = Path.Combine(this._testDirectory, "source.jpg");
    var targetDir = Path.Combine(this._testDirectory, "2024", "20240115");
    var targetFile = Path.Combine(targetDir, "143022.jpg");
    var sourceContent = "New source content";
    
    Directory.CreateDirectory(targetDir);
    await File.WriteAllTextAsync(sourceFile, sourceContent);
    await File.WriteAllTextAsync(targetFile, "Old content");
    
    var fileToImport = new FileToImport(new FileInfo(sourceFile));
    var dateTime = new DateTime(2024, 1, 15, 14, 30, 22);
    var settings = new ImportSettings {
      SourceDirectory = new DirectoryInfo(this._testDirectory),
      DuplicateHandling = DuplicateHandling.Overwrite,
      PreserveOriginals = false
    };

    // Act
    var (result, path, message) = await this._organizer.ProcessFileAsync(fileToImport, dateTime, settings);

    // Assert
    Assert.That(result, Is.EqualTo(FileOperationResult.Success));
    Assert.That(File.Exists(sourceFile), Is.False, "Source should be moved");
    Assert.That(File.Exists(targetFile), Is.True, "Target should exist");
    Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(sourceContent), "Content should be overwritten");
  }

  [Test]
  public async Task ProcessFile_RenameHandling_ExistingFile_Renames() {
    // Arrange
    var sourceFile = Path.Combine(this._testDirectory, "source.jpg");
    var targetDir = Path.Combine(this._testDirectory, "2024", "20240115");
    var targetFile = Path.Combine(targetDir, "143022.jpg");
    var expectedRenamedFile = Path.Combine(targetDir, "143022 (2).jpg");
    
    Directory.CreateDirectory(targetDir);
    await File.WriteAllTextAsync(sourceFile, "Source content");
    await File.WriteAllTextAsync(targetFile, "Existing content");
    
    var fileToImport = new FileToImport(new FileInfo(sourceFile));
    var dateTime = new DateTime(2024, 1, 15, 14, 30, 22);
    var settings = new ImportSettings {
      SourceDirectory = new DirectoryInfo(this._testDirectory),
      DuplicateHandling = DuplicateHandling.Rename,
      PreserveOriginals = false
    };

    // Act
    var (result, path, message) = await this._organizer.ProcessFileAsync(fileToImport, dateTime, settings);

    // Assert
    Assert.That(result, Is.EqualTo(FileOperationResult.Success));
    Assert.That(File.Exists(sourceFile), Is.False, "Source should be moved");
    Assert.That(File.Exists(expectedRenamedFile), Is.True, "File should be renamed");
    Assert.That(path?.FullName, Is.EqualTo(expectedRenamedFile));
  }

  [Test]
  public async Task ProcessFile_DryRun_IdenticalFile_PreviewsSkip() {
    // Arrange
    var sourceFile = Path.Combine(this._testDirectory, "source.jpg");
    var targetDir = Path.Combine(this._testDirectory, "2024", "20240115");
    var targetFile = Path.Combine(targetDir, "143022.jpg");
    var content = "Identical content";
    
    Directory.CreateDirectory(targetDir);
    await File.WriteAllTextAsync(sourceFile, content);
    await File.WriteAllTextAsync(targetFile, content);
    
    var fileToImport = new FileToImport(new FileInfo(sourceFile));
    var dateTime = new DateTime(2024, 1, 15, 14, 30, 22);
    var settings = new ImportSettings {
      SourceDirectory = new DirectoryInfo(this._testDirectory),
      DuplicateHandling = DuplicateHandling.Smart,
      DryRun = true
    };

    // Act
    var (result, path, message) = await this._organizer.ProcessFileAsync(fileToImport, dateTime, settings);

    // Assert
    Assert.That(result, Is.EqualTo(FileOperationResult.Success));
    Assert.That(File.Exists(sourceFile), Is.True, "Source should remain in dry run");
    Assert.That(message, Does.Contain("Would skip"));
  }
}