using PhotoManager.Core.Metadata;

namespace PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class XmpSidecarWriterTests {
  private DirectoryInfo _workingDir = null!;
  private XmpSidecarWriter _writer = null!;

  [SetUp]
  public void Setup() {
    this._workingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "PhotoManager-tests-" + Guid.NewGuid().ToString("N")));
    this._workingDir.Create();
    this._writer = new XmpSidecarWriter();
  }

  [TearDown]
  public void Teardown() {
    if (this._workingDir.Exists)
      this._workingDir.Delete(recursive: true);
  }

  private FileInfo CreateFakeImage(string name = "photo.jpg") {
    var file = new FileInfo(Path.Combine(this._workingDir.FullName, name));
    File.WriteAllBytes(file.FullName, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }); // minimal JPEG-ish bytes
    file.Refresh();
    return file;
  }

  [Test]
  public async Task ApplyAsync_NoExistingSidecar_CreatesFile() {
    var image = this.CreateFakeImage();
    var edit = new MetadataEdit {
      Gps = new GpsCoordinate(37.7749, -122.4194)
    };

    var sidecar = await this._writer.ApplyAsync(image, edit);

    Assert.That(sidecar.Exists, Is.True);
    Assert.That(sidecar.FullName, Is.EqualTo(image.FullName + ".xmp"));
  }

  [Test]
  public async Task ApplyAsync_PartialPatch_PreservesUntouchedFields() {
    var image = this.CreateFakeImage();

    // Seed a full sidecar
    await this._writer.ApplyAsync(image, new MetadataEdit {
      Gps = new GpsCoordinate(10, 20, 100),
      Rating = 3,
      ColorLabel = "Red",
      Title = "Original Title"
    });

    // Patch only the rating
    await this._writer.ApplyAsync(image, new MetadataEdit { Rating = 5 });

    var reader = new MetadataReader();
    var result = await reader.ReadAsync(image);

    Assert.Multiple(() => {
      Assert.That(result.Rating, Is.EqualTo(5), "patched field should reflect the new value");
      Assert.That(result.ColorLabel, Is.EqualTo("Red"), "untouched label should survive");
      Assert.That(result.Title, Is.EqualTo("Original Title"), "untouched title should survive");
      Assert.That(result.Gps!.Value.Latitude, Is.EqualTo(10).Within(1e-6));
    });
  }

  [Test]
  public async Task ApplyAsync_ClearingField_RemovesItFromSidecar() {
    var image = this.CreateFakeImage();

    await this._writer.ApplyAsync(image, new MetadataEdit {
      Rating = 4,
      ColorLabel = "Blue"
    });

    // Patch: clear the label by setting Optional to null
    await this._writer.ApplyAsync(image, new MetadataEdit {
      ColorLabel = Optional<string?>.Set(null)
    });

    var reader = new MetadataReader();
    var result = await reader.ReadAsync(image);

    Assert.Multiple(() => {
      Assert.That(result.ColorLabel, Is.Null);
      Assert.That(result.Rating, Is.EqualTo(4), "other fields should not be disturbed");
    });
  }

  [Test]
  public async Task ApplyAsync_SetKeywordsThenEmpty_RemovesSubjectElement() {
    var image = this.CreateFakeImage();

    await this._writer.ApplyAsync(image, new MetadataEdit {
      Keywords = Optional<IReadOnlyList<string>>.Set(new[] { "tag1", "tag2" })
    });

    await this._writer.ApplyAsync(image, new MetadataEdit {
      Keywords = Optional<IReadOnlyList<string>>.Set(Array.Empty<string>())
    });

    var sidecar = SidecarPath.For(image);
    var xml = await File.ReadAllTextAsync(sidecar.FullName);

    Assert.That(xml, Does.Not.Contain("dc:subject"));
  }
}
