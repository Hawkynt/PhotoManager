namespace PhotoManager.Core.Metadata.Containers;

/// <summary>
/// Atomic in-place file rewrite used by every container metadata writer.
/// Writes a <c>.pmtmp</c> neighbour, swaps it in via <c>File.Replace</c>,
/// and restores the ORIGINAL <c>LastWriteTimeUtc</c> on success.
///
/// The mtime restore is the crux of the design: when only metadata
/// segments (XMP/IPTC/EXIF) change, the pixel data is byte-identical, so
/// the file's last-modified timestamp shouldn't advance. Backup tools,
/// DAMs, and sync services use mtime to decide what's changed — updating
/// it on every tag edit would force full re-scans and cloud re-uploads
/// for no real change.
/// </summary>
internal static class AtomicMetadataWrite {
  public static async Task WriteAsync(FileInfo target, byte[] output, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(output);

    var originalMtime = File.Exists(target.FullName)
      ? File.GetLastWriteTimeUtc(target.FullName)
      : (DateTime?)null;

    var tempPath = target.FullName + ".pmtmp";
    try {
      await File.WriteAllBytesAsync(tempPath, output, cancellationToken);
      if (File.Exists(target.FullName))
        File.Replace(tempPath, target.FullName, destinationBackupFileName: null);
      else
        File.Move(tempPath, target.FullName);
    } catch {
      try {
        if (File.Exists(tempPath))
          File.Delete(tempPath);
      } catch {
        // Best-effort cleanup — not fatal if the temp file lingers.
      }
      throw;
    }

    if (originalMtime is { } mtime) {
      try {
        File.SetLastWriteTimeUtc(target.FullName, mtime);
      } catch {
        // Read-only filesystem or similar — not fatal, the write succeeded.
      }
    }

    target.Refresh();
  }
}
