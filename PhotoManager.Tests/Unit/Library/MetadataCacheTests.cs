using PhotoManager.Core.Library;
using PhotoManager.Core.Metadata;

namespace PhotoManager.Tests.Unit.Library;

[TestFixture]
public class MetadataCacheTests {
  private DirectoryInfo _workingDir = null!;

  [SetUp]
  public void Setup() {
    this._workingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-cache-" + Guid.NewGuid().ToString("N")));
    this._workingDir.Create();
  }

  [TearDown]
  public void Teardown() {
    if (this._workingDir.Exists)
      this._workingDir.Delete(recursive: true);
  }

  private FileInfo CreateFile(string name = "photo.jpg") {
    var f = new FileInfo(Path.Combine(this._workingDir.FullName, name));
    File.WriteAllBytes(f.FullName, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
    f.Refresh();
    return f;
  }

  private sealed class CountingReader : IMetadataReader {
    public int Reads { get; private set; }
    public Task<FullMetadata> ReadAsync(FileInfo imageFile, CancellationToken cancellationToken = default) {
      this.Reads++;
      return Task.FromResult(new FullMetadata { Title = $"title-{this.Reads}" });
    }
  }

  [Test]
  public async Task GetAsync_SameFileTwice_ReadsOnlyOnce() {
    var reader = new CountingReader();
    var cache = new MetadataCache(reader);
    var file = this.CreateFile();

    var first = await cache.GetAsync(file);
    var second = await cache.GetAsync(file);

    Assert.Multiple(() => {
      Assert.That(reader.Reads, Is.EqualTo(1));
      Assert.That(first.Title, Is.EqualTo("title-1"));
      Assert.That(second.Title, Is.EqualTo("title-1"), "cache returned the same snapshot");
    });
  }

  [Test]
  public async Task GetAsync_FileTouched_RereadsMetadata() {
    var reader = new CountingReader();
    var cache = new MetadataCache(reader);
    var file = this.CreateFile();

    await cache.GetAsync(file);
    // Advance mtime by a measurable amount — Windows mtime is ~10ms resolution.
    File.SetLastWriteTimeUtc(file.FullName, DateTime.UtcNow.AddSeconds(2));
    file.Refresh();

    await cache.GetAsync(file);

    Assert.That(reader.Reads, Is.EqualTo(2));
  }

  [Test]
  public async Task Invalidate_ForcesReread() {
    var reader = new CountingReader();
    var cache = new MetadataCache(reader);
    var file = this.CreateFile();

    await cache.GetAsync(file);
    cache.Invalidate(file);
    await cache.GetAsync(file);

    Assert.That(reader.Reads, Is.EqualTo(2));
  }

  [Test]
  public async Task GetAsync_MissingFile_ReturnsEmpty_DoesNotRead() {
    var reader = new CountingReader();
    var cache = new MetadataCache(reader);
    var file = new FileInfo(Path.Combine(this._workingDir.FullName, "nonexistent.jpg"));

    var result = await cache.GetAsync(file);

    Assert.Multiple(() => {
      Assert.That(result.Title, Is.Null);
      Assert.That(reader.Reads, Is.EqualTo(0));
    });
  }

  [Test]
  public async Task Snapshot_ReturnsCachedEntries() {
    var reader = new CountingReader();
    var cache = new MetadataCache(reader);
    var a = this.CreateFile("a.jpg");
    var b = this.CreateFile("b.jpg");

    await cache.GetAsync(a);
    await cache.GetAsync(b);

    var items = cache.Snapshot().ToList();
    Assert.That(items, Has.Count.EqualTo(2));
  }

  [Test]
  public async Task Clear_EmptiesCache() {
    var reader = new CountingReader();
    var cache = new MetadataCache(reader);
    await cache.GetAsync(this.CreateFile());
    Assert.That(cache.Count, Is.EqualTo(1));
    cache.Clear();
    Assert.That(cache.Count, Is.EqualTo(0));
  }
}
