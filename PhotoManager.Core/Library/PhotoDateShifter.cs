using FileFormat.JpegArchive;
using PhotoManager.Core.Gpx;
using PhotoManager.Core.Metadata.Containers;

namespace PhotoManager.Core.Library;

/// <summary>
/// Shifts EXIF capture timestamps by a fixed offset — the workflow you
/// reach for when the camera clock was set wrong (DST, timezone, drift).
/// Reads <c>DateTimeOriginal</c> via <see cref="PhotoTimestampReader"/>,
/// applies the offset, and writes <c>DateTimeOriginal</c> +
/// <c>DateTimeDigitized</c> + IFD0 <c>DateTime</c> back through
/// <see cref="JpegMetadataEditor.ApplyExifPatch"/>.
///
/// Pixel data is not touched and the filesystem mtime/ctime/atime are
/// preserved by <see cref="AtomicMetadataWrite"/>.
/// </summary>
public static class PhotoDateShifter {
  private static readonly string[] JpegExtensions = { ".jpg", ".jpeg", ".jpe", ".jfif" };

  public static bool SupportsContainer(FileInfo? file)
    => file is not null && JpegExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase);

  public sealed record ShiftPlan(FileInfo File, DateTime? Current, DateTime? Shifted) {
    public bool HasChange => this.Current is { } c && this.Shifted is { } s && c != s;
  }

  /// <summary>Build a per-file plan without touching disk — useful for
  /// driving a preview list before the user clicks Apply.</summary>
  public static IReadOnlyList<ShiftPlan> Plan(IEnumerable<FileInfo> files, TimeSpan offset) {
    ArgumentNullException.ThrowIfNull(files);
    var plans = new List<ShiftPlan>();
    foreach (var f in files) {
      var current = PhotoTimestampReader.ReadLocalCameraTime(f);
      var shifted = current.HasValue ? current.Value + offset : (DateTime?)null;
      plans.Add(new ShiftPlan(f, current, shifted));
    }
    return plans;
  }

  /// <summary>
  /// Apply <paramref name="offset"/> to <paramref name="file"/>'s EXIF
  /// dates. Returns true on success, false when the container isn't a
  /// JPEG / has no readable date / the rewrite fails.
  /// </summary>
  public static async Task<bool> ApplyAsync(FileInfo file, TimeSpan offset, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(file);
    if (!SupportsContainer(file) || !file.Exists)
      return false;

    var current = PhotoTimestampReader.ReadLocalCameraTime(file);
    if (current is null)
      return false;
    var shifted = current.Value + offset;

    byte[] hostBytes;
    try {
      hostBytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
    } catch {
      return false;
    }

    var patch = new ExifPatch {
      DateTimeOriginal  = shifted,
      DateTimeDigitized = shifted,
      DateTimeModified  = shifted
    };

    byte[] output;
    try {
      output = JpegMetadataEditor.ApplyExifPatch(hostBytes, patch);
    } catch {
      return false;
    }

    try {
      await AtomicMetadataWrite.WriteAsync(file, output, cancellationToken);
      return true;
    } catch {
      return false;
    }
  }
}
