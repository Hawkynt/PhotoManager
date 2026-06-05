namespace Hawkynt.PhotoManager.Core.Previews;

/// <summary>
/// A cache key that changes whenever the file changes on disk. Combines the
/// full path with the file's size and last-write time so edits, deletes, or
/// replacements invalidate the cached preview automatically.
/// </summary>
public readonly record struct PreviewCacheKey(string FullPath, long SizeBytes, long LastWriteTicks, int MaxEdgePixels) {
  public static PreviewCacheKey For(FileInfo file, int maxEdgePixels) {
    file.Refresh();
    return new PreviewCacheKey(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, maxEdgePixels);
  }
}
