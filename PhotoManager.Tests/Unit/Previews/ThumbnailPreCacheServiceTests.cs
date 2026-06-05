using Hawkynt.PhotoManager.Core.Previews;

namespace Hawkynt.PhotoManager.Tests.Unit.Previews;

[TestFixture]
public class ThumbnailPreCacheServiceTests {
  private static readonly byte[] FakeJpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };

  /// <summary>
  /// A generator that returns a fixed byte array for any file, recording
  /// every path it was asked to decode so tests can verify call counts.
  /// </summary>
  private sealed class FakeGenerator {
    private readonly byte[] _result;
    private readonly List<string> _decoded = new();
    private readonly object _gate = new();

    public FakeGenerator(byte[]? result = null) => this._result = result ?? FakeJpeg;

    public IReadOnlyList<string> DecodedPaths {
      get { lock (this._gate) return this._decoded.ToList(); }
    }

    public Task<byte[]?> GenerateAsync(FileInfo file, CancellationToken ct) {
      lock (this._gate) this._decoded.Add(file.FullName);
      return Task.FromResult<byte[]?>(this._result);
    }
  }

  /// <summary>
  /// Helper that creates a real temporary file so PreviewCacheKey.For can
  /// stat it (Refresh / Length / LastWriteTimeUtc).
  /// </summary>
  private static FileInfo CreateTempFile(string? name = null) {
    var path = Path.Combine(Path.GetTempPath(), "pm-precache-test", name ?? Guid.NewGuid().ToString("N") + ".jpg");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, new byte[] { 1 });
    return new FileInfo(path);
  }

  [TearDown]
  public void Cleanup() {
    var dir = Path.Combine(Path.GetTempPath(), "pm-precache-test");
    if (Directory.Exists(dir))
      try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
  }

  [Test]
  public async Task Enqueue_ProcessesFiles_TryGetCachedReturnsBytes() {
    var cache = new InMemoryPreviewCache();
    var gen = new FakeGenerator();
    using var svc = new ThumbnailPreCacheService(cache, gen.GenerateAsync);

    var file = CreateTempFile("a.jpg");
    svc.Enqueue(new[] { file });

    // Wait for the consumer to process the single item.
    await WaitUntilProcessed(svc, 1, timeoutMs: 5000);

    Assert.That(svc.ProcessedCount, Is.EqualTo(1));
    Assert.That(svc.TotalQueued, Is.EqualTo(1));

    var cached = svc.TryGetCached(file);
    Assert.That(cached, Is.Not.Null);
    Assert.That(cached, Is.EqualTo(FakeJpeg));
  }

  [Test]
  public async Task MemoryBound_EvictsOldestEntries() {
    // Cache limited to 20 bytes. Each fake JPEG is 7 bytes, so after 3
    // entries (21 bytes) the oldest should get evicted.
    var cache = new InMemoryPreviewCache(maxEntries: 1000, maxTotalBytes: 20);
    var gen = new FakeGenerator();
    using var svc = new ThumbnailPreCacheService(cache, gen.GenerateAsync);

    var f1 = CreateTempFile("f1.jpg");
    var f2 = CreateTempFile("f2.jpg");
    var f3 = CreateTempFile("f3.jpg");

    svc.Enqueue(new[] { f1, f2, f3 });
    await WaitUntilProcessed(svc, 3, timeoutMs: 5000);

    Assert.That(svc.ProcessedCount, Is.EqualTo(3));
    // At least one early entry should have been evicted by the cache's
    // byte budget. The exact count depends on ordering but total cached
    // bytes must be <= 20.
    Assert.That(cache.CurrentBytes, Is.LessThanOrEqualTo(20));
  }

  [Test]
  public async Task Cancel_StopsProcessing() {
    var tcs = new TaskCompletionSource<byte[]?>();
    // Generator that blocks until we release it.
    Task<byte[]?> BlockingGen(FileInfo _, CancellationToken ct) {
      ct.Register(() => tcs.TrySetCanceled());
      return tcs.Task;
    }

    var cache = new InMemoryPreviewCache();
    using var svc = new ThumbnailPreCacheService(cache, BlockingGen);

    var file = CreateTempFile("block.jpg");
    svc.Enqueue(new[] { file });

    // Give the consumer a moment to pick up the item and block on it.
    await Task.Delay(200);

    svc.Cancel();

    Assert.That(svc.IsRunning, Is.False);
    Assert.That(svc.TotalQueued, Is.EqualTo(0), "Cancel resets counters");
    Assert.That(svc.ProcessedCount, Is.EqualTo(0));
  }

  [Test]
  public void EmptyEnqueue_IsNoOp() {
    var cache = new InMemoryPreviewCache();
    var gen = new FakeGenerator();
    using var svc = new ThumbnailPreCacheService(cache, gen.GenerateAsync);

    svc.Enqueue(Enumerable.Empty<FileInfo>());

    Assert.That(svc.TotalQueued, Is.EqualTo(0));
    Assert.That(svc.ProcessedCount, Is.EqualTo(0));
    Assert.That(gen.DecodedPaths, Is.Empty);
  }

  [Test]
  public async Task DuplicateEnqueue_DoesNotDoubleProcess() {
    var cache = new InMemoryPreviewCache();
    var gen = new FakeGenerator();
    using var svc = new ThumbnailPreCacheService(cache, gen.GenerateAsync);

    var file = CreateTempFile("dup.jpg");
    svc.Enqueue(new[] { file, file, file });

    await WaitUntilProcessed(svc, 1, timeoutMs: 5000);

    // Only one decode should have been triggered — the duplicates were
    // deduped by the _seen set.
    Assert.That(svc.TotalQueued, Is.EqualTo(1));
    Assert.That(gen.DecodedPaths.Count, Is.EqualTo(1));
  }

  [Test]
  public async Task AlreadyCachedFile_SkipsGeneration() {
    var cache = new InMemoryPreviewCache();
    var gen = new FakeGenerator();

    var file = CreateTempFile("pre.jpg");

    // Pre-populate the cache with the same key the service would use.
    var key = PreviewCacheKey.For(file, 1600);
    cache.Set(key, new byte[] { 99 });

    using var svc = new ThumbnailPreCacheService(cache, gen.GenerateAsync);
    svc.Enqueue(new[] { file });

    await WaitUntilProcessed(svc, 1, timeoutMs: 5000);

    // The generator should NOT have been called — the cache already had it.
    Assert.That(gen.DecodedPaths, Is.Empty);
    Assert.That(svc.ProcessedCount, Is.EqualTo(1));
  }

  private static async Task WaitUntilProcessed(ThumbnailPreCacheService svc, int expected, int timeoutMs) {
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (svc.ProcessedCount < expected && sw.ElapsedMilliseconds < timeoutMs)
      await Task.Delay(50);
    // Small extra grace for the consumer loop to park.
    if (svc.ProcessedCount < expected)
      await Task.Delay(200);
  }
}
