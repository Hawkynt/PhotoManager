using FileFormat.JpegArchive;

namespace PhotoManager.Core.Metadata.Containers;

public sealed class TiffContainerMetadataWriter : IContainerMetadataWriter {
  private static readonly string[] TiffExtensions = { ".tif", ".tiff" };

  public bool SupportsExtension(string extension)
    => TiffExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

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
      output = TiffMetadataEditor.ReplaceXmpPacket(input, xmpBytes);
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
