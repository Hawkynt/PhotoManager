using System.Text;
using FileFormat.JpegArchive;

namespace PhotoManager.Core.Metadata.Containers;

public sealed class JpegContainerMetadataWriter : IContainerMetadataWriter {
  private static readonly string[] JpegExtensions = { ".jpg", ".jpeg", ".jpe", ".jfif" };

  public bool SupportsExtension(string extension)
    => JpegExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

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
      output = JpegSegmentSurgery.ReplaceXmpSegment(input, xmpBytes);
    } catch (InvalidDataException) {
      return ContainerWriteResult.Failed;
    } catch (InvalidOperationException) {
      // XMP exceeds the single-APP1 size limit — fall back to sidecar.
      return ContainerWriteResult.NotSupported;
    }

    try {
      await AtomicMetadataWrite.WriteAsync(imageFile, output, cancellationToken);
      return ContainerWriteResult.Written;
    } catch {
      return ContainerWriteResult.Failed;
    }
  }
}

/// <summary>
/// Combines EXIF + XMP + IPTC updates into a single JPEG rewrite. All three
/// metadata segments are replaced/inserted in one pass so the file is only
/// written once. Used by <see cref="PhotoManager.Core.Metadata.CompositeMetadataWriter"/>
/// for JPEGs where we want native EXIF + legacy IPTC (which many tools read
/// instead of XMP) kept in sync with our canonical XMP.
/// </summary>
public static class JpegContainerExifBridge {
  public static async Task<bool> WriteAsync(
    FileInfo imageFile,
    byte[] xmpBytes,
    ExifPatch exifPatch,
    IptcFields? iptc = null,
    CancellationToken cancellationToken = default
  ) {
    if (!imageFile.Exists)
      return false;

    byte[] input;
    try {
      input = await File.ReadAllBytesAsync(imageFile.FullName, cancellationToken);
    } catch {
      return false;
    }

    byte[] output;
    try {
      var afterExif = JpegMetadataEditor.ApplyExifPatch(input, exifPatch);
      var afterXmp = JpegSegmentSurgery.ReplaceXmpSegment(afterExif, xmpBytes);
      output = iptc is { IsEmpty: false }
        ? JpegSegmentSurgery.ReplaceIptcSegment(afterXmp, IptcIimEncoder.Encode(iptc))
        : afterXmp;
    } catch (InvalidDataException) {
      return false;
    } catch (InvalidOperationException) {
      return false;
    } catch (ArgumentException) {
      return false;
    }

    try {
      await AtomicMetadataWrite.WriteAsync(imageFile, output, cancellationToken);
      return true;
    } catch {
      return false;
    }
  }
}
