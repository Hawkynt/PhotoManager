namespace Hawkynt.PhotoManager.Core.Geocoding;

/// <summary>
/// Sun &amp; moon sky positions for a UTC instant + ground location.
/// The sun routine implements the NOAA spreadsheet algorithm (Meeus Astronomical
/// Algorithms low-accuracy formulation): obliquity, equation of time, declination,
/// hour angle. Accurate to better than 0.1° for any photographic context.
/// The moon routine uses the Astronomical Almanac low-precision formula
/// (good to ~0.3° in position) — plenty for indicating which way the moon
/// was hanging when the shutter clicked.
///
/// Both methods return azimuth in degrees clockwise from true north
/// (0 = N, 90 = E, 180 = S, 270 = W) and altitude in degrees above the
/// horizon (negative = below).
/// </summary>
public static class SolarLunarCalculator {
  public static (double Azimuth, double Altitude) SunPosition(DateTime utc, double lat, double lon) {
    var jd = JulianDay(utc);
    var n = jd - 2451545.0;

    // Mean longitude and mean anomaly of the sun, in degrees.
    var meanLong = NormalizeDegrees(280.460 + 0.9856474 * n);
    var meanAnom = ToRadians(NormalizeDegrees(357.528 + 0.9856003 * n));

    // Ecliptic longitude (degrees → radians) — series truncated at the cubic.
    var eclipticLong = ToRadians(meanLong + 1.915 * Math.Sin(meanAnom) + 0.020 * Math.Sin(2 * meanAnom));

    // Mean obliquity of the ecliptic.
    var obliquity = ToRadians(23.439 - 0.0000004 * n);

    // Right ascension and declination.
    var ra = Math.Atan2(Math.Cos(obliquity) * Math.Sin(eclipticLong), Math.Cos(eclipticLong));
    var dec = Math.Asin(Math.Sin(obliquity) * Math.Sin(eclipticLong));

    return EquatorialToHorizontal(jd, ra, dec, lat, lon);
  }

  public static (double Azimuth, double Altitude) MoonPosition(DateTime utc, double lat, double lon) {
    // Astronomical Almanac low-precision lunar formulas. Time argument is
    // the number of Julian centuries from J2000.0 in TT — for photo-level
    // accuracy the TT/UT difference is negligible, so we use UT directly.
    var jd = JulianDay(utc);
    var d = jd - 2451545.0;

    // All angles below in degrees; reduce at the end.
    var lambda = ToRadians(NormalizeDegrees(218.32 + 481267.8813 * d / 36525.0
      + 6.29 * Math.Sin(ToRadians(NormalizeDegrees(134.9 + 477198.85 * d / 36525.0)))
      - 1.27 * Math.Sin(ToRadians(NormalizeDegrees(259.2 - 413335.38 * d / 36525.0)))
      + 0.66 * Math.Sin(ToRadians(NormalizeDegrees(235.7 + 890534.23 * d / 36525.0)))
      + 0.21 * Math.Sin(ToRadians(NormalizeDegrees(269.9 + 954397.70 * d / 36525.0)))
      - 0.19 * Math.Sin(ToRadians(NormalizeDegrees(357.5 + 35999.05  * d / 36525.0)))
      - 0.11 * Math.Sin(ToRadians(NormalizeDegrees(186.6 + 966404.05 * d / 36525.0)))
    ));

    var beta = ToRadians(
        5.13 * Math.Sin(ToRadians(NormalizeDegrees(93.3 + 483202.03 * d / 36525.0)))
      + 0.28 * Math.Sin(ToRadians(NormalizeDegrees(228.2 + 960400.87 * d / 36525.0)))
      - 0.28 * Math.Sin(ToRadians(NormalizeDegrees(318.3 + 6003.18   * d / 36525.0)))
      - 0.17 * Math.Sin(ToRadians(NormalizeDegrees(217.6 - 407332.20 * d / 36525.0)))
    );

    var obliquity = ToRadians(23.439 - 0.0000004 * d);

    // Convert ecliptic (lambda, beta) to equatorial (RA, dec).
    var sinDec = Math.Sin(beta) * Math.Cos(obliquity) + Math.Cos(beta) * Math.Sin(obliquity) * Math.Sin(lambda);
    var dec = Math.Asin(sinDec);
    var ra = Math.Atan2(
      Math.Sin(lambda) * Math.Cos(obliquity) - Math.Tan(beta) * Math.Sin(obliquity),
      Math.Cos(lambda)
    );

    return EquatorialToHorizontal(jd, ra, dec, lat, lon);
  }

