using PhotoManager.Core.Gpx;
using PhotoManager.Core.Library;
using PhotoManager.Tests.Helpers;

namespace PhotoManager.Tests.Unit.Library;

[TestFixture]
public class PhotoDateShifterTests {
  private DirectoryInfo _tempDir = null!;

  [SetUp]
  public void SetUp() {
    this._tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(),
      "PhotoManagerDateShiftTests_" + Guid.NewGuid().ToString("N")));
    this._tempDir.Create();
  }

  [TearDown]
  public void TearDown() {
    try { this._tempDir.Delete(recursive: true); } catch { /* best effort */ }
  }

  private FileInfo NewJpeg(DateTime taken) {
    var path = Path.Combine(this._tempDir.FullName, $"photo-{Guid.NewGuid():N}.jpg");
    TestJpegFactory.Write(path, exifSubIfdDateTimeOriginal: taken);
    return new FileInfo(path);
  }

  [Test]
  public void Plan_NoOffset_ReturnsRowsWithMatchingCurrentAndShifted() {
    var taken = new DateTime(2024, 6, 15, 12, 0, 0);
    var file = this.NewJpeg(taken);

    var plans = PhotoDateShifter.Plan(new[] { file }, TimeSpan.Zero);

    Assert.That(plans, Has.Count.EqualTo(1));
    Assert.That(plans[0].Current, Is.EqualTo(taken));
    Assert.That(plans[0].Shifted, Is.EqualTo(taken));
    Assert.That(plans[0].HasChange, Is.False);
  }

  [Test]
  public void Plan_PositiveOffset_AddsToCurrentDate() {
    var taken = new DateTime(2024, 6, 15, 12, 0, 0);
    var file = this.NewJpeg(taken);

    var plans = PhotoDateShifter.Plan(new[] { file }, TimeSpan.FromHours(2));

    Assert.That(plans[0].Shifted, Is.EqualTo(taken.AddHours(2)));
    Assert.That(plans[0].HasChange, Is.True);
  }

  [Test]
  public void Plan_FileWithoutDate_ReturnsNullCurrent() {
    var path = Path.Combine(this._tempDir.FullName, "nodate.jpg");
    TestJpegFactory.Write(path);  // no exifSubIfdDateTimeOriginal
    var file = new FileInfo(path);

    var plans = PhotoDateShifter.Plan(new[] { file }, TimeSpan.FromHours(1));

    Assert.That(plans[0].Current, Is.Null);
    Assert.That(plans[0].Shifted, Is.Null);
    Assert.That(plans[0].HasChange, Is.False);
  }

  [Test]
  public async Task ApplyAsync_ShiftsExifDate() {
    var taken = new DateTime(2024, 6, 15, 12, 0, 0);
    var file = this.NewJpeg(taken);

    var ok = await PhotoDateShifter.ApplyAsync(file, TimeSpan.FromMinutes(45));
    Assert.That(ok, Is.True);

    file.Refresh();
    var afterDate = PhotoTimestampReader.ReadLocalCameraTime(file);
    Assert.That(afterDate, Is.EqualTo(taken.AddMinutes(45)));
  }

  [Test]
  public async Task ApplyAsync_PreservesFileTimestamps() {
    var taken = new DateTime(2024, 6, 15, 12, 0, 0);
    var file = this.NewJpeg(taken);
    var originalMtime = File.GetLastWriteTimeUtc(file.FullName);

    await PhotoDateShifter.ApplyAsync(file, TimeSpan.FromHours(-3));

    file.Refresh();
    var afterMtime = File.GetLastWriteTimeUtc(file.FullName);
    Assert.That(afterMtime, Is.EqualTo(originalMtime).Within(TimeSpan.FromSeconds(1)),
      "AtomicMetadataWrite should restore mtime so backup tools don't see false changes");
  }

  [Test]
  public async Task ApplyAsync_NegativeOffset_ShiftsBack() {
    var taken = new DateTime(2024, 6, 15, 12, 0, 0);
    var file = this.NewJpeg(taken);

    await PhotoDateShifter.ApplyAsync(file, TimeSpan.FromDays(-1));

    file.Refresh();
    Assert.That(PhotoTimestampReader.ReadLocalCameraTime(file), Is.EqualTo(taken.AddDays(-1)));
  }

  [Test]
  public async Task ApplyAsync_FileWithoutDate_ReturnsFalse() {
    var path = Path.Combine(this._tempDir.FullName, "nodate.jpg");
    TestJpegFactory.Write(path);
    var file = new FileInfo(path);

    var ok = await PhotoDateShifter.ApplyAsync(file, TimeSpan.FromHours(1));
    Assert.That(ok, Is.False);
  }
}
