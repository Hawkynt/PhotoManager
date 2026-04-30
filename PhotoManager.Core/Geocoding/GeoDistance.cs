namespace PhotoManager.Core.Geocoding;

/// <summary>
/// Bare-bones haversine helper that operates on raw <c>double</c> latitude /
/// longitude pairs — handy for radius-search filtering loops where the caller
/// is already holding raw doubles (e.g. the WorldMapWindow nearby-search).
/// For coordinate-bearing geodesy use <see cref="GreatCircle"/>.
/// </summary>
public static class GeoDistance {
  /// <summary>Mean Earth radius in meters; matches <see cref="GreatCircle.EarthRadiusMeters"/>.</summary>
  public const double EarthRadiusMeters = GreatCircle.EarthRadiusMeters;

  /// <summary>
  /// Great-circle distance in meters between two lat/lon pairs (decimal
  /// degrees) using the haversine formula. Same accuracy class as
  /// <see cref="GreatCircle.DistanceMeters"/> — sub-kilometer over thousands
  /// of km — but with a primitive-doubles signature suitable for tight loops.
  /// </summary>
  public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2) {
    var phi1 = ToRadians(lat1);
    var phi2 = ToRadians(lat2);
    var deltaPhi = ToRadians(lat2 - lat1);
    var deltaLambda = ToRadians(lon2 - lon1);

    var sinDp = Math.Sin(deltaPhi / 2);
    var sinDl = Math.Sin(deltaLambda / 2);

    var h = sinDp * sinDp + Math.Cos(phi1) * Math.Cos(phi2) * sinDl * sinDl;
    var c = 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
    return EarthRadiusMeters * c;
  }

  private static double ToRadians(double deg) => deg * Math.PI / 180.0;
}
