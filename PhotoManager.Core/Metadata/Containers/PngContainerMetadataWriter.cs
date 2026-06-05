using FileFormat.JpegArchive;

namespace Hawkynt.PhotoManager.Core.Metadata.Containers;

/// <summary>
/// Embeds our XMP packet into a PNG's iTXt chunk so metadata travels with
/// the file without a sidecar. PNG has no EXIF IFD — XMP is the only in-file
/// metadata route — so there's no EXIF-bridge equivalent here.
/// </summary>
public sealed class PngContainerMetadataWriter : IContainerMetadataWriter {
  public bool SupportsExtension(string extension)
    => extension.Equals(".png", StringComparison.OrdinalIgnoreCase);

  public async Task<ContainerWriteResult> WriteXmpAsync(FileInfo imageFile, byte[] xmpBytes, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(imageFile);
    ArgumentNullException.ThrowIfNull(xmpBytes);

    if (!this.SupportsExtension(imageFile.Extension))
      return ContainerWriteResult.NotSupported;
    if (!imageFile.Exists)
      return ContainerWriteResult.Failed;

    byte[] input;
    try {
      input = await File.ReadAllBytesAsync(imageFile.FullName, cancellationToken);
    } catch {
      return ContainerWriteResult.Failed;
    }

    byte[] output;
    try {
      output = PngChunkSurgery.ReplaceXmpChunk(input, xmpBytes);
    } catch (InvalidDataException) {
      return ContainerWriteResult.Failed;
    }

    try {
      await AtomicMetadataWrite.WriteAsync(imageFile, output, cancellationToken);
      return ContainerWriteResult.Written;
    } catch {
      return ContainerWriteResult.Failed;
    }
  }
}
