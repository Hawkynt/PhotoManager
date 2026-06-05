namespace Hawkynt.PhotoManager.Core.Geocoding;

/// <summary>
/// Pure-data result describing a sun or moon direction arrow to be drawn
/// on a map overlay. All geometry is expressed as pixel offsets from the
/// pin center so the UI layer can position the arrow without knowing
/// anything about the underlying astronomy.
/// </summary>
public sealed record SunMoonArrowData(
  /// <summary>Offset in pixels from the pin to the arrowhead (X = right, Y = up on screen).</summary>
  double DeltaX,
  double DeltaY,
  /// <summary>Compass azimuth in degrees (0 = N, 90 = E, ...), for the label.</summary>
  double AzimuthDegrees,
  /// <summary>Altitude in degrees above the horizon; negative = below.</summary>
  double AltitudeDegrees,
  /// <summary>True when the body is above the horizon and the arrow should be visible.</summary>
  bool IsVisible
);

/// <summary>
/// Computes <see cref="SunMoonArrowData"/> for both sun and moon given a
/// GPS coordinate, a UTC timestamp, and a base arrow length in pixels.
/// This is pure computation with no UI dependencies -- easy to unit test.
/// </summary>
public static class SunMoonArrowComputer {
  /// <summary>Default arrow length in pixels when the body sits exactly at the horizon.</summary>
  public const double DefaultBaseLength = 80.0;

  /// <summary>
  /// Compute both sun and moon arrows for a given position and time.
  /// </summary>
  public static (SunMoonArrowData Sun, SunMoonArrowData Moon) Compute(
    double latitude,
    double longitude,
    DateTime utc,
    double baseLength = DefaultBaseLength
  ) {
    var (sunAz, sunAlt) = SolarLunarCalculator.SunPosition(utc, latitude, longitude);
    var (moonAz, moonAlt) = SolarLunarCalculator.MoonPosition(utc, latitude, longitude);

    return (
      BuildArrow(sunAz, sunAlt, baseLength),
      BuildArrow(moonAz, moonAlt, baseLength)
    );
  }

  /// <summary>
  /// Convert an azimuth (compass degrees) and altitude (degrees above
  /// horizon) into a pixel-space arrow. The arrow points in the azimuth
  /// direction with length scaled by <c>cos(altitude)</c>: body at horizon
  /// gives full length; body overhead shrinks to zero.
  ///
  /// Screen coordinates: X positive = right, Y positive = up. The caller
  /// must negate Y for screen-down conventions if needed.
  /// </summary>
  public static SunMoonArrowData BuildArrow(double azimuthDegrees, double altitudeDegrees, double baseLength) {
    // Body below the horizon -> hide the arrow.
    if (altitudeDegrees < 0)
      return new SunMoonArrowData(0, 0, azimuthDegrees, altitudeDegrees, IsVisible: false);

    // cos(altitude) scaling: horizon (0 deg) -> 1.0, zenith (90 deg) -> 0.0.
    var altRad = altitudeDegrees * Math.PI / 180.0;
    var length = baseLength * Math.Cos(altRad);

    // Azimuth to math-angle: compass 0 deg (north) = screen up = math 90 deg.
    // compass -> math angle: math = 90 deg - compass
    var mathAngleRad = (90.0 - azimuthDegrees) * Math.PI / 180.0;

    var dx = length * Math.Cos(mathAngleRad);
    var dy = length * Math.Sin(mathAngleRad); // positive = up

    // Threshold: if length is effectively zero, mark as not visible.
    var visible = length >= 1.0;

    return new SunMoonArrowData(dx, dy, azimuthDegrees, altitudeDegrees, visible);
  }
}
