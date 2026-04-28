namespace PhotoManager.Core.Metadata.Containers;

/// <summary>
/// Atomic in-place file rewrite used by every container metadata writer.
/// Writes a <c>.pmtmp</c> neighbour, swaps it in via <c>File.Replace</c>,
/// and restores ALL three filesystem timestamps (creation, last-write,
/// last-access) on success.
///
/// The timestamp restore is the crux of the design: when only metadata
/// segments (XMP/IPTC/EXIF) change, the pixel data is byte-identical, so
/// none of the file timestamps should advance. Backup tools, DAMs, and
/// sync services use these timestamps to decide what's changed — updating
/// them on every tag edit would force full re-scans and cloud re-uploads
/// for no real change.
/// </summary>
internal static class AtomicMetadataWrite {
  public static async Task WriteAsync(FileInfo target, byte[] output, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(output);

    DateTime? originalCreated = null;
    DateTime? originalModified = null;
    DateTime? originalAccessed = null;
    if (File.Exists(target.FullName)) {
      originalCreated = File.GetCreationTimeUtc(target.FullName);
      originalModified = File.GetLastWriteTimeUtc(target.FullName);
      originalAccessed = File.GetLastAccessTimeUtc(target.FullName);
    }

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

    // Restore timestamps. Each is wrapped individually because read-only
    // filesystems / strict perms can reject one without breaking the
    // others — and the metadata write itself already succeeded.
    if (originalCreated  is { } c) { try { File.SetCreationTimeUtc(target.FullName,  c); } catch { } }
    if (originalModified is { } m) { try { File.SetLastWriteTimeUtc(target.FullName, m); } catch { } }
    if (originalAccessed is { } a) { try { File.SetLastAccessTimeUtc(target.FullName, a); } catch { } }

    target.Refresh();
  }
}
