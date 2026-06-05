using Hawkynt.PhotoManager.Core.Previews;

namespace Hawkynt.PhotoManager.Tests.Unit.Previews;

[TestFixture]
public class InMemoryPreviewCacheTests {
  private static PreviewCacheKey Key(string name, long size = 100, long ticks = 1000, int edge = 1600)
    => new(name, size, ticks, edge);

  [Test]
  public void Set_And_TryGet_RoundTrip() {
    var cache = new InMemoryPreviewCache();
    var key = Key("a.jpg");
    cache.Set(key, new byte[] { 1, 2, 3 });

    Assert.That(cache.TryGet(key, out var bytes), Is.True);
    Assert.That(bytes, Is.EqualTo(new byte[] { 1, 2, 3 }));
  }

  [Test]
  public void TryGet_MissingKey_ReturnsFalse() {
    var cache = new InMemoryPreviewCache();
    Assert.That(cache.TryGet(Key("missing.jpg"), out _), Is.False);
  }

  [Test]
  public void DifferentMtime_InvalidatesEntry() {
    var cache = new InMemoryPreviewCache();
    var oldKey = Key("a.jpg", ticks: 1000);
    var newKey = Key("a.jpg", ticks: 2000);

    cache.Set(oldKey, new byte[] { 1 });

    Assert.That(cache.TryGet(newKey, out _), Is.False,
      "mtime change should miss, forcing regeneration");
  }

  [Test]
  public void Eviction_ExceedEntryLimit_DropsLeastRecentlyUsed() {
    var cache = new InMemoryPreviewCache(maxEntries: 3, maxTotalBytes: long.MaxValue);
    var a = Key("a"); var b = Key("b"); var c = Key("c"); var d = Key("d");

    cache.Set(a, new byte[] { 1 });
    cache.Set(b, new byte[] { 2 });
    cache.Set(c, new byte[] { 3 });

    // Touch 'a' so 'b' is now least-recently-used.
    Assert.That(cache.TryGet(a, out _), Is.True);

    cache.Set(d, new byte[] { 4 });

    Assert.Multiple(() => {
      Assert.That(cache.TryGet(a, out _), Is.True,  "a was promoted, should survive");
      Assert.That(cache.TryGet(b, out _), Is.False, "b was least recent, should be evicted");
      Assert.That(cache.TryGet(c, out _), Is.True);
      Assert.That(cache.TryGet(d, out _), Is.True);
    });
  }

  [Test]
  public void Eviction_ExceedByteBudget_DropsUntilUnderLimit() {
    var cache = new InMemoryPreviewCache(maxEntries: 100, maxTotalBytes: 300);

    cache.Set(Key("a"), new byte[100]);
    cache.Set(Key("b"), new byte[100]);
    cache.Set(Key("c"), new byte[100]);
    Assert.That(cache.CurrentBytes, Is.EqualTo(300));

    cache.Set(Key("d"), new byte[100]);

    Assert.That(cache.CurrentBytes, Is.LessThanOrEqualTo(300));
    Assert.That(cache.Count, Is.LessThanOrEqualTo(3));
  }

  [Test]
  public void Set_OverwriteSameKey_ReplacesBytesAndAdjustsBudget() {
    var cache = new InMemoryPreviewCache();
    var k = Key("a");
    cache.Set(k, new byte[50]);
    cache.Set(k, new byte[10]);

    Assert.Multiple(() => {
      Assert.That(cache.Count, Is.EqualTo(1));
      Assert.That(cache.CurrentBytes, Is.EqualTo(10));
      Assert.That(cache.TryGet(k, out var bytes), Is.True);
      Assert.That(bytes.Length, Is.EqualTo(10));
    });
  }

  [Test]
  public void Clear_RemovesAllEntries() {
    var cache = new InMemoryPreviewCache();
    cache.Set(Key("a"), new byte[] { 1 });
    cache.Set(Key("b"), new byte[] { 2 });

    cache.Clear();

    Assert.Multiple(() => {
      Assert.That(cache.Count, Is.Zero);
      Assert.That(cache.CurrentBytes, Is.Zero);
    });
  }
}
