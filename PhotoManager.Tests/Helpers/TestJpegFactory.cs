using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Helpers;

/// <summary>
/// Generates 1x1 JPEGs with caller-supplied EXIF / GPS metadata so unit
/// tests can exercise the metadata-reading code paths without bundling
/// binary fixtures or shelling out to exiftool. Reads through
/// MetadataExtractor exactly the way the production code does.
///
/// EXIF date format follows the JEITA spec: "yyyy:MM:dd HH:mm:ss".
/// GPS coordinates are encoded as the rational triple (deg, min, sec) the
/// EXIF spec mandates, plus the N/S/E/W reference char.
/// </summary>
internal static class TestJpegFactory {
  private const string ExifDateFormat = "yyyy:MM:dd HH:mm:ss";

  public sealed record GpsValue(double Latitude, double Longitude, DateTime? Stamp = null);

  public static void Write(string path,
                          DateTime? exifIfd0DateTime = null,
                          DateTime? exifSubIfdDateTimeOriginal = null,
                          GpsValue? gps = null,
                          string? artist = null,
                          string? copyright = null,
                          int width = 1,
                          int height = 1) {
    using var img = new Image<Rgb24>(width, height);
    var profile = new ExifProfile();

    if (exifIfd0DateTime is { } d0)
      profile.SetValue(ExifTag.DateTime, d0.ToString(ExifDateFormat, CultureInfo.InvariantCulture));
    if (exifSubIfdDateTimeOriginal is { } d1)
      profile.SetValue(ExifTag.DateTimeOriginal, d1.ToString(ExifDateFormat, CultureInfo.InvariantCulture));
    if (!string.IsNullOrEmpty(artist))
      profile.SetValue(ExifTag.Artist, artist);
    if (!string.IsNullOrEmpty(copyright))
      profile.SetValue(ExifTag.Copyright, copyright);

    if (gps is { } g) {
      profile.SetValue(ExifTag.GPSLatitudeRef, g.Latitude >= 0 ? "N" : "S");
      profile.SetValue(ExifTag.GPSLatitude, ToRational(Math.Abs(g.Latitude)));
      profile.SetValue(ExifTag.GPSLongitudeRef, g.Longitude >= 0 ? "E" : "W");
      profile.SetValue(ExifTag.GPSLongitude, ToRational(Math.Abs(g.Longitude)));
      if (g.Stamp is { } stamp) {
        profile.SetValue(ExifTag.GPSDateStamp, stamp.ToString("yyyy:MM:dd", CultureInfo.InvariantCulture));
        profile.SetValue(ExifTag.GPSTimestamp, new[] {
          new Rational((uint)stamp.Hour, 1),
          new Rational((uint)stamp.Minute, 1),
          new Rational((uint)stamp.Second, 1)
        });
      }
    }

    img.Metadata.ExifProfile = profile;
    img.SaveAsJpeg(path);
  }

  /// <summary>Convert a decimal degrees value to the EXIF (deg, min, sec) rational triple.</summary>
  private static Rational[] ToRational(double decimalDegrees) {
    var degrees = (uint)Math.Floor(decimalDegrees);
    var minuteFloat = (decimalDegrees - degrees) * 60.0;
    var minutes = (uint)Math.Floor(minuteFloat);
    var secondsScaled = (uint)Math.Round((minuteFloat - minutes) * 60.0 * 1000.0);
    return new[] {
      new Rational(degrees, 1),
      new Rational(minutes, 1),
      new Rational(secondsScaled, 1000)
    };
  }
}
