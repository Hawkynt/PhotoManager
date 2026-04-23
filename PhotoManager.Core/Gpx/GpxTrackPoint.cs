using PhotoManager.Core.Metadata;

namespace PhotoManager.Core.Gpx;

/// <summary>
/// A single point along a GPS track. <see cref="TimeUtc"/> is authoritative —
/// GPX timestamps are always in UTC per the spec.
/// </summary>
public readonly record struct GpxTrackPoint(DateTime TimeUtc, GpsCoordinate Coordinate);

/// <summary>
/// A complete imported GPX track or set of tracks. Points are held flat in
/// time order so timestamp-matching can binary-search.
/// </summary>
public sealed record GpxTrack(IReadOnlyList<GpxTrackPoint> Points) {
  public DateTime? StartUtc => this.Points.Count == 0 ? null : this.Points[0].TimeUtc;
  public DateTime? EndUtc => this.Points.Count == 0 ? null : this.Points[^1].TimeUtc;
  public int PointCount => this.Points.Count;
}
