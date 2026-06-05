using Hawkynt.PhotoManager.Core.Geocoding;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Tests.Unit.Geocoding;

[TestFixture]
public class PhotoResectionTests {
  /// <summary>
  /// For a given (camera, heading, FOV) and landmark GPS, produce the pixel
  /// position the landmark would occupy in the image. Inverts
  /// <c>PixelToHorizontalAngle</c>.
  /// </summary>
  private static double SyntheticPixel(
    GpsCoordinate camera,
    double heading,
    GpsCoordinate landmark,
    double imageWidth,
    double fovDegrees
  ) {
    var bearing = GreatCircle.BearingDegrees(camera, landmark);
    // Signed relative angle (-fov/2 .. +fov/2)
    var rel = GreatCircle.NormalizeDegrees(bearing - heading);
    if (rel > 180)
      rel -= 360;
    return (rel / fovDegrees + 0.5) * imageWidth;
  }

  [Test]
  public void Solve_RecoversKnownCamera_WithinAFewMeters() {
    // Three landmarks around a known camera. Using roughly 200m spacing so
    // the resection geometry is well-conditioned.
    var camera = new GpsCoordinate(47.5000, 9.5000);
    var heading = 45.0;
    const double fov = 70.0;
    const double width = 4000.0;

    var a = new GpsCoordinate(47.5018, 9.4990);
    var b = new GpsCoordinate(47.5020, 9.5022);
    var c = new GpsCoordinate(47.5005, 9.5035);

    var landmarks = new[] {
      new PhotoResection.LandmarkObservation(a, SyntheticPixel(camera, heading, a, width, fov)),
      new PhotoResection.LandmarkObservation(b, SyntheticPixel(camera, heading, b, width, fov)),
      new PhotoResection.LandmarkObservation(c, SyntheticPixel(camera, heading, c, width, fov))
    };

    var result = PhotoResection.Solve(landmarks, width, fov);

    Assert.That(result, Is.Not.Null);
    var distance = GreatCircle.DistanceMeters(result!.CameraGps, camera);
    Assert.Multiple(() => {
      Assert.That(distance, Is.LessThan(2), "camera position should match to <2m");
      Assert.That(Math.Abs(GreatCircle.NormalizeDegrees(result.HeadingDegrees - heading)), Is.LessThan(0.5)
        .Or.GreaterThan(359.5), "heading should match to <0.5°");
      Assert.That(result.RmsAngularErrorDegrees, Is.LessThan(1));
    });
  }

  [Test]
  public void Solve_LandmarksTooCloseInPixels_ReturnsNull() {
    // Same landmark repeated at almost the same pixel — pathological.
    var a = new GpsCoordinate(47.5000, 9.5000);
    var b = new GpsCoordinate(47.5001, 9.5001);
    var c = new GpsCoordinate(47.5002, 9.5002);

    var landmarks = new[] {
      new PhotoResection.LandmarkObservation(a, 2000),
      new PhotoResection.LandmarkObservation(b, 2001),
      new PhotoResection.LandmarkObservation(c, 2002)
    };

    var result = PhotoResection.Solve(landmarks, 4000, 70);
    Assert.That(result, Is.Null);
  }

  [Test]
  public void Solve_TwoLandmarks_Throws() {
    var a = new GpsCoordinate(47.5000, 9.5000);
    var b = new GpsCoordinate(47.5001, 9.5001);

    var landmarks = new[] {
      new PhotoResection.LandmarkObservation(a, 1000),
      new PhotoResection.LandmarkObservation(b, 3000)
    };

    Assert.Throws<ArgumentException>(() => PhotoResection.Solve(landmarks, 4000, 70));
  }

  [Test]
  public void Solve_InvalidFov_Throws() {
    var a = new GpsCoordinate(47.5, 9.5);
    var landmarks = new[] {
      new PhotoResection.LandmarkObservation(a, 0),
      new PhotoResection.LandmarkObservation(a, 1),
      new PhotoResection.LandmarkObservation(a, 2)
    };
    Assert.Multiple(() => {
      Assert.Throws<ArgumentOutOfRangeException>(() => PhotoResection.Solve(landmarks, 4000, 0));
      Assert.Throws<ArgumentOutOfRangeException>(() => PhotoResection.Solve(landmarks, 4000, 200));
      Assert.Throws<ArgumentOutOfRangeException>(() => PhotoResection.Solve(landmarks, 0, 70));
    });
  }

  [Test]
  public void Solve_RecoversCameraLookingSouthwest() {
    // Sanity check a different heading so we're not accidentally locked to 0°.
    var camera = new GpsCoordinate(47.5000, 9.5000);
    var heading = 225.0;
    const double fov = 60.0;
    const double width = 3000.0;

    var a = new GpsCoordinate(47.4985, 9.4990);
    var b = new GpsCoordinate(47.4988, 9.4980);
    var c = new GpsCoordinate(47.4995, 9.4985);

    var landmarks = new[] {
      new PhotoResection.LandmarkObservation(a, SyntheticPixel(camera, heading, a, width, fov)),
      new PhotoResection.LandmarkObservation(b, SyntheticPixel(camera, heading, b, width, fov)),
      new PhotoResection.LandmarkObservation(c, SyntheticPixel(camera, heading, c, width, fov))
    };

    var result = PhotoResection.Solve(landmarks, width, fov);

    Assert.That(result, Is.Not.Null);
    var distance = GreatCircle.DistanceMeters(result!.CameraGps, camera);
    Assert.That(distance, Is.LessThan(2), "camera position should match to <2m");
    var headingDiff = Math.Abs(GreatCircle.NormalizeDegrees(result.HeadingDegrees - heading));
    Assert.That(headingDiff < 0.5 || headingDiff > 359.5, "heading should match to <0.5°");
  }
}
