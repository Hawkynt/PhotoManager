using PhotoManager.Core.Metadata;

namespace PhotoManager.Core.Geocoding;

/// <summary>
/// Great-circle (spherical-earth) geodesy helpers. Accuracy is plenty for
/// any photo-related workflow (sub-meter over short distances, sub-kilometer
/// over thousands); we don't need WGS84-ellipsoid precision for tagging
/// which direction a camera was pointing.
/// </summary>
public static class GreatCircle {
  /// <summary>Mean Earth radius in meters.</summary>
  public const double EarthRadiusMeters = 6_371_008.8;

  /// <summary>
  /// Initial bearing (forward azimuth) from <paramref name="from"/> to
  /// <paramref name="to"/> in degrees, normalized to 0..360 where 0 is north
  /// and 90 is east.
  /// </summary>
  public static double BearingDegrees(GpsCoordinate from, GpsCoordinate to) {
    var lat1 = ToRadians(from.Latitude);
    var lat2 = ToRadians(to.Latitude);
    var deltaLon = ToRadians(to.Longitude - from.Longitude);

    var y = Math.Sin(deltaLon) * Math.Cos(lat2);
    var x = Math.Cos(lat1) * Math.Sin(lat2)
          - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(deltaLon);

    var bearing = ToDegrees(Math.Atan2(y, x));
    return NormalizeDegrees(bearing);
  }

  /// <summary>
  /// Haversine distance in meters between two coordinates.
  /// </summary>
  public static double DistanceMeters(GpsCoordinate a, GpsCoordinate b) {
    var lat1 = ToRadians(a.Latitude);
    var lat2 = ToRadians(b.Latitude);
    var deltaLat = ToRadians(b.Latitude - a.Latitude);
    var deltaLon = ToRadians(b.Longitude - a.Longitude);

    var sinDeltaLat2 = Math.Sin(deltaLat / 2);
    var sinDeltaLon2 = Math.Sin(deltaLon / 2);

    var h = sinDeltaLat2 * sinDeltaLat2
          + Math.Cos(lat1) * Math.Cos(lat2) * sinDeltaLon2 * sinDeltaLon2;
    var c = 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
    return EarthRadiusMeters * c;
  }

  /// <summary>
  /// Destination coordinate given a start, initial bearing (degrees, 0 = north)
  /// and distance in meters along the great-circle path.
  /// </summary>
  public static GpsCoordinate Destination(GpsCoordinate from, double bearingDegrees, double distanceMeters) {
    var angularDistance = distanceMeters / EarthRadiusMeters;
    var bearing = ToRadians(NormalizeDegrees(bearingDegrees));
    var lat1 = ToRadians(from.Latitude);
    var lon1 = ToRadians(from.Longitude);

    var sinLat1 = Math.Sin(lat1);
    var cosLat1 = Math.Cos(lat1);
    var sinAng = Math.Sin(angularDistance);
    var cosAng = Math.Cos(angularDistance);

    var sinLat2 = sinLat1 * cosAng + cosLat1 * sinAng * Math.Cos(bearing);
    var lat2 = Math.Asin(sinLat2);

    var y = Math.Sin(bearing) * sinAng * cosLat1;
    var x = cosAng - sinLat1 * sinLat2;
    var lon2 = lon1 + Math.Atan2(y, x);

    // Normalize longitude to [-180, 180].
    var lonDeg = ToDegrees(lon2);
    lonDeg = ((lonDeg + 540) % 360) - 180;

    return new GpsCoordinate(ToDegrees(lat2), lonDeg, from.AltitudeMeters);
  }

  /// <summary>
  /// Wraps a degree value into the [0, 360) range. Handles both sides of the
  /// wrap (e.g. -0.1 → 359.9, 360 → 0, 720.5 → 0.5) including the float
  /// edge case where a tiny-negative input rounds up to exactly 360 after
  /// adding 360.
  /// </summary>
  public static double NormalizeDegrees(double degrees) {
    var r = degrees % 360;
    if (r < 0)
      r += 360;
    if (r >= 360)
      r -= 360;
    return r;
  }

  private static double ToRadians(double deg) => deg * Math.PI / 180.0;
  private static double ToDegrees(double rad) => rad * 180.0 / Math.PI;
}