  /// <summary>
  /// Convenience helper: returns a short description of the current solar
  /// twilight regime ("blue hour", "golden hour", "civil twilight"…) given
  /// the sun's altitude. Useful for the photo properties dialog hint.
  /// </summary>
  public static string DescribeTwilight(double sunAltitudeDegrees) {
    if (sunAltitudeDegrees > 6.0)
      return "Daylight";
    if (sunAltitudeDegrees > 0.0)
      return "Golden hour (sun low, warm light)";
    if (sunAltitudeDegrees > -6.0)
      return "Blue hour / civil twilight";
    if (sunAltitudeDegrees > -12.0)
      return "Nautical twilight";
    if (sunAltitudeDegrees > -18.0)
      return "Astronomical twilight";
    return "Night";
  }

  /// <summary>
  /// Greenwich Mean Sidereal Time in degrees for a given Julian Day,
  /// per IAU 1982 simplified formula (Meeus eq. 12.4 truncated). Adequate
  /// to a few arc-seconds for the centuries surrounding J2000.
  /// </summary>
  private static double GreenwichMeanSiderealTimeDegrees(double jd) {
    var t = (jd - 2451545.0) / 36525.0;
    var gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0)
             + t * t * (0.000387933 - t / 38710000.0);
    return NormalizeDegrees(gmst);
  }

  private static (double Azimuth, double Altitude) EquatorialToHorizontal(double jd, double ra, double dec, double lat, double lon) {
    var gmstDeg = GreenwichMeanSiderealTimeDegrees(jd);
    var lstDeg = NormalizeDegrees(gmstDeg + lon);
    var hourAngle = ToRadians(lstDeg) - ra;

    var latR = ToRadians(lat);
    var sinAlt = Math.Sin(latR) * Math.Sin(dec) + Math.Cos(latR) * Math.Cos(dec) * Math.Cos(hourAngle);
    var altitude = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0));

    // Standard astronomical convention here is "azimuth from south,
    // positive west". We rotate to "from north, clockwise" so callers can
    // compose with the camera-bearing arrows that already share that frame.
    var y = Math.Sin(hourAngle);
    var x = Math.Cos(hourAngle) * Math.Sin(latR) - Math.Tan(dec) * Math.Cos(latR);
    var azimuthFromSouth = Math.Atan2(y, x);
    var azimuthFromNorth = NormalizeDegrees(ToDegrees(azimuthFromSouth) + 180.0);

    return (azimuthFromNorth, ToDegrees(altitude));
  }

  private static double JulianDay(DateTime utc) {
    var dt = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
    int year = dt.Year, month = dt.Month;
    double day = dt.Day + (dt.Hour + (dt.Minute + dt.Second / 60.0) / 60.0) / 24.0;

    if (month <= 2) {
      year -= 1;
      month += 12;
    }
    var a = year / 100;
    var b = 2 - a + a / 4;
    return Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + b - 1524.5;
  }

  private static double NormalizeDegrees(double d) {
    var r = d % 360.0;
    if (r < 0) r += 360.0;
    if (r >= 360.0) r -= 360.0;
    return r;
  }

  private static double ToRadians(double deg) => deg * Math.PI / 180.0;
  private static double ToDegrees(double rad) => rad * 180.0 / Math.PI;
}
