using PhotoManager.Core.Develop;
using PhotoManager.Tests.Helpers;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
public class VirtualCopyManagerTests {
  private DirectoryInfo _tempDir = null!;

  [SetUp]
  public void SetUp() {
    this._tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(),
      "PhotoManagerVCMgrTests_" + Guid.NewGuid().ToString("N")));
    this._tempDir.Create();
  }

  [TearDown]
  public void TearDown() {
    try { this._tempDir.Delete(recursive: true); } catch { /* best effort */ }
  }

  private FileInfo NewJpeg(string name = "IMG.jpg") {
    var path = Path.Combine(this._tempDir.FullName, name);
    TestJpegFactory.Write(path, exifSubIfdDateTimeOriginal: new DateTime(2024, 1, 1, 12, 0, 0));
    return new FileInfo(path);
  }

  [Test]
  public void Discover_NoCopies_ReturnsEmptyList() {
    var file = this.NewJpeg();
    var copies = VirtualCopyManager.Discover(file);
    Assert.That(copies, Is.Empty);
  }

  [Test]
  public void CreateCopy_FirstCopy_CreatesCopy1Xmp() {
    var file = this.NewJpeg();
    var copy = VirtualCopyManager.CreateCopy(file);

    Assert.That(copy.CopyNumber, Is.EqualTo(1));
    Assert.That(copy.SidecarFile.Name, Is.EqualTo("IMG.copy1.xmp"));
    Assert.That(copy.SidecarFile.Exists, Is.True);
    Assert.That(copy.SourceFile.FullName, Is.EqualTo(file.FullName));
  }

  [Test]
  public void CreateCopy_SecondCopy_CreatesCopy2Xmp() {
    var file = this.NewJpeg();
    VirtualCopyManager.CreateCopy(file);
    var copy2 = VirtualCopyManager.CreateCopy(file);

    Assert.That(copy2.CopyNumber, Is.EqualTo(2));
    Assert.That(copy2.SidecarFile.Name, Is.EqualTo("IMG.copy2.xmp"));
    Assert.That(copy2.SidecarFile.Exists, Is.True);
  }

  [Test]
  public void Discover_FindsBothCopiesInOrder() {
    var file = this.NewJpeg();
    VirtualCopyManager.CreateCopy(file);
    VirtualCopyManager.CreateCopy(file);

    var copies = VirtualCopyManager.Discover(file);
    Assert.That(copies.Count, Is.EqualTo(2));
    Assert.That(copies[0].CopyNumber, Is.EqualTo(1));
    Assert.That(copies[1].CopyNumber, Is.EqualTo(2));
    Assert.That(copies[0].SidecarFile.Exists, Is.True);
    Assert.That(copies[1].SidecarFile.Exists, Is.True);
  }

  [Test]
  public void DeleteCopy_RemovesSidecarFile() {
    var file = this.NewJpeg();
    var copy = VirtualCopyManager.CreateCopy(file);
    Assert.That(copy.SidecarFile.Exists, Is.True);

    VirtualCopyManager.DeleteCopy(copy);

    // Refresh FileInfo to see the deletion on disk.
    copy.SidecarFile.Refresh();
    Assert.That(copy.SidecarFile.Exists, Is.False);
  }

  [Test]
  public async Task RoundTrip_CreateCopyWithSettings_LoadSettingsFromCopy_Match() {
    var file = this.NewJpeg();
    var original = new DevelopSettings(
      ExposureStops: 1.5,
      ContrastPercent: -20,
      SaturationPercent: 40
    );

    var copy = VirtualCopyManager.CreateCopy(file, original);
    Assert.That(copy.SidecarFile.Exists, Is.True);

    // Load the settings back from the copy's sidecar via DevelopMetadataStore.
    var loaded = await DevelopMetadataStore.LoadAsync(file, copy.CopyNumber);
    Assert.That(loaded, Is.Not.Null);
    Assert.That(loaded!.ExposureStops, Is.EqualTo(1.5).Within(0.01));
    Assert.That(loaded.ContrastPercent, Is.EqualTo(-20).Within(0.1));
    Assert.That(loaded.SaturationPercent, Is.EqualTo(40).Within(0.1));

    // Original (copy 0) should be unaffected.
    var origLoaded = await DevelopMetadataStore.LoadAsync(file, copyIndex: 0);
    Assert.That(origLoaded, Is.Null);
  }
}
