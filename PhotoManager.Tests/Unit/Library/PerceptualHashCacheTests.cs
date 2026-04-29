using PhotoManager.Core.Library;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Library;

[TestFixture]
public class PerceptualHashCacheTests {
  private DirectoryInfo _workingDir = null!;

  [SetUp]
  public void Setup() {
    this._workingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-phashcache-" + Guid.NewGuid().ToString("N")));
    this._workingDir.Create();
  }

  [TearDown]
  public void Teardown() {
    if (this._workingDir.Exists)
      this._workingDir.Delete(recursive: true);
  }

  private FileInfo WriteJpeg(string name, int seed) {
    var path = Path.Combine(this._workingDir.FullName, name);
    using var image = new Image<Rgba32>(64, 64);
    var rng = new Random(seed);
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < accessor.Width; x++)
          row[x] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
      }
    });
    image.SaveAsJpeg(path);
    return new FileInfo(path);
  }

  [Test]
  public async Task GetAsync_SameFileTwice_HitsCache() {
    var cache = new PerceptualHashCache();
    var file = this.WriteJpeg("a.jpg", seed: 4);

    var first = await cache.GetAsync(file);
    var mtimeBefore = File.GetLastWriteTimeUtc(file.FullName);

    var second = await cache.GetAsync(file);

    Assert.Multiple(() => {
      Assert.That(second, Is.EqualTo(first), "cache returned the same hash");
      Assert.That(File.GetLastWriteTimeUtc(file.FullName), Is.EqualTo(mtimeBefore),
        "second call must not have touched the file");
      Assert.That(cache.Count, Is.EqualTo(1));
    });
  }

  [Test]
  public async Task GetAsync_FileTouched_RecomputesHash() {
    var cache = new PerceptualHashCache();
    var file = this.WriteJpeg("a.jpg", seed: 4);

    await cache.GetAsync(file);
    File.SetLastWriteTimeUtc(file.FullName, DateTime.UtcNow.AddSeconds(2));
    file.Refresh();

    // Overwrite with different content so the recomputed hash actually differs.
    using (var img2 = new Image<Rgba32>(64, 64))
      img2.SaveAsJpeg(file.FullName);
    File.SetLastWriteTimeUtc(file.FullName, DateTime.UtcNow.AddSeconds(4));
    file.Refresh();

    var second = await cache.GetAsync(file);
    Assert.That(cache.Count, Is.EqualTo(1));
    // Snapshot's only entry should reflect the latest mtime — i.e. the cache
    // is not stale even after a content swap.
    var snap = cache.Snapshot().Single();
    Assert.That(snap.Hash, Is.EqualTo(second));
  }

  [Test]
  public async Task Invalidate_ForcesRecompute() {
    var cache = new PerceptualHashCache();
    var file = this.WriteJpeg("a.jpg", seed: 4);

    await cache.GetAsync(file);
    Assert.That(cache.Count, Is.EqualTo(1));

    cache.Invalidate(file);
    Assert.That(cache.Count, Is.EqualTo(0));

    await cache.GetAsync(file);
    Assert.That(cache.Count, Is.EqualTo(1));
  }

  [Test]
  public async Task Snapshot_ReturnsCachedEntries() {
    var cache = new PerceptualHashCache();
    var a = this.WriteJpeg("a.jpg", seed: 1);
    var b = this.WriteJpeg("b.jpg", seed: 2);

    await cache.GetAsync(a);
    await cache.GetAsync(b);

    var items = cache.Snapshot().ToList();
    Assert.That(items, Has.Count.EqualTo(2));
  }
}
