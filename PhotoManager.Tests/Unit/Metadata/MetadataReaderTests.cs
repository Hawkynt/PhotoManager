using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class MetadataReaderTests {
  private DirectoryInfo _workingDir = null!;
  private MetadataReader _reader = null!;
  private XmpSidecarWriter _writer = null!;

  [SetUp]
  public void Setup() {
    this._workingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "PhotoManager-reader-" + Guid.NewGuid().ToString("N")));
    this._workingDir.Create();
    this._reader = new MetadataReader();
    this._writer = new XmpSidecarWriter();
  }

  [TearDown]
  public void Teardown() {
    if (this._workingDir.Exists)
      this._workingDir.Delete(recursive: true);
  }

  private FileInfo CreateFakeImage(string name = "photo.jpg") {
    var file = new FileInfo(Path.Combine(this._workingDir.FullName, name));
    File.WriteAllBytes(file.FullName, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
    file.Refresh();
    return file;
  }

  [Test]
  public async Task ReadAsync_NoSidecar_ReturnsEmptyMetadata() {
    var image = this.CreateFakeImage();

    var result = await this._reader.ReadAsync(image);

    Assert.Multiple(() => {
      Assert.That(result.Gps, Is.Null);
      Assert.That(result.Rating, Is.Null);
      Assert.That(result.Keywords, Is.Empty);
    });
  }

  [Test]
  public async Task ReadAsync_SidecarPresent_ReturnsSidecarValues() {
    var image = this.CreateFakeImage();
    await this._writer.ApplyAsync(image, new MetadataEdit {
      Gps = new GpsCoordinate(45.0, 9.0, 250),
      Rating = 5,
      Keywords = Optional<IReadOnlyList<string>>.Set(new[] { "alps", "summit" })
    });

    var result = await this._reader.ReadAsync(image);

    Assert.Multiple(() => {
      Assert.That(result.Gps!.Value.Latitude, Is.EqualTo(45.0).Within(1e-6));
      Assert.That(result.Gps.Value.Longitude, Is.EqualTo(9.0).Within(1e-6));
      Assert.That(result.Rating, Is.EqualTo(5));
      Assert.That(result.Keywords, Is.EquivalentTo(new[] { "alps", "summit" }));
    });
  }

  [Test]
  public async Task ReadAsync_MalformedSidecar_FallsBackToEmpty() {
    var image = this.CreateFakeImage();
    var sidecar = SidecarPath.For(image);
    await File.WriteAllTextAsync(sidecar.FullName, "<not-xml><garbage>");

    var result = await this._reader.ReadAsync(image);

    // Shouldn't throw; shouldn't see garbage data either.
    Assert.That(result.Gps, Is.Null);
    Assert.That(result.Rating, Is.Null);
  }

  [Test]
  public async Task ReadAsync_NonexistentImage_ReturnsEmptyMetadata() {
    var result = await this._reader.ReadAsync(new FileInfo(Path.Combine(this._workingDir.FullName, "nope.jpg")));

    Assert.That(result.Gps, Is.Null);
    Assert.That(result.Keywords, Is.Empty);
  }
}
