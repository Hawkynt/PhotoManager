namespace PhotoManager.Core.Metadata;

public interface IMetadataWriter {
  /// <summary>
  /// Applies the <paramref name="edit"/> patch to <paramref name="imageFile"/>'s
  /// sidecar. Reads the existing sidecar (if any), overlays the patch, and
  /// writes the result back. Fields not mentioned in the patch are preserved.
  /// Returns the <see cref="FileInfo"/> of the sidecar that was written.
  /// </summary>
  Task<FileInfo> ApplyAsync(FileInfo imageFile, MetadataEdit edit, CancellationToken cancellationToken = default);
}
