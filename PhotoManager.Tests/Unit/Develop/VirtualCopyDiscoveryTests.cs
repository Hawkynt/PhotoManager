using Hawkynt.PhotoManager.Core.Develop;
using Hawkynt.PhotoManager.Tests.Helpers;

namespace Hawkynt.PhotoManager.Tests.Unit.Develop;

[TestFixture]
public class VirtualCopyDiscoveryTests {
  private DirectoryInfo _tempDir = null!;

  [SetUp]
  public void SetUp() {
    this._tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(),
      "PhotoManagerVirtualCopyTests_" + Guid.NewGuid().ToString("N")));
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

  private void TouchSidecar(string filename) {
    File.WriteAllText(Path.Combine(this._tempDir.FullName, filename),
      "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" /></x:xmpmeta>");
  }

  [Test]
  public void Enumerate_NoSidecars_ReturnsEmpty() {
    var file = this.NewJpeg();
    Assert.That(VirtualCopyDiscovery.Enumerate(file), Is.Empty);
    Assert.That(VirtualCopyDiscovery.EnumerateIndices(file), Is.Empty);
  }

  [Test]
  public void Enumerate_WithGapInIndices_ReturnsBothPresent() {
    var file = this.NewJpeg();
    this.TouchSidecar("IMG.copy1.xmp");
    this.TouchSidecar("IMG.copy3.xmp");

    Assert.That(VirtualCopyDiscovery.EnumerateIndices(file), Is.EqualTo(new[] { 1, 3 }));
  }

  [Test]
  public void Enumerate_IsSortedAscending() {
    var file = this.NewJpeg();
    this.TouchSidecar("IMG.copy7.xmp");
    this.TouchSidecar("IMG.copy2.xmp");
    this.TouchSidecar("IMG.copy11.xmp");

    Assert.That(VirtualCopyDiscovery.EnumerateIndices(file), Is.EqualTo(new[] { 2, 7, 11 }));
  }

  [Test]
  public void Enumerate_IgnoresOtherImagesAndCopy0Pattern() {
    var file = this.NewJpeg();
    this.TouchSidecar("IMG.copy1.xmp");
    this.TouchSidecar("IMG.copy0.xmp");      // copy 0 is implicit (embedded), not a sidecar
    this.TouchSidecar("IMG.xmp");            // default sidecar — also not a copy
    this.TouchSidecar("OTHER.copy1.xmp");    // belongs to a different basename

    Assert.That(VirtualCopyDiscovery.EnumerateIndices(file), Is.EqualTo(new[] { 1 }));
  }

  [Test]
  public void NextAvailableIndex_NoSidecars_Returns1() {
    var file = this.NewJpeg();
    Assert.That(VirtualCopyDiscovery.NextAvailableIndex(file), Is.EqualTo(1));
  }

  [Test]
  public void NextAvailableIndex_WithGap_FillsIt() {
    var file = this.NewJpeg();
    this.TouchSidecar("IMG.copy1.xmp");
    this.TouchSidecar("IMG.copy3.xmp");

    Assert.That(VirtualCopyDiscovery.NextAvailableIndex(file), Is.EqualTo(2));
  }

  [Test]
  public void NextAvailableIndex_DenseRange_ReturnsNextInteger() {
    var file = this.NewJpeg();
    this.TouchSidecar("IMG.copy1.xmp");
    this.TouchSidecar("IMG.copy2.xmp");
    this.TouchSidecar("IMG.copy3.xmp");

    Assert.That(VirtualCopyDiscovery.NextAvailableIndex(file), Is.EqualTo(4));
  }

  [Test]
  public void SidecarFor_ProducesExpectedPath() {
    var file = this.NewJpeg("IMG.jpg");
    var sidecar = VirtualCopyDiscovery.SidecarFor(file, 2);
    Assert.That(sidecar.Name, Is.EqualTo("IMG.copy2.xmp"));
    Assert.That(sidecar.DirectoryName, Is.EqualTo(file.DirectoryName));
  }

  [Test]
  public void SidecarFor_Index0_Throws() {
    var file = this.NewJpeg();
    Assert.Throws<ArgumentOutOfRangeException>(() => VirtualCopyDiscovery.SidecarFor(file, 0));
  }

  [Test]
  public async Task SaveAsync_ToCopySidecar_WritesNextToSourceWithCorrectName() {
    var file = this.NewJpeg("IMG.jpg");
    var settings = new DevelopSettings(ExposureStops: 0.75, ContrastPercent: 30);
    Assert.That(await DevelopMetadataStore.SaveAsync(file, settings, copyIndex: 1, snapshotLabel: null), Is.True);

    var expected = new FileInfo(Path.Combine(this._tempDir.FullName, "IMG.copy1.xmp"));
    Assert.That(expected.Exists, Is.True);

    var loaded = await DevelopMetadataStore.LoadAsync(file, copyIndex: 1);
    Assert.That(loaded, Is.Not.Null);
    Assert.That(loaded!.ExposureStops, Is.EqualTo(0.75).Within(0.01));
    Assert.That(loaded.ContrastPercent, Is.EqualTo(30));

    // Copy 0 is unaffected
    Assert.That(await DevelopMetadataStore.LoadAsync(file, copyIndex: 0), Is.Null);
  }

  [Test]
  public async Task EnumerateVirtualCopies_AfterCreatingTwoCopies_ReturnsBothIndices() {
    var file = this.NewJpeg("IMG.jpg");
    Assert.That(await DevelopMetadataStore.SaveAsync(file, new DevelopSettings(ExposureStops: 0.5), copyIndex: 1, snapshotLabel: null), Is.True);
    Assert.That(await DevelopMetadataStore.SaveAsync(file, new DevelopSettings(ExposureStops: 1.0), copyIndex: 2, snapshotLabel: null), Is.True);

    Assert.That(DevelopMetadataStore.EnumerateVirtualCopies(file), Is.EqualTo(new[] { 1, 2 }));
  }
}
