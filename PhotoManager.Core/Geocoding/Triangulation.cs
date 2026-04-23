using PhotoManager.Core.Metadata;

namespace PhotoManager.Core.Geocoding;

/// <summary>
/// Computes camera heading and a derived "target" coordinate from two
/// landmarks visible in a photo whose positions the user has pinned on a map.
///
/// The idea: for a pinhole camera with horizontal field of view
/// <paramref name="horizontalFovDegrees"/>, a pixel column X in an image
/// <paramref name="imageWidth"/> pixels wide corresponds to a horizontal
/// angle (relative to the camera's centre axis) of
/// <c>(X / imageWidth - 0.5) × FOV</c>. If we know the real-world bearing
/// from camera to a landmark (compute via <see cref="GreatCircle.BearingDegrees"/>)
/// and the landmark's in-image angle, the camera heading is the difference.
/// Averaging two landmarks' solutions cancels out small pixel-picking errors.
///
/// Full resection (unknown camera position) needs at least 3 points or an
/// extra constraint; the methods here all require the camera GPS up front.
/// </summary>
public static class Triangulation {
  /// <summary>One landmark visible in the photo: where it really is (GPS) and where it appears in pixels (horizontal X).</summary>
  public readonly record struct Landmark(GpsCoordinate Gps, double PixelX);

  /// <summary>
  /// Camera heading (0..360°, north = 0) given two landmarks visible in the
  /// photo and the camera's own GPS. Averages the two per-landmark solutions
  /// in a way that wraps correctly across the 0°/360° seam.
  /// </summary>
  public static double CameraHeadingFromTwoLandmarks(
    GpsCoordinate cameraGps,
    Landmark landmarkA,
    Landmark landmarkB,
    double imageWidth,
    double horizontalFovDegrees
  ) {
    if (imageWidth <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
    if (horizontalFovDegrees is <= 0 or >= 180) throw new ArgumentOutOfRangeException(nameof(horizontalFovDegrees));

    var headingA = SinglePointHeading(cameraGps, landmarkA, imageWidth, horizontalFovDegrees);
    var headingB = SinglePointHeading(cameraGps, landmarkB, imageWidth, horizontalFovDegrees);

    return AverageBearings(headingA, headingB);
  }

  /// <summary>
  /// Heading solution from a single landmark. Exposed so callers can inspect
  /// per-point results (e.g. to warn when the two solutions diverge wildly,
  /// which usually means a mis-picked pixel or landmark).
  /// </summary>
  public static double SinglePointHeading(
    GpsCoordinate cameraGps,
    Landmark landmark,
    double imageWidth,
    double horizontalFovDegrees
  ) {
    if (imageWidth <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
    if (horizontalFovDegrees is <= 0 or >= 180) throw new ArgumentOutOfRangeException(nameof(horizontalFovDegrees));

    var bearing = GreatCircle.BearingDegrees(cameraGps, landmark.Gps);
    var pixelAngle = PixelToHorizontalAngle(landmark.PixelX, imageWidth, horizontalFovDegrees);
    return GreatCircle.NormalizeDegrees(bearing - pixelAngle);
  }

  /// <summary>
  /// Converts a horizontal pixel position to an angle relative to the image
  /// centre. Negative angles are left of centre, positive right, in degrees.
  /// </summary>
  public static double PixelToHorizontalAngle(double pixelX, double imageWidth, double horizontalFovDegrees)
    => (pixelX / imageWidth - 0.5) * horizontalFovDegrees;

  /// <summary>
  /// A conceptual "target" point — where the camera was pointing, projected
  /// <paramref name="distanceMeters"/> along the heading ray. Useful for
  /// populating <c>GPSDestLatitude</c>/<c>Longitude</c> when the user wants
  /// a recorded gaze point and doesn't care about the exact subject distance.
  /// </summary>
  public static GpsCoordinate TargetFromCameraAndHeading(
    GpsCoordinate cameraGps,
    double headingDegrees,
    double distanceMeters = 100
  ) => GreatCircle.Destination(cameraGps, headingDegrees, distanceMeters);

  /// <summary>
  /// Derives a best-effort horizontal field of view from a 35-mm equivalent
  /// focal length. For a FF frame (36 mm wide): <c>2·atan(18/focal)</c>.
  /// </summary>
  public static double HorizontalFovFromFocalLength35(double focalLengthMm) {
    if (focalLengthMm <= 0) throw new ArgumentOutOfRangeException(nameof(focalLengthMm));
    const double sensorHalfWidthMm = 36.0 / 2.0;
    var fovRadians = 2 * Math.Atan(sensorHalfWidthMm / focalLengthMm);
    return fovRadians * 180.0 / Math.PI;
  }

  /// <summary>
  /// Averages two compass bearings, handling the 0°/360° wrap-around. Simple
  /// arithmetic mean on 1° and 359° would give 180° — wrong; the circular
  /// mean via unit-vector averaging gives 0°.
  /// </summary>
  internal static double AverageBearings(double a, double b) {
    var ar = a * Math.PI / 180.0;
    var br = b * Math.PI / 180.0;

    var x = (Math.Cos(ar) + Math.Cos(br)) / 2.0;
    var y = (Math.Sin(ar) + Math.Sin(br)) / 2.0;

    var mean = Math.Atan2(y, x) * 180.0 / Math.PI;
    return GreatCircle.NormalizeDegrees(mean);
  }
}
