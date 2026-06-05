using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Geocoding;

/// <summary>
/// Three-point photogrammetric resection: given three landmarks visible in
/// a photo with known world GPS and known horizontal pixel positions, and
/// a known camera horizontal FOV, compute the unknown camera GPS AND
/// heading in one pass. <see cref="Triangulation"/> requires the camera GPS
/// up front; this solves for it from three landmarks.
///
/// Approach: project the three landmarks onto a local tangent plane
/// (flat-earth around the first landmark's latitude — valid to sub-meter
/// over several km). The inscribed-angle theorem says an observer who sees
/// two landmarks at angle α lies on a circular arc through those landmarks;
/// intersecting two such arcs (one for pair AB, one for BC) yields the
/// observer up to a two-fold ambiguity, which we resolve by picking the
/// candidate whose predicted bearings match the observed pixel angles.
///
/// Works well for landmarks that aren't colinear and aren't all near the
/// same direction from the camera. Degenerates when the camera sits on the
/// circumcircle through the three landmarks (the classic "dangerous circle"
/// in surveying) — we return null in that case for the caller to surface.
/// </summary>
public static class PhotoResection {
  /// <summary>One landmark visible in the photo: real-world GPS + pixel X.</summary>
  public readonly record struct LandmarkObservation(GpsCoordinate Gps, double PixelX);

  /// <summary>Result of a successful resection.</summary>
  public sealed record Result(
    GpsCoordinate CameraGps,
    double HeadingDegrees,
    double RmsAngularErrorDegrees
  );

