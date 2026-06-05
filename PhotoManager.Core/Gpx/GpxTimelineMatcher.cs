using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Gpx;

/// <summary>
/// Given a photo's capture time, find the best-fitting GPS position in a
/// <see cref="GpxTrack"/>. Two strategies: nearest-in-time (snap to the
/// closest recorded point), or linear interpolation between the two
/// straddling points when both sides are within <see cref="MaxToleranceSeconds"/>.
/// Interpolation matches what GeoSetter does — more accurate when the track
/// has sparse points (e.g. a logger set to 10-second intervals).
/// </summary>
public sealed class GpxTimelineMatcher {
  /// <summary>Maximum time gap we'll accept before declining to geotag (default 60 s).</summary>
  public int MaxToleranceSeconds { get; init; } = 60;

  /// <summary>Interpolate between straddling points when both are within tolerance.</summary>
  public bool Interpolate { get; init; } = true;

  public GpsCoordinate? Match(GpxTrack track, DateTime photoTimeUtc) {
    ArgumentNullException.ThrowIfNull(track);
    if (track.Points.Count == 0)
      return null;

    var points = track.Points;

    // Binary search for the first point with time >= photoTimeUtc.
    var lo = 0;
    var hi = points.Count;
    while (lo < hi) {
      var mid = (lo + hi) / 2;
      if (points[mid].TimeUtc < photoTimeUtc)
        lo = mid + 1;
      else
        hi = mid;
    }

    GpxTrackPoint? before = lo > 0 ? points[lo - 1] : null;
    GpxTrackPoint? after = lo < points.Count ? points[lo] : null;

    var tolerance = TimeSpan.FromSeconds(this.MaxToleranceSeconds);

    if (this.Interpolate && before is { } b && after is { } a) {
      var span = a.TimeUtc - b.TimeUtc;
      if (span <= tolerance * 2) {
        var t = (photoTimeUtc - b.TimeUtc).TotalSeconds / Math.Max(1e-9, span.TotalSeconds);
        t = Math.Clamp(t, 0, 1);
        return LerpCoords(b.Coordinate, a.Coordinate, t);
      }
    }

    // Fall back to nearest. Pick whichever candidate is within tolerance and closer.
    GpxTrackPoint? best = null;
    TimeSpan bestGap = TimeSpan.MaxValue;

    if (before is { } bp) {
      var gap = photoTimeUtc - bp.TimeUtc;
      if (gap <= tolerance) {
        best = bp;
        bestGap = gap;
      }
    }
    if (after is { } ap) {
      var gap = ap.TimeUtc - photoTimeUtc;
      if (gap <= tolerance && gap < bestGap)
        best = ap;
    }

    return best?.Coordinate;
  }

  private static GpsCoordinate LerpCoords(GpsCoordinate a, GpsCoordinate b, double t) {
    var lat = a.Latitude + (b.Latitude - a.Latitude) * t;
    var lon = a.Longitude + (b.Longitude - a.Longitude) * t;
    double? alt = a.AltitudeMeters is { } altA && b.AltitudeMeters is { } altB
      ? altA + (altB - altA) * t
      : a.AltitudeMeters ?? b.AltitudeMeters;
    return new GpsCoordinate(lat, lon, alt);
  }
}
