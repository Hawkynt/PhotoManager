using PhotoManager.Core.Detection;
using PhotoManager.Core.Faces;
using PhotoManager.Core.Interfaces;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Regions;
using PhotoManager.Core.Services;

namespace PhotoManager.Tests.Unit.Faces;

[TestFixture]
public class LibraryFaceScannerTests {
  private DirectoryInfo _root = null!;

  [SetUp]
  public void Setup() {
    this._root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-scan-" + Guid.NewGuid().ToString("N")));
    this._root.Create();
  }

  [TearDown]
  public void Teardown() {
    if (this._root.Exists)
      this._root.Delete(recursive: true);
  }

  private FileInfo CreateStubImage(string relativePath) {
    var full = new FileInfo(Path.Combine(this._root.FullName, relativePath));
    full.Directory!.Create();
    File.WriteAllBytes(full.FullName, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
    full.Refresh();
    return full;
  }

  private sealed class StubReader : IMetadataReader {
    private readonly Dictionary<string, FullMetadata> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public void Register(FileInfo file, FullMetadata metadata)
      => this._byPath[file.FullName] = metadata;

    public Task<FullMetadata> ReadAsync(FileInfo imageFile, CancellationToken cancellationToken = default)
      => Task.FromResult(this._byPath.GetValueOrDefault(imageFile.FullName, new FullMetadata()));
  }

  private static TaggedRegion PersonRegion(string? name = null, float[]? embedding = null)
    => new(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Person, Label: name, Embedding: embedding);

  [Test]
  public async Task ScanAsync_NonExistentRoot_ReturnsEmpty() {
    var reader = new StubReader();
    var scanner = new LibraryFaceScanner(reader, new SupportedFormatsService());
    var result = await scanner.ScanAsync(new DirectoryInfo(Path.Combine(this._root.FullName, "nope")));
    Assert.That(result, Is.Empty);
  }

  [Test]
  public async Task ScanAsync_FindsPersonRegions_AcrossFiles() {
    var a = this.CreateStubImage("a.jpg");
    var b = this.CreateStubImage("sub/b.jpg");
    var reader = new StubReader();
    reader.Register(a, new FullMetadata { Regions = [PersonRegion("Alice", [1, 0, 0])] });
    reader.Register(b, new FullMetadata { Regions = [PersonRegion(embedding: [0, 1, 0])] });

    var scanner = new LibraryFaceScanner(reader, new SupportedFormatsService());
    var result = await scanner.ScanAsync(this._root);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(2));
      Assert.That(result.Select(r => r.Name), Does.Contain("Alice"));
    });
  }

  [Test]
  public async Task ScanAsync_SkipsNonPersonRegions() {
    var file = this.CreateStubImage("a.jpg");
    var reader = new StubReader();
    reader.Register(file, new FullMetadata {
      Regions = [
        PersonRegion("Alice"),
        new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Animal, Label: "cat"),
        new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Item, Label: "mug")
      ]
    });

    var scanner = new LibraryFaceScanner(reader, new SupportedFormatsService());
    var result = await scanner.ScanAsync(this._root);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(1));
      Assert.That(result[0].Name, Is.EqualTo("Alice"));
    });
  }

  [Test]
  public async Task ScanAsync_OnlyEmbedded_FiltersOutPlainFaces() {
    var file = this.CreateStubImage("a.jpg");
    var reader = new StubReader();
    reader.Register(file, new FullMetadata {
      Regions = [
        PersonRegion("Alice", [1, 0, 0]),
        PersonRegion("Bob")  // no embedding
      ]
    });

    var scanner = new LibraryFaceScanner(reader, new SupportedFormatsService());
    var result = await scanner.ScanAsync(this._root, onlyEmbedded: true);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(1));
      Assert.That(result[0].Name, Is.EqualTo("Alice"));
    });
  }

  [Test]
  public async Task ScanAsync_IgnoresUnsupportedExtensions() {
    var jpg = this.CreateStubImage("photo.jpg");
    var txt = this.CreateStubImage("notes.txt");
    var reader = new StubReader();
    reader.Register(jpg, new FullMetadata { Regions = [PersonRegion("Alice", [1, 0, 0])] });
    reader.Register(txt, new FullMetadata { Regions = [PersonRegion("Bob", [0, 1, 0])] });

    var scanner = new LibraryFaceScanner(reader, new SupportedFormatsService());
    var result = await scanner.ScanAsync(this._root);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(1));
      Assert.That(result[0].Name, Is.EqualTo("Alice"));
    });
  }

  [Test]
  public async Task ScanAsync_NonRecursive_IgnoresSubdirs() {
    var top = this.CreateStubImage("top.jpg");
    var nested = this.CreateStubImage("sub/nested.jpg");
    var reader = new StubReader();
    reader.Register(top, new FullMetadata { Regions = [PersonRegion("Top")] });
    reader.Register(nested, new FullMetadata { Regions = [PersonRegion("Nested")] });

    var scanner = new LibraryFaceScanner(reader, new SupportedFormatsService());
    var result = await scanner.ScanAsync(this._root, recursive: false);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(1));
      Assert.That(result[0].Name, Is.EqualTo("Top"));
    });
  }

  [Test]
  public async Task ScanAsync_ProgressReported_ForEachImage() {
    this.CreateStubImage("a.jpg");
    this.CreateStubImage("b.jpg");
    var reader = new StubReader();
    var reports = new List<string>();
    var progress = new Progress<FileInfo>(f => reports.Add(f.Name));

    var scanner = new LibraryFaceScanner(reader, new SupportedFormatsService());
    await scanner.ScanAsync(this._root, progress: progress);

    // Progress reporting is async — give the sync context time to drain.
    // Two files means at least two reports will have been enqueued; we assert
    // the count eventually, but take what's synchronously visible immediately.
    await Task.Yield();
    await Task.Delay(25);

    Assert.That(reports, Has.Count.EqualTo(2));
  }
}
