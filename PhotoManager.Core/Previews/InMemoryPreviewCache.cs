namespace PhotoManager.Core.Previews;

/// <summary>
/// Bounded in-memory LRU cache for decoded preview JPEG byte buffers.
/// No disk persistence — on app restart, previews regenerate on demand.
/// Bounded simultaneously by entry count and total byte size; whichever
/// limit trips first triggers eviction of the least-recently-used entry.
/// </summary>
public sealed class InMemoryPreviewCache {
  private readonly int _maxEntries;
  private readonly long _maxTotalBytes;
  private readonly LinkedList<CacheEntry> _order = new();
  private readonly Dictionary<PreviewCacheKey, LinkedListNode<CacheEntry>> _index = new();
  private readonly object _gate = new();

  private long _currentBytes;

  public InMemoryPreviewCache(int maxEntries = 64, long maxTotalBytes = 256 * 1024 * 1024) {
    if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
    if (maxTotalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxTotalBytes));

    this._maxEntries = maxEntries;
    this._maxTotalBytes = maxTotalBytes;
  }

  public int Count {
    get { lock (this._gate) return this._index.Count; }
  }

  public long CurrentBytes {
    get { lock (this._gate) return this._currentBytes; }
  }

  public bool TryGet(PreviewCacheKey key, out byte[] bytes) {
    lock (this._gate) {
      if (!this._index.TryGetValue(key, out var node)) {
        bytes = Array.Empty<byte>();
        return false;
      }

      // Promote to most-recently-used.
      this._order.Remove(node);
      this._order.AddFirst(node);

      bytes = node.Value.Bytes;
      return true;
    }
  }

  public void Set(PreviewCacheKey key, byte[] bytes) {
    ArgumentNullException.ThrowIfNull(bytes);

    lock (this._gate) {
      if (this._index.TryGetValue(key, out var existing)) {
        this._currentBytes -= existing.Value.Bytes.LongLength;
        this._order.Remove(existing);
      }

      var entry = new CacheEntry(key, bytes);
      var node = this._order.AddFirst(entry);
      this._index[key] = node;
      this._currentBytes += bytes.LongLength;

      this.EvictWhileOverBudget();
    }
  }

  public void Clear() {
    lock (this._gate) {
      this._order.Clear();
      this._index.Clear();
      this._currentBytes = 0;
    }
  }

  private void EvictWhileOverBudget() {
    while ((this._index.Count > this._maxEntries || this._currentBytes > this._maxTotalBytes)
           && this._order.Last is { } victim) {
      this._order.RemoveLast();
      this._index.Remove(victim.Value.Key);
      this._currentBytes -= victim.Value.Bytes.LongLength;
    }
  }

  private readonly record struct CacheEntry(PreviewCacheKey Key, byte[] Bytes);
}
