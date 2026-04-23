using System.Globalization;

namespace PhotoManager.Core.Metadata;

/// <summary>
/// A geographic point in decimal degrees. Latitude is positive north of the
/// equator; longitude is positive east of the prime meridian. Altitude is in
/// meters above (positive) or below (negative) mean sea level.
/// </summary>
public readonly record struct GpsCoordinate(double Latitude, double Longitude, double? AltitudeMeters = null) {
  public bool IsValid =>
    this.Latitude is >= -90.0 and <= 90.0 &&
    this.Longitude is >= -180.0 and <= 180.0;

  /// <summary>
  /// Formats latitude as the XMP-style <c>&lt;degrees&gt;,&lt;minutes&gt;N|S</c> string.
  /// E.g. 37.8054° → "37,48.324N". Matches the format exiftool produces for XMP sidecars.
  /// </summary>
  public string LatitudeAsXmpString() => FormatDegreesMinutes(this.Latitude, 'N', 'S');

  /// <summary>
  /// Formats longitude as the XMP-style <c>&lt;degrees&gt;,&lt;minutes&gt;E|W</c> string.
  /// </summary>
  public string LongitudeAsXmpString() => FormatDegreesMinutes(this.Longitude, 'E', 'W');

  public static bool TryParseXmpLatitude(string value, out double degrees)
    => TryParseDegreesMinutes(value, 'N', 'S', out degrees);

  public static bool TryParseXmpLongitude(string value, out double degrees)
    => TryParseDegreesMinutes(value, 'E', 'W', out degrees);

  private static string FormatDegreesMinutes(double signed, char positiveSuffix, char negativeSuffix) {
    var absolute = Math.Abs(signed);
    var degrees = (int)Math.Floor(absolute);
    var minutes = (absolute - degrees) * 60.0;
    var suffix = signed >= 0 ? positiveSuffix : negativeSuffix;
    return string.Create(CultureInfo.InvariantCulture, $"{degrees},{minutes:0.######}{suffix}");
  }

  private static bool TryParseDegreesMinutes(string value, char positiveSuffix, char negativeSuffix, out double degrees) {
    degrees = 0;
    if (string.IsNullOrWhiteSpace(value))
      return false;

    var trimmed = value.Trim();
    var last = trimmed[^1];
    var sign = last switch {
      var c when c == positiveSuffix || c == char.ToLowerInvariant(positiveSuffix) => 1,
      var c when c == negativeSuffix || c == char.ToLowerInvariant(negativeSuffix) => -1,
      _ => 0
    };

    if (sign == 0)
      return false;

    var body = trimmed[..^1];
    var commaIndex = body.IndexOf(',');
    if (commaIndex < 0)
      return false;

    var degText = body[..commaIndex];
    var minText = body[(commaIndex + 1)..];

    if (!int.TryParse(degText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deg))
      return false;

    if (!double.TryParse(minText, NumberStyles.Float, CultureInfo.InvariantCulture, out var min))
      return false;

    degrees = sign * (deg + min / 60.0);
    return true;
  }
}
