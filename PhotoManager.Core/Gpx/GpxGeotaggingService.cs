using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Gpx;

/// <summary>
/// Outcome per file for <see cref="GpxGeotaggingService.ApplyToFilesAsync"/>.
/// </summary>
public sealed record GpxGeotagResult(
  FileInfo File,
  GpsCoordinate? Matched,
  DateTime? CameraLocalTime,
  string? Reason = null
) {
  public bool Success => this.Matched is not null && this.Reason is null;
}

/// <summary>
/// Applies GPX trackpoints to a batch of photos: reads each file's local
/// camera time, adjusts by the user-supplied clock offset to get UTC, asks
/// the <see cref="GpxTimelineMatcher"/> for the best-fit coordinate, and
/// writes it through the injected <see cref="IMetadataWriter"/>.
///
/// The clock offset is what GeoSetter calls the "time shift": camera clocks
/// drift, and on trips you often discover your camera says 12:00 while the
/// GPS logger says 12:07. <c>cameraOffsetFromUtc</c> bundles that drift in
/// with the TZ offset: <c>cameraLocalTime - cameraOffsetFromUtc = trackUtcTime</c>.
/// </summary>
public sealed class GpxGeotaggingService {
  private readonly IMetadataWriter _writer;

  public GpxGeotaggingService(IMetadataWriter writer) {
    ArgumentNullException.ThrowIfNull(writer);
    this._writer = writer;
  }

  public async Task<IReadOnlyList<GpxGeotagResult>> ApplyToFilesAsync(
    IReadOnlyList<FileInfo> files,
    GpxTrack track,
    TimeSpan cameraOffsetFromUtc,
    GpxTimelineMatcher? matcher = null,
    bool overwriteExistingGps = false,
    IProgress<FileInfo>? progress = null,
    IMetadataReader? reader = null,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(files);
    ArgumentNullException.ThrowIfNull(track);
    matcher ??= new GpxTimelineMatcher();
    reader ??= new MetadataReader();

    var results = new List<GpxGeotagResult>();
    foreach (var file in files) {
      cancellationToken.ThrowIfCancellationRequested();
      progress?.Report(file);

      var localTime = PhotoTimestampReader.ReadLocalCameraTime(file);
      if (localTime is null) {
        results.Add(new GpxGeotagResult(file, null, null, "no capture time"));
        continue;
      }

      if (!overwriteExistingGps) {
        var existing = await reader.ReadAsync(file, cancellationToken);
        if (existing.Gps is not null) {
          results.Add(new GpxGeotagResult(file, existing.Gps, localTime, "already has GPS"));
          continue;
        }
      }

      var utc = DateTime.SpecifyKind(localTime.Value - cameraOffsetFromUtc, DateTimeKind.Utc);
      var matched = matcher.Match(track, utc);
      if (matched is null) {
        results.Add(new GpxGeotagResult(file, null, localTime, "no trackpoint within tolerance"));
        continue;
      }

      try {
        await this._writer.ApplyAsync(file, new MetadataEdit { Gps = matched }, cancellationToken);
        results.Add(new GpxGeotagResult(file, matched, localTime));
      } catch (Exception ex) {
        results.Add(new GpxGeotagResult(file, null, localTime, $"write failed: {ex.Message}"));
      }
    }

    return results;
  }

  /// <summary>
  /// Derive <c>cameraOffsetFromUtc</c> from a single calibration shot: the
  /// user picks a photo taken at a place whose GPS-timestamp is known (e.g.
  /// photographed the GPS receiver's display, or took the shot while the
  /// logger was at a specific known waypoint). Returns the offset such that
  /// <c>cameraLocal - offset = knownUtc</c>.
  /// </summary>
  public static TimeSpan? CalibrateOffset(FileInfo calibrationPhoto, DateTime knownUtc) {
    ArgumentNullException.ThrowIfNull(calibrationPhoto);
    var localTime = PhotoTimestampReader.ReadLocalCameraTime(calibrationPhoto);
    if (localTime is null)
      return null;
    return DateTime.SpecifyKind(localTime.Value, DateTimeKind.Unspecified)
         - DateTime.SpecifyKind(knownUtc, DateTimeKind.Unspecified);
  }
}