  /// <summary>
  /// Solve camera GPS + heading from three or more landmarks. Uses the first
  /// three landmarks; extras are ignored (the inscribed-angle method is an
  /// exact closed form for 3 points). Returns null when the geometry is
  /// degenerate (landmarks too close together in pixels, camera on the
  /// dangerous circle, or observations internally inconsistent).
  /// </summary>
  public static Result? Solve(
    IReadOnlyList<LandmarkObservation> landmarks,
    double imageWidth,
    double horizontalFovDegrees
  ) {
    ArgumentNullException.ThrowIfNull(landmarks);
    if (landmarks.Count < 3)
      throw new ArgumentException("Three landmarks are required.", nameof(landmarks));
    if (imageWidth <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
    if (horizontalFovDegrees is <= 0 or >= 180) throw new ArgumentOutOfRangeException(nameof(horizontalFovDegrees));

    // Flat-earth projection centered on the first landmark's latitude.
    // East = +X, North = +Y, both in meters.
    var originLat = landmarks[0].Gps.Latitude;
    var originLon = landmarks[0].Gps.Longitude;
    var metersPerDegLat = Math.PI / 180.0 * GreatCircle.EarthRadiusMeters;
    var metersPerDegLon = metersPerDegLat * Math.Cos(originLat * Math.PI / 180.0);

    var p = new (double X, double Y)[3];
    var ang = new double[3];
    for (var i = 0; i < 3; i++) {
      p[i] = (
        (landmarks[i].Gps.Longitude - originLon) * metersPerDegLon,
        (landmarks[i].Gps.Latitude - originLat) * metersPerDegLat
      );
      ang[i] = (landmarks[i].PixelX / imageWidth - 0.5) * horizontalFovDegrees;
    }

    var alphaAB = Math.Abs(ang[1] - ang[0]);
    var alphaBC = Math.Abs(ang[2] - ang[1]);
    if (alphaAB < 0.5 || alphaBC < 0.5)
      return null;  // pixels too close — numerically unreliable

    var circlesAB = InscribedAngleCircles(p[0], p[1], alphaAB);
    var circlesBC = InscribedAngleCircles(p[1], p[2], alphaBC);

    Result? best = null;
    foreach (var cAB in circlesAB) {
      foreach (var cBC in circlesBC) {
        foreach (var pt in IntersectCircles(cAB, cBC)) {
          var candidate = Evaluate(pt, p, ang, originLat, originLon, metersPerDegLat, metersPerDegLon);
          if (candidate == null)
            continue;
          if (best == null || candidate.RmsAngularErrorDegrees < best.RmsAngularErrorDegrees)
            best = candidate;
        }
      }
    }

    return best;
  }

  /// <summary>
  /// Both candidate circles (mirrors across the AB chord) on which an observer
  /// seeing AB at angle α must lie. We can't tell which side without extra
  /// info, so we try both and let the overall fit pick.
  /// </summary>
  private static ((double X, double Y) Center, double Radius)[] InscribedAngleCircles(
    (double X, double Y) a,
    (double X, double Y) b,
    double angleDegrees
  ) {
    var mx = (a.X + b.X) / 2;
    var my = (a.Y + b.Y) / 2;
    var dx = b.X - a.X;
    var dy = b.Y - a.Y;
    var len = Math.Sqrt(dx * dx + dy * dy);
    if (len < 1e-6)
      return Array.Empty<((double, double), double)>();

    var perpX = -dy / len;
    var perpY = dx / len;

    var alpha = angleDegrees * Math.PI / 180.0;
    var radius = (len / 2) / Math.Sin(alpha);
    var offset = (len / 2) / Math.Tan(alpha);

    return [
      ((mx + perpX * offset, my + perpY * offset), radius),
      ((mx - perpX * offset, my - perpY * offset), radius)
    ];
  }

  private static IEnumerable<(double X, double Y)> IntersectCircles(
    ((double X, double Y) Center, double Radius) c1,
    ((double X, double Y) Center, double Radius) c2
  ) {
    var dx = c2.Center.X - c1.Center.X;
    var dy = c2.Center.Y - c1.Center.Y;
    var d = Math.Sqrt(dx * dx + dy * dy);

    if (d < 1e-9 || d > c1.Radius + c2.Radius || d < Math.Abs(c1.Radius - c2.Radius))
      yield break;

    var a = (c1.Radius * c1.Radius - c2.Radius * c2.Radius + d * d) / (2 * d);
    var h2 = c1.Radius * c1.Radius - a * a;
    if (h2 < 0)
      yield break;
    var h = Math.Sqrt(h2);

    var midX = c1.Center.X + a * dx / d;
    var midY = c1.Center.Y + a * dy / d;

    var ox = h * dy / d;
    var oy = -h * dx / d;

    yield return (midX + ox, midY + oy);
    if (h > 1e-9)
      yield return (midX - ox, midY - oy);
  }

  private static Result? Evaluate(
    (double X, double Y) pt,
    (double X, double Y)[] p,
    double[] ang,
    double originLat,
    double originLon,
    double metersPerDegLat,
    double metersPerDegLon
  ) {
    // Reject candidates coincident with (or within half a meter of) a landmark.
    for (var i = 0; i < p.Length; i++) {
      var dxL = p[i].X - pt.X;
      var dyL = p[i].Y - pt.Y;
      if (dxL * dxL + dyL * dyL < 0.25)
        return null;
    }

    // Bearing convention: 0° = north, clockwise positive. In our XY frame
    // (east = +X, north = +Y), bearing = atan2(east_delta, north_delta).
    var bearings = new double[p.Length];
    for (var i = 0; i < p.Length; i++) {
      var east = p[i].X - pt.X;
      var north = p[i].Y - pt.Y;
      bearings[i] = Math.Atan2(east, north) * 180.0 / Math.PI;
    }

    // Average across all landmarks for a stable heading estimate.
    var heading = AverageHeadingFromAngles(bearings, ang);

    double sumSq = 0;
    for (var i = 0; i < p.Length; i++) {
      var predicted = GreatCircle.NormalizeDegrees(heading + ang[i]);
      var diff = AbsAngleDiff(predicted, GreatCircle.NormalizeDegrees(bearings[i]));
      sumSq += diff * diff;
    }
    var rms = Math.Sqrt(sumSq / p.Length);

    // Discard candidates with implausible angular error.
    if (rms > 5.0)
      return null;

    var deltaLat = pt.Y / metersPerDegLat;
    var deltaLon = pt.X / metersPerDegLon;
    var cameraGps = new GpsCoordinate(originLat + deltaLat, originLon + deltaLon);

    return new Result(cameraGps, heading, rms);
  }

  /// <summary>
  /// Best-fit heading across all (bearing, pixel-angle) pairs. The relation is
  /// bearing = heading + pixelAngle, so per-landmark heading estimates are
  /// (bearing - pixelAngle); we average via the unit-vector circular mean to
  /// handle wrap-around cleanly.
  /// </summary>
  private static double AverageHeadingFromAngles(double[] bearings, double[] pixelAngles) {
    double sumX = 0, sumY = 0;
    for (var i = 0; i < bearings.Length; i++) {
      var h = (bearings[i] - pixelAngles[i]) * Math.PI / 180.0;
      sumX += Math.Cos(h);
      sumY += Math.Sin(h);
    }
    var mean = Math.Atan2(sumY, sumX) * 180.0 / Math.PI;
    return GreatCircle.NormalizeDegrees(mean);
  }

  private static double AbsAngleDiff(double a, double b) {
    var d = ((a - b + 540) % 360) - 180;
    return Math.Abs(d);
  }
}
