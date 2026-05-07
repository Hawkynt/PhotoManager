using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Develop;

/// <summary>
/// Per-stage image cache that the UI hands to
/// <see cref="RestorationPipeline.Apply"/> across slider drags so a
/// settings change on stage N reuses cached outputs from stages 0..N-1
/// instead of recomputing the whole pipeline.
///
/// <para>The cache keys each stage by a settings-hash string the
/// pipeline computes from <see cref="RestorationSettings"/>. A stage's
/// cache entry is only honored if (a) the source image's identity
/// hasn't changed since the entry was stored AND (b) every prior
/// stage's cache entry is still fresh — otherwise the upstream change
/// would invalidate this stage's input. The pipeline tracks the
/// "longest fresh prefix" during Apply and invalidates from the first
/// miss onwards.</para>
///
/// <para>Held by <c>RestoreWindow</c> for the lifetime of the open
/// window. <see cref="Dispose"/> drops every cached image (each is a
/// freshly-allocated <see cref="Image{Rgba32}"/> so the cache OWNS
/// them — pipeline callers must clone before storing).</para>
/// </summary>
public sealed class RestorationPipelineCache : IDisposable {
  /// <summary>The cached output for one stage, plus the settings-key
  /// it was produced for. Key mismatch = stage settings changed →
  /// invalidate this and everything downstream.</summary>
  private sealed record CacheEntry(string Key, Image<Rgba32> Image);

  private readonly Dictionary<string, CacheEntry> _entries = new();
  private object? _sourceIdentity;

  /// <summary>
  /// Bind the cache to <paramref name="source"/>. If the source is
  /// different from the previously-bound one (different reference =
  /// different file or downscaled differently), every cached entry is
  /// invalidated. Use ReferenceEquals semantics — pipeline callers
  /// pass the same _previewSource clone-source on every render, so
  /// reference identity is stable as long as the file isn't reloaded.
  /// </summary>
  public void BindToSource(object source) {
    if (this._sourceIdentity != null && !ReferenceEquals(this._sourceIdentity, source))
      this.Clear();
    this._sourceIdentity = source;
  }

  /// <summary>Return the cached output for <paramref name="stageName"/>
  /// IF its key matches <paramref name="key"/>. null on miss. The
  /// returned image is owned by the cache; callers must clone it
  /// before mutating.</summary>
  public Image<Rgba32>? GetIfFresh(string stageName, string key) {
    if (!this._entries.TryGetValue(stageName, out var entry))
      return null;
    return entry.Key == key ? entry.Image : null;
  }

  /// <summary>Store a stage's output. Internally clones
  /// <paramref name="image"/> so the cache holds an independent copy
  /// that survives the caller's subsequent mutations.</summary>
  public void Set(string stageName, string key, Image<Rgba32> image) {
    if (this._entries.TryGetValue(stageName, out var existing))
      existing.Image.Dispose();
    this._entries[stageName] = new CacheEntry(key, image.Clone());
  }

  /// <summary>Drop every cached entry. Cumulative-key cache
  /// invalidation is implicit: when a stage's key changes, every
  /// downstream stage's cumulative key also changes (since they share
  /// the now-changed prefix), causing them to miss naturally on the
  /// next Apply call. Stale entries linger in the dict until
  /// overwritten or this method is called.</summary>
  public void Clear() {
    foreach (var entry in this._entries.Values)
      entry.Image.Dispose();
    this._entries.Clear();
  }

  /// <summary>Diagnostic — number of stages with a live cache entry.
  /// Useful for tests / debugging cache hit-rate.</summary>
  public int CachedStageCount => this._entries.Count;

  public void Dispose() => this.Clear();
}
