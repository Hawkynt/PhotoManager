using System.Collections.Concurrent;

namespace Hawkynt.PhotoManager.Core.Library;

/// <summary>
/// In-memory cache of 64-bit perceptual hashes keyed by absolute file path.
/// Validity is tied to the file's <c>LastWriteTimeUtc</c> — if the file on
/// disk has changed since the entry was stored we recompute. Mirrors the
/// no-local-DB principle of <see cref="MetadataCache"/>: memory only,
/// rebuilt on demand, no persistence.
/// </summary>
public sealed class PerceptualHashCache {
  private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

  public int Count => this._entries.Count;

  /// <summary>Every currently cached (file, hash) pair.</summary>
  public IEnumerable<(FileInfo File, ulong Hash)> Snapshot() {
    foreach (var kv in this._entries.ToArray())
      yield return (new FileInfo(kv.Key), kv.Value.Hash);
  }

  /// <summary>
  /// Returns the cached pHash for <paramref name="file"/>, recomputing if
  /// the file's mtime has changed since the cached entry was stored.
  /// Throws on missing files — the duplicate finder filters those out
  /// before calling so it's a real error if it ever bubbles up.
  /// </summary>
  public async Task<ulong> GetAsync(FileInfo file, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(file);
    var mtime = file.LastWriteTimeUtc;
    if (this._entries.TryGetValue(file.FullName, out var cached) && cached.LastWriteTimeUtc == mtime)
      return cached.Hash;

    var fresh = await PerceptualHash.ComputeAsync(file, cancellationToken);
    this._entries[file.FullName] = new Entry(mtime, fresh);
    return fresh;
  }

  public void Invalidate(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    this._entries.TryRemove(file.FullName, out _);
  }

  public void Clear() => this._entries.Clear();

  private sealed record Entry(DateTime LastWriteTimeUtc, ulong Hash);
}
