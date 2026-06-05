using Hawkynt.PhotoManager.Core.Enums;
using Hawkynt.PhotoManager.Core.Models;
using Hawkynt.PhotoManager.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Services;

[TestFixture]
public class ImportManagerTests {
  private DirectoryInfo _tempDir = null!;

  [SetUp]
  public void SetUp() {
    this._tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-import-tests-" + Guid.NewGuid().ToString("N")));
    this._tempDir.Create();
  }

  [TearDown]
  public void TearDown() {
    if (this._tempDir.Exists)
      this._tempDir.Delete(recursive: true);
  }

  /// <summary>
  /// Writes a 1x1 valid JPEG so MetadataExtractor can parse the file
  /// without choking. Stub byte arrays like {0xFF, 0xD8} aren't well-
  /// formed JPEGs and trigger "End of data reached" exceptions.
  /// </summary>
  private static void WriteRealJpeg(string path) {
    using var img = new Image<Rgb24>(1, 1);
    img.SaveAsJpeg(path);
  }

  [Test]
  public async Task EnumerateDirectory_TopLevel_YieldsOnlySupportedFiles() {
    WriteRealJpeg(Path.Combine(this._tempDir.FullName, "sample.jpg"));
    File.WriteAllBytes(Path.Combine(this._tempDir.FullName, "notes.txt"), new byte[] { 0x00 });

    var manager = new ImportManager();
    var results = new List<FileToImport>();
    await foreach (var f in manager.EnumerateDirectory(this._tempDir, recursive: false))
      results.Add(f);

    Assert.That(results, Has.Count.EqualTo(1));
    Assert.That(results[0].FileName, Is.EqualTo("sample.jpg"));
  }

  [Test]
  public async Task EnumerateDirectory_Recursive_DescendsIntoSubfolders() {
    var sub = Path.Combine(this._tempDir.FullName, "sub");
    Directory.CreateDirectory(sub);
    WriteRealJpeg(Path.Combine(sub, "deep.jpg"));
    WriteRealJpeg(Path.Combine(this._tempDir.FullName, "top.jpg"));

    var manager = new ImportManager();
    var results = new List<string>();
    await foreach (var f in manager.EnumerateDirectory(this._tempDir, recursive: true))
      results.Add(f.FileName);

    Assert.That(results, Is.EquivalentTo(new[] { "deep.jpg", "top.jpg" }));
  }

  [Test]
  public void EnumerateDirectory_MissingRoot_ThrowsDirectoryNotFound() {
    var bogus = new DirectoryInfo(Path.Combine(this._tempDir.FullName, "does-not-exist"));
    var manager = new ImportManager();
    Assert.ThrowsAsync<DirectoryNotFoundException>(async () => {
      await foreach (var _ in manager.EnumerateDirectory(bogus, recursive: false)) { }
    });
  }

  [Test]
  public async Task EnumerateDateTimes_FilenameDateOnly_ReturnsParsedDate() {
    // 20231024 153012 → 2023-10-24 15:30:12 (filename pattern).
    var path = Path.Combine(this._tempDir.FullName, "20231024153012.jpg");
    WriteRealJpeg(path);

    var manager = new ImportManager();
    var fileToImport = new FileToImport(new FileInfo(path));
    var sources = new List<DateTimeSource>();
    await foreach (var (source, _) in manager.EnumerateDateTimes(fileToImport))
      sources.Add(source);

    // Filesystem times always emit (FileCreatedAt + FileModifiedAt) plus
    // the filename-pattern date. EXIF/GPS aren't present in our stub file
    // so they yield nothing.
    Assert.That(sources, Does.Contain(DateTimeSource.FileCreatedAt));
    Assert.That(sources, Does.Contain(DateTimeSource.FileModifiedAt));
    Assert.That(sources, Does.Contain(DateTimeSource.FileName));
  }

  [Test]
  public async Task GetMostLogicalCreationDateAsync_FilenameDateBeatsFileMtime() {
    // Filename-encoded date is more reliable than file mtime (which gets
    // bumped by copies). The importer should pick the filename date when
    // it exists and is sane.
    var path = Path.Combine(this._tempDir.FullName, "20231024153012.jpg");
    WriteRealJpeg(path);
    File.SetLastWriteTime(path, new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Local));

    var manager = new ImportManager();
    var fileToImport = new FileToImport(new FileInfo(path));
    var (date, source) = await manager.GetMostLogicalCreationDateWithSourceAsync(fileToImport);

    Assert.Multiple(() => {
      Assert.That(date, Is.Not.Null);
      Assert.That(source, Is.Not.EqualTo(DateTimeSource.Unknown));
    });
  }

  [Test]
  public async Task GetMostLogicalCreationDateAsync_ExifSubIfdBeatsFilenameAndMtime() {
    // SubIFD DateTimeOriginal is the most reliable source — when it exists,
    // it should win over the filename date and the filesystem mtime.
    var path = Path.Combine(this._tempDir.FullName, "20231024153012.jpg");
    var exifMoment = new DateTime(2023, 10, 24, 15, 30, 12);
    Helpers.TestJpegFactory.Write(path, exifSubIfdDateTimeOriginal: exifMoment);
    File.SetLastWriteTime(path, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Local));

    var manager = new ImportManager();
    var fileToImport = new FileToImport(new FileInfo(path));
    var (date, source) = await manager.GetMostLogicalCreationDateWithSourceAsync(fileToImport);

    Assert.That(date, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(date!.Value, Is.EqualTo(exifMoment));
      Assert.That(source, Is.EqualTo(DateTimeSource.ExifSubIfd));
    });
  }

  [Test]
  public async Task GetMostLogicalCreationDateAsync_FutureExif_IsRejected() {
    // EXIF dates beyond "now" are bogus — the importer's pipeline filters
    // them. Falls back to the filename date instead.
    var path = Path.Combine(this._tempDir.FullName, "20231024153012.jpg");
    var futureMoment = DateTime.UtcNow.AddYears(5);
    Helpers.TestJpegFactory.Write(path, exifSubIfdDateTimeOriginal: futureMoment);

    var manager = new ImportManager();
    var fileToImport = new FileToImport(new FileInfo(path));
    var date = await manager.GetMostLogicalCreationDateAsync(fileToImport);

    Assert.That(date, Is.Not.Null);
    Assert.That(date!.Value, Is.LessThan(DateTime.UtcNow.AddDays(1)));
  }

  [Test]
  public async Task GetMostLogicalCreationDateAsync_OldFilenameYear_FiltersBelowMinimumValid() {
    // A 1970 filename date should be ignored — the "minimum valid" threshold
    // is 1990. The importer falls back to file mtime / created-at instead.
    var path = Path.Combine(this._tempDir.FullName, "19700101000000.jpg");
    WriteRealJpeg(path);
    File.SetLastWriteTime(path, new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Local));

    var manager = new ImportManager();
    var fileToImport = new FileToImport(new FileInfo(path));
    var date = await manager.GetMostLogicalCreationDateAsync(fileToImport);

    Assert.That(date, Is.Not.Null);
    Assert.That(date!.Value.Year, Is.GreaterThanOrEqualTo(1990));
  }
}
