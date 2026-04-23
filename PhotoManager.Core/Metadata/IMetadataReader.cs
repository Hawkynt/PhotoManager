namespace PhotoManager.Core.Metadata;

public interface IMetadataReader {
  /// <summary>
  /// Returns the full metadata snapshot for <paramref name="imageFile"/>:
  /// EXIF values first, then XMP sidecar values overlaid on top. Returns
  /// an empty <see cref="FullMetadata"/> for files without readable
  /// metadata (and never throws for missing sidecars).
  /// </summary>
  Task<FullMetadata> ReadAsync(FileInfo imageFile, CancellationToken cancellationToken = default);
}
