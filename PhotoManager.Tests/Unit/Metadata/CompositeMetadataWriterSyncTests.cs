using System.Text;
using FileFormat.JpegArchive;
using Hawkynt.PhotoManager.Core.Detection;
using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.Core.Regions;

namespace Hawkynt.PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class CompositeMetadataWriterSyncTests {
  private DirectoryInfo _workingDir = null!;

  [SetUp]
  public void Setup() {
    this._workingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-composite-" + Guid.NewGuid().ToString("N")));
    this._workingDir.Create();
  }

  [TearDown]
  public void Teardown() {
    if (this._workingDir.Exists)
      this._workingDir.Delete(recursive: true);
  }

  /// <summary>
  /// Minimal JPEG (SOI + DQT stub + EOI) — enough for JpegSegmentSurgery to
  /// parse + round-trip. Not a decodable image; it's purely for byte-level
  /// XMP segment tests.
  /// </summary>
  private FileInfo CreateMinimalJpeg() {
    var file = new FileInfo(Path.Combine(this._workingDir.FullName, "photo.jpg"));
    using var ms = new MemoryStream();
    ms.Write(new byte[] { 0xFF, 0xD8 });
    // APP0 JFIF so the segment parser has a starting segment
    var jfifPayload = new byte[] { 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00 };
    var jfifLen = 2 + jfifPayload.Length;
    ms.Write(new byte[] { 0xFF, 0xE0, (byte)(jfifLen >> 8), (byte)(jfifLen & 0xFF) });
    ms.Write(jfifPayload);
    ms.Write(new byte[] { 0xFF, 0xDB, 0x00, 0x03, 0x00 });  // DQT stub
    ms.Write(new byte[] { 0xFF, 0xD9 });                     // EOI
    File.WriteAllBytes(file.FullName, ms.ToArray());
    file.Refresh();
    return file;
  }

  [Test]
  public async Task DismissingLastRegion_AlsoClearsInFileXmp_WhenSidecarExists() {
    var file = this.CreateMinimalJpeg();
    var writer = new CompositeMetadataWriter();
    var reader = new MetadataReader();

    // 1) Propose a region via the normal write path. Since no sidecar exists,
    //    Case 2 fires and the region lands in the JPEG's embedded XMP.
    var regionService = new RegionService(reader, writer);
    await regionService.AppendAsync(file, new[] {
      new TaggedRegion(new NormalizedBoundingBox(0.1f, 0.1f, 0.2f, 0.2f),
        RegionCategory.Animal, "cat", RegionStatus.Proposed, TaggedRegion.YoloSource)
    });

    // Sanity: the in-file XMP now contains the region.
    var bytes = await File.ReadAllBytesAsync(file.FullName);
    var inFileXmp = JpegSegmentSurgery.TryReadXmpSegment(bytes);
    Assert.That(inFileXmp, Is.Not.Null);
    Assert.That(Encoding.UTF8.GetString(inFileXmp!), Does.Contain("mwg-rs:Regions"));

    // 2) Manually drop a sidecar next to it so future writes take Case 1 path.
    var sidecarPath = SidecarPath.For(file);
    await File.WriteAllTextAsync(sidecarPath.FullName,
      XmpSidecarFormatter.Serialize(await reader.ReadAsync(file)));

    // 3) Dismiss the region. Previously this would update the sidecar but
    //    leave the stale <mwg-rs:Regions> element in the JPEG's APP1 XMP,
    //    so MergeSidecarOverExif's fall-through would resurface it on read.
    await regionService.DiscardAsync(file, index: 0);

    // 4) Re-read the file bytes: the in-file XMP must no longer carry a
    //    Regions element (the sidecar's explicit clear must have synced).
    bytes = await File.ReadAllBytesAsync(file.FullName);
    inFileXmp = JpegSegmentSurgery.TryReadXmpSegment(bytes);
    Assert.Multiple(() => {
      Assert.That(inFileXmp, Is.Not.Null);
      Assert.That(Encoding.UTF8.GetString(inFileXmp!), Does.Not.Contain("mwg-rs:Regions"),
        "sidecar write should have synced the in-file XMP too");
    });

    // 5) And the full read (sidecar + in-file merge) returns zero regions.
    var final = await reader.ReadAsync(file);
    Assert.That(final.Regions, Is.Empty);
  }

  [Test]
  public async Task InFileWrite_PreservesLastWriteTime() {
    var file = this.CreateMinimalJpeg();

    // Pin mtime to an older value so "now" comparisons are reliable.
    var originalMtime = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(file.FullName, originalMtime);

    var writer = new CompositeMetadataWriter();
    await writer.ApplyAsync(file, new MetadataEdit { Title = "Hello" });

    file.Refresh();
    Assert.That(file.LastWriteTimeUtc, Is.EqualTo(originalMtime),
      "metadata-only edits must not advance the file's mtime — pixel data is untouched");
  }

  [Test]
  public async Task SupportedFormat_DoesNotCreateSidecar() {
    var file = this.CreateMinimalJpeg();
    var sidecarPath = SidecarPath.For(file);

    var writer = new CompositeMetadataWriter();
    await writer.ApplyAsync(file, new MetadataEdit { Title = "only-in-file" });

    Assert.That(File.Exists(sidecarPath.FullName), Is.False,
      "JPEG supports in-file metadata — no sidecar should be created");

    // Read-back confirms the title survived via the embedded XMP.
    var reader = new MetadataReader();
    var md = await reader.ReadAsync(file);
    Assert.That(md.Title, Is.EqualTo("only-in-file"));
  }

  [Test]
  public async Task ExistingSidecar_GetsKeptInSyncButIsNotCreatedFromScratch() {
    var file = this.CreateMinimalJpeg();
    var sidecarPath = SidecarPath.For(file);
    var writer = new CompositeMetadataWriter();

    // First edit — no sidecar yet, should stay absent.
    await writer.ApplyAsync(file, new MetadataEdit { Title = "v1" });
    Assert.That(File.Exists(sidecarPath.FullName), Is.False);

    // User or external tool creates the sidecar.
    await File.WriteAllTextAsync(sidecarPath.FullName,
      XmpSidecarFormatter.Serialize(new FullMetadata { Title = "v1" }));

    // Subsequent edit — in-file stays primary, existing sidecar also gets updated.
    await writer.ApplyAsync(file, new MetadataEdit { Title = "v2" });

    var sidecarText = await File.ReadAllTextAsync(sidecarPath.FullName);
    Assert.That(sidecarText, Does.Contain("v2"), "existing sidecar should be kept in sync");

    var reader = new MetadataReader();
    var md = await reader.ReadAsync(file);
    Assert.That(md.Title, Is.EqualTo("v2"));
  }
}
