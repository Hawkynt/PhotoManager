using Hawkynt.PhotoManager.Core.Detection;
using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.Core.Regions;

namespace Hawkynt.PhotoManager.Tests.Unit.Regions;

[TestFixture]
public class RegionServiceTests {
  private DirectoryInfo _workingDir = null!;
  private RegionService _service = null!;

  [SetUp]
  public void Setup() {
    this._workingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-regions-" + Guid.NewGuid().ToString("N")));
    this._workingDir.Create();
    this._service = new RegionService(new MetadataReader(), new XmpSidecarWriter());
  }

  [TearDown]
  public void Teardown() {
    if (this._workingDir.Exists)
      this._workingDir.Delete(recursive: true);
  }

  private FileInfo CreateFakeImage() {
    var file = new FileInfo(Path.Combine(this._workingDir.FullName, "photo.jpg"));
    File.WriteAllBytes(file.FullName, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
    file.Refresh();
    return file;
  }

  [Test]
  public async Task Append_Accept_PromotesLabelToKeywords() {
    var file = this.CreateFakeImage();

    await this._service.AppendAsync(file, new[] {
      new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Animal, "cat", RegionStatus.Proposed, TaggedRegion.YoloSource)
    });
    await this._service.AcceptAsync(file, 0);

    var reader = new MetadataReader();
    var md = await reader.ReadAsync(file);

    Assert.Multiple(() => {
      Assert.That(md.Regions, Has.Count.EqualTo(1));
      Assert.That(md.Regions[0].Status, Is.EqualTo(RegionStatus.Accepted));
      Assert.That(md.Keywords, Does.Contain("cat"));
    });
  }

  [Test]
  public async Task Discard_RemovesRegion() {
    var file = this.CreateFakeImage();

    await this._service.AppendAsync(file, new[] {
      new TaggedRegion(new NormalizedBoundingBox(0.1f, 0.1f, 0.1f, 0.1f), RegionCategory.Item, "spoon"),
      new TaggedRegion(new NormalizedBoundingBox(0.5f, 0.5f, 0.1f, 0.1f), RegionCategory.Item, "fork")
    });

    await this._service.DiscardAsync(file, index: 0);

    var regions = await this._service.ListAsync(file);
    Assert.Multiple(() => {
      Assert.That(regions, Has.Count.EqualTo(1));
      Assert.That(regions[0].Label, Is.EqualTo("fork"));
    });
  }

  [Test]
  public async Task Relabel_ChangesLabel() {
    var file = this.CreateFakeImage();

    await this._service.AppendAsync(file, new[] {
      new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Person, "Unknown")
    });

    await this._service.RelabelAsync(file, index: 0, newLabel: "Alice");

    var regions = await this._service.ListAsync(file);
    Assert.That(regions[0].Label, Is.EqualTo("Alice"));
  }

  [Test]
  public async Task Append_DedupesByBoxAndCategory() {
    var file = this.CreateFakeImage();
    var box = new NormalizedBoundingBox(0.2f, 0.2f, 0.1f, 0.1f);

    await this._service.AppendAsync(file, new[] {
      new TaggedRegion(box, RegionCategory.Animal, "cat", RegionStatus.Proposed)
    });
    // Second append with same box + category — should be deduped.
    await this._service.AppendAsync(file, new[] {
      new TaggedRegion(box, RegionCategory.Animal, "cat", RegionStatus.Proposed)
    });

    var regions = await this._service.ListAsync(file);
    Assert.That(regions, Has.Count.EqualTo(1));
  }

  [Test]
  public void Accept_OutOfRange_Throws() {
    var file = this.CreateFakeImage();
    Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
      await this._service.AcceptAsync(file, 42)
    );
  }

  [Test]
  public async Task Relabel_SameNameAsAnotherPersonRegion_StripsTheOther() {
    var file = this.CreateFakeImage();

    // Two Person regions, one already tagged "Alice".
    await this._service.AppendAsync(file, new[] {
      new TaggedRegion(new NormalizedBoundingBox(0.1f, 0.1f, 0.1f, 0.1f),
        RegionCategory.Person, "Alice", RegionStatus.Accepted, TaggedRegion.FaceDetectorSource),
      new TaggedRegion(new NormalizedBoundingBox(0.5f, 0.5f, 0.1f, 0.1f),
        RegionCategory.Person, "Bob", RegionStatus.Accepted, TaggedRegion.FaceDetectorSource)
    });

    // User renames Bob → Alice. Invariant: only ONE Alice per photo, so
    // the first one loses its name.
    await this._service.RelabelAsync(file, 1, "Alice");

    var md = await new MetadataReader().ReadAsync(file);
    var aliceRegions = md.Regions.Where(r => r.Label == "Alice").ToList();
    Assert.Multiple(() => {
      Assert.That(aliceRegions, Has.Count.EqualTo(1),
        "a person can't appear twice on the same photo — latest tag wins");
      // The relabeled region (index 1) keeps the name, the old Alice is stripped.
      Assert.That(md.Regions[1].Label, Is.EqualTo("Alice"));
      Assert.That(md.Regions[0].Label, Is.Null.Or.Empty);
    });
  }

  [Test]
  public async Task Relabel_PromotesNewLabelToKeywords() {
    var file = this.CreateFakeImage();

    await this._service.AppendAsync(file, new[] {
      new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Person,
        "UnknownFace", RegionStatus.Proposed, TaggedRegion.FaceDetectorSource)
    });
    await this._service.RelabelAsync(file, 0, "Tim");

    var md = await new MetadataReader().ReadAsync(file);
    Assert.Multiple(() => {
      Assert.That(md.Regions[0].Label, Is.EqualTo("Tim"));
      Assert.That(md.Keywords, Does.Contain("Tim"), "relabel should promote the new name into keywords so search finds it");
    });
  }
}
