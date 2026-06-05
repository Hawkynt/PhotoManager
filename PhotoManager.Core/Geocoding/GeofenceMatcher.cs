using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Geocoding;

/// <summary>
/// Pure-logic helper that decides which user-saved bookmarks contain a
/// given GPS coordinate inside their radius. No I/O, no metadata reads —
/// callers feed in the bookmarks they want considered. Multiple overlapping
/// bookmarks all match; ordering follows the input list so the first hit
/// can be treated as canonical when callers want a single result.
/// </summary>
public static class GeofenceMatcher {
  public static IReadOnlyList<MapBookmark> MatchAll(IReadOnlyList<MapBookmark> bookmarks, GpsCoordinate coordinate) {
    ArgumentNullException.ThrowIfNull(bookmarks);
    if (!coordinate.IsValid || bookmarks.Count == 0)
      return Array.Empty<MapBookmark>();

    var hits = new List<MapBookmark>();
    foreach (var bookmark in bookmarks) {
      if (bookmark.RadiusMeters <= 0)
        continue;
      var bookmarkGps = bookmark.ToGps();
      if (!bookmarkGps.IsValid)
        continue;
      var distance = GeoDistance.HaversineMeters(
        bookmarkGps.Latitude, bookmarkGps.Longitude,
        coordinate.Latitude, coordinate.Longitude);
      if (distance <= bookmark.RadiusMeters)
        hits.Add(bookmark);
    }
    return hits;
  }
}
