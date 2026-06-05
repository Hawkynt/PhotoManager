using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.Tests.Helpers;

namespace Hawkynt.PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class MetadataReaderExifTests {
  private DirectoryInfo _tempDir = null!;

  [SetUp]
  public void SetUp() {
    this._tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-mr-exif-" + Guid.NewGuid().ToString("N")));
    this._tempDir.Create();
  }

  [TearDown]
  public void TearDown() {
    if (this._tempDir.Exists)
      this._tempDir.Delete(recursive: true);
  }

  [Test]
  public async Task ReadAsync_FileWithGps_ReturnsCoordinate() {
    var path = Path.Combine(this._tempDir.FullName, "paris.jpg");
    TestJpegFactory.Write(path, gps: new TestJpegFactory.GpsValue(48.8566, 2.3522));

    var reader = new MetadataReader();
    var md = await reader.ReadAsync(new FileInfo(path));

    Assert.That(md.Gps, Is.Not.Null);
    Assert.Multiple(() => {
      // GPS rational encoding loses tiny precision; tolerate ~0.001°.
      Assert.That(md.Gps!.Value.Latitude,  Is.EqualTo(48.8566).Within(0.001));
      Assert.That(md.Gps!.Value.Longitude, Is.EqualTo(2.3522).Within(0.001));
    });
  }

  [Test]
  public async Task ReadAsync_FileWithSouthAndWestHemispheres_NegatesAxes() {
    var path = Path.Combine(this._tempDir.FullName, "rio.jpg");
    TestJpegFactory.Write(path, gps: new TestJpegFactory.GpsValue(-22.9068, -43.1729));

    var reader = new MetadataReader();
    var md = await reader.ReadAsync(new FileInfo(path));

    Assert.That(md.Gps, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(md.Gps!.Value.Latitude,  Is.LessThan(0));
      Assert.That(md.Gps!.Value.Longitude, Is.LessThan(0));
    });
  }

  [Test]
  public async Task ReadAsync_FileWithoutGps_ReturnsNullGps() {
    var path = Path.Combine(this._tempDir.FullName, "no-gps.jpg");
    TestJpegFactory.Write(path);

    var reader = new MetadataReader();
    var md = await reader.ReadAsync(new FileInfo(path));

    Assert.That(md.Gps, Is.Null);
  }

  [Test]
  public async Task ReadAsync_MissingFile_ReturnsEmptyMetadata() {
    var path = Path.Combine(this._tempDir.FullName, "ghost.jpg");
    var reader = new MetadataReader();
    var md = await reader.ReadAsync(new FileInfo(path));

    // No exception, just empty record.
    Assert.That(md.Gps, Is.Null);
    Assert.That(md.Title, Is.Null);
  }
}
