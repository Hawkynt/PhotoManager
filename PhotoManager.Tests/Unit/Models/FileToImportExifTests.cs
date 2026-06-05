using Hawkynt.PhotoManager.Core.Models;
using Hawkynt.PhotoManager.Tests.Helpers;

namespace Hawkynt.PhotoManager.Tests.Unit.Models;

[TestFixture]
public class FileToImportExifTests {
  private DirectoryInfo _tempDir = null!;

  [SetUp]
  public void SetUp() {
    this._tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-fti-tests-" + Guid.NewGuid().ToString("N")));
    this._tempDir.Create();
  }

  [TearDown]
  public void TearDown() {
    if (this._tempDir.Exists)
      this._tempDir.Delete(recursive: true);
  }

  [Test]
  public async Task GetExifIfd0DateAsync_WithDateTime_ReadsBackTheValue() {
    var captured = new DateTime(2023, 4, 15, 10, 30, 0);
    var path = Path.Combine(this._tempDir.FullName, "ifd0.jpg");
    TestJpegFactory.Write(path, exifIfd0DateTime: captured);

    var fti = new FileToImport(new FileInfo(path));
    var dates = new List<DateTime>();
    await foreach (var d in fti.GetExifIfd0DateAsync())
      dates.Add(d);

    Assert.That(dates, Has.Count.EqualTo(1));
    Assert.That(dates[0], Is.EqualTo(captured));
  }

  [Test]
  public async Task GetExifSubIfdDateAsync_WithDateTimeOriginal_ReadsBackTheValue() {
    var captured = new DateTime(2024, 7, 20, 14, 5, 30);
    var path = Path.Combine(this._tempDir.FullName, "subifd.jpg");
    TestJpegFactory.Write(path, exifSubIfdDateTimeOriginal: captured);

    var fti = new FileToImport(new FileInfo(path));
    var dates = new List<DateTime>();
    await foreach (var d in fti.GetExifSubIfdDateAsync())
      dates.Add(d);

    Assert.That(dates, Has.Count.EqualTo(1));
    Assert.That(dates[0], Is.EqualTo(captured));
  }

  [Test]
  public async Task GetExifSubIfdDateAsync_NoExif_ReturnsEmpty() {
    var path = Path.Combine(this._tempDir.FullName, "noexif.jpg");
    TestJpegFactory.Write(path);  // no EXIF date set

    var fti = new FileToImport(new FileInfo(path));
    var dates = new List<DateTime>();
    await foreach (var d in fti.GetExifSubIfdDateAsync())
      dates.Add(d);

    Assert.That(dates, Is.Empty);
  }

  [Test]
  public async Task GetExifSubIfdDateAsync_CallTwice_UsesCachedResult() {
    // Same instance reads cache on second call. We can't directly assert
    // the cache hit but we can confirm behaviour is consistent.
    var captured = new DateTime(2022, 1, 1, 12, 0, 0);
    var path = Path.Combine(this._tempDir.FullName, "cached.jpg");
    TestJpegFactory.Write(path, exifSubIfdDateTimeOriginal: captured);

    var fti = new FileToImport(new FileInfo(path));
    var first = new List<DateTime>();
    await foreach (var d in fti.GetExifSubIfdDateAsync()) first.Add(d);
    var second = new List<DateTime>();
    await foreach (var d in fti.GetExifSubIfdDateAsync()) second.Add(d);

    Assert.That(second, Is.EquivalentTo(first));
  }

  [Test]
  public async Task GetGpsDateAsync_NoGpsTimestamp_ReturnsEmpty() {
    var path = Path.Combine(this._tempDir.FullName, "no-gps.jpg");
    TestJpegFactory.Write(path);

    var fti = new FileToImport(new FileInfo(path));
    var dates = new List<DateTime>();
    await foreach (var d in fti.GetGpsDateAsync())
      dates.Add(d);

    Assert.That(dates, Is.Empty);
  }
}
