using Hawkynt.PhotoManager.Core.Geocoding;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Library;

/// <summary>
/// Pure-logic predicates powering the Memories window: photos taken on the
/// same calendar day in past years ("On this day") and photos within a
/// distance / time radius of an anchor photo ("On this trip"). The UI layer
/// renders the results — these helpers just say which files qualify.
/// </summary>
public static class MemoriesFilter {
  /// <summary>
  /// Photos whose <see cref="FullMetadata.DateCreated"/> matches today's
  /// month + day in any year other than the anchor's year. Items without a
  /// capture date are skipped.
  /// </summary>
  public static IEnumerable<(FileInfo File, FullMetadata Metadata)> OnThisDay(
    IEnumerable<(FileInfo File, FullMetadata Metadata)> photos,
    DateTime today
  ) {
    ArgumentNullException.ThrowIfNull(photos);
    var month = today.Month;
    var day = today.Day;
    var year = today.Year;

    foreach (var (file, md) in photos) {
      if (md.DateCreated is not { } captured)
        continue;
      if (captured.Month == month && captured.Day == day && captured.Year != year)
        yield return (file, md);
    }
  }

  /// <summary>
  /// Photos within <paramref name="radiusKm"/> kilometres of <paramref name="anchor"/>
  /// AND captured within ±<paramref name="window"/> of <paramref name="anchorTime"/>.
  /// Photos missing GPS or capture date are skipped. The anchor itself is
  /// excluded (zero distance + zero time delta = same photo).
  /// </summary>
  public static IEnumerable<(FileInfo File, FullMetadata Metadata)> OnThisTrip(
    IEnumerable<(FileInfo File, FullMetadata Metadata)> photos,
    GpsCoordinate anchor,
    DateTime anchorTime,
    double radiusKm = 5,
    TimeSpan? window = null
  ) {
    ArgumentNullException.ThrowIfNull(photos);
    var actualWindow = window ?? TimeSpan.FromDays(3);
    var radiusMeters = radiusKm * 1000.0;

    foreach (var (file, md) in photos) {
      if (md.Gps is not { } gps || !gps.IsValid)
        continue;
      if (md.DateCreated is not { } captured)
        continue;

      var dt = (captured - anchorTime).Duration();
      if (dt > actualWindow)
        continue;

      var distance = GreatCircle.DistanceMeters(anchor, gps);
      if (distance > radiusMeters)
        continue;

      // Skip the anchor photo itself: same point + same instant.
      if (distance < 1 && dt < TimeSpan.FromSeconds(1))
        continue;

      yield return (file, md);
    }
  }
}
