using MetadataExtractor.Formats.Exif;

namespace Hawkynt.PhotoManager.Core.Gpx;

/// <summary>
/// Reads the capture timestamp from a photo's EXIF data. Preference order:
/// EXIF SubIFD DateTimeOriginal (when the shutter closed — the right signal
/// for geotagging), then IFD0 DateTime (last-edited), then file mtime.
///
/// Returns the timestamp as-if-local — EXIF DateTimeOriginal is written in
/// the camera's local time with no timezone, so the caller has to apply an
/// offset to convert it to UTC for GPX matching.
/// </summary>
public static class PhotoTimestampReader {
  public static DateTime? ReadLocalCameraTime(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    if (!file.Exists)
      return null;

    IReadOnlyList<MetadataExtractor.Directory> directories;
    try {
      directories = ImageMetadataReader.ReadMetadata(file.FullName);
    } catch {
      return null;
    }

    foreach (var subIfd in directories.OfType<ExifSubIfdDirectory>()) {
      if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var value))
        return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }

    foreach (var ifd0 in directories.OfType<ExifIfd0Directory>()) {
      if (ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var value))
        return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }

    return null;
  }
}
