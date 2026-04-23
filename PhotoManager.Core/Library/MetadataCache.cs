using System.Collections.Concurrent;
using PhotoManager.Core.Metadata;

namespace PhotoManager.Core.Library;

/// <summary>
/// In-memory cache of <see cref="FullMetadata"/> keyed by absolute file path.
/// Validity is tied to the file's <c>LastWriteTimeUtc</c> — if the file on
/// disk has changed since the entry was stored we re-read. Consistent with
/// the no-local-DB principle: memory only, rebuilt on demand, no persistence.
///
/// Threadsafe so a background index scan and a foreground search can share
/// the same cache without locking at the call site.
/// </summary>
public sealed class MetadataCache {
  private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
  private readonly IMetadataReader _reader;

  public MetadataCache(IMetadataReader? reader = null) {
    this._reader = reader ?? new MetadataReader();
  }

  public int Count => this._entries.Count;

  /// <summary>Every currently cached (file, metadata) pair. Snapshots the keys to avoid mutation-while-iterating.</summary>
  public IEnumerable<(FileInfo File, FullMetadata Metadata)> Snapshot() {
    foreach (var kv in this._entries.ToArray())
      yield return (new FileInfo(kv.Key), kv.Value.Metadata);
  }

  /// <summary>
  /// Returns the cached metadata for <paramref name="file"/>, re-reading if
  /// the file's mtime has changed since the cached entry was stored. Missing
  /// files return an empty <see cref="FullMetadata"/> rather than throwing.
  /// </summary>
  public async Task<FullMetadata> GetAsync(FileInfo file, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(file);
    if (!file.Exists)
      return new FullMetadata();

    var mtime = file.LastWriteTimeUtc;
    if (this._entries.TryGetValue(file.FullName, out var cached) && cached.LastWriteTimeUtc == mtime)
      return cached.Metadata;

    var fresh = await this._reader.ReadAsync(file, cancellationToken);
    this._entries[file.FullName] = new Entry(mtime, fresh);
    return fresh;
  }

  /// <summary>Forget a specific file's entry — used after a write so the next lookup re-reads.</summary>
  public void Invalidate(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    this._entries.TryRemove(file.FullName, out _);
  }

  /// <summary>Wipe the cache entirely.</summary>
  public void Clear() => this._entries.Clear();

  private sealed record Entry(DateTime LastWriteTimeUtc, FullMetadata Metadata);
}
