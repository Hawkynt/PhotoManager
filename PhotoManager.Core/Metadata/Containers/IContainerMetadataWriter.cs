namespace PhotoManager.Core.Metadata.Containers;

/// <summary>
/// Strategy for writing metadata directly inside an image file (as opposed
/// to into an <c>.xmp</c> sidecar). Implementations return
/// <see cref="ContainerWriteResult.NotSupported"/> when asked about a
/// format they can't handle — callers use this to fall through to the
/// sidecar writer.
/// </summary>
public interface IContainerMetadataWriter {
  bool SupportsExtension(string extension);

  /// <summary>
  /// Writes <paramref name="xmpBytes"/> into <paramref name="imageFile"/>
  /// without re-encoding the image. Only the XMP metadata segment is
  /// touched; pixel data is byte-for-byte preserved.
  /// </summary>
  Task<ContainerWriteResult> WriteXmpAsync(FileInfo imageFile, byte[] xmpBytes, CancellationToken cancellationToken = default);
}

public enum ContainerWriteResult {
  /// <summary>The format isn't handled by this writer.</summary>
  NotSupported,
  /// <summary>The XMP payload was successfully embedded.</summary>
  Written,
  /// <summary>The format is supported but the particular file is malformed.</summary>
  Failed
}
