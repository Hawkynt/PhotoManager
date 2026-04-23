using PhotoManager.Core.Geocoding;
using PhotoManager.Core.Metadata;
using static PhotoManager.Core.Geocoding.Triangulation;

namespace PhotoManager.Tests.Unit.Geocoding;

[TestFixture]
public class TriangulationTests {
  [Test]
  public void PixelToHorizontalAngle_CenterPixel_IsZero() {
    var angle = Triangulation.PixelToHorizontalAngle(500, 1000, 60);
    Assert.That(angle, Is.EqualTo(0).Within(1e-6));
  }

  [Test]
  public void PixelToHorizontalAngle_LeftEdge_IsNegativeHalfFov() {
    var angle = Triangulation.PixelToHorizontalAngle(0, 1000, 60);
    Assert.That(angle, Is.EqualTo(-30).Within(1e-6));
  }

  [Test]
  public void PixelToHorizontalAngle_RightEdge_IsPositiveHalfFov() {
    var angle = Triangulation.PixelToHorizontalAngle(999.999, 1000, 60);
    Assert.That(angle, Is.EqualTo(30).Within(0.01));
  }

  [Test]
  public void SinglePointHeading_LandmarkAtCenter_HeadingEqualsBearingToLandmark() {
    var camera = new GpsCoordinate(0, 0);
    var landmarkGps = new GpsCoordinate(1, 0);  // directly north
    var heading = Triangulation.SinglePointHeading(
      camera,
      new Landmark(landmarkGps, PixelX: 500),
      imageWidth: 1000,
      horizontalFovDegrees: 60
    );

    // Landmark is at the photo's center → camera is pointing directly at it → heading 0.
    Assert.That(heading, Is.EqualTo(0).Within(0.01));
  }

  [Test]
  public void SinglePointHeading_LandmarkOnRightEdge_HeadingRotatesLeft() {
    var camera = new GpsCoordinate(0, 0);
    var landmarkGps = new GpsCoordinate(1, 0);  // directly north
    // Landmark appears at the right edge of the image (30° right of center).
    // For that to be true, the camera must have been pointing 30° LEFT of the
    // landmark, i.e. bearing -30° == 330°.
    var heading = Triangulation.SinglePointHeading(
      camera,
      new Landmark(landmarkGps, PixelX: 999.999),
      imageWidth: 1000,
      horizontalFovDegrees: 60
    );

    Assert.That(heading, Is.EqualTo(330).Within(0.05));
  }

  [Test]
  public void CameraHeadingFromTwoLandmarks_SymmetricLandmarks_HeadingPointsBetweenThem() {
    // Two landmarks equidistant left and right of the camera, both at the
    // same range; camera should be pointing directly between them.
    var camera = new GpsCoordinate(0, 0);

    // Landmark A bears 350° (north-northwest); landmark B bears 10° (north-northeast).
    var northwest = GreatCircle.Destination(camera, 350, 1000);
    var northeast = GreatCircle.Destination(camera, 10, 1000);

    // In the photo: A sits at x=250 (left of center), B at x=750 (right).
    var heading = Triangulation.CameraHeadingFromTwoLandmarks(
      camera,
      new Landmark(northwest, PixelX: 250),
      new Landmark(northeast, PixelX: 750),
      imageWidth: 1000,
      horizontalFovDegrees: 40
    );

    // Camera must be facing due north (0°) for both bearings to line up.
    Assert.That(heading, Is.EqualTo(0).Within(0.2));
  }

  [Test]
  public void CameraHeadingFromTwoLandmarks_OffCenterScene_MatchesExpectedBearing() {
    var camera = new GpsCoordinate(48.8584, 2.2945);  // Eiffel Tower-ish

    // Camera facing bearing 60° (ENE). Two landmarks placed in the scene:
    //   A at +10° from photo centre → pixelX/width - 0.5 = 10/FOV
    //   B at -15° from photo centre → pixelX/width - 0.5 = -15/FOV
    const double expectedHeading = 60.0;
    const double fov = 45.0;

    const double imageWidth = 1000;
    var pixelA = (0.5 + 10.0 / fov) * imageWidth;   // 722.2
    var pixelB = (0.5 + -15.0 / fov) * imageWidth;  // 166.7

    var landA = GreatCircle.Destination(camera, bearingDegrees: expectedHeading + 10, distanceMeters: 500);
    var landB = GreatCircle.Destination(camera, bearingDegrees: expectedHeading - 15, distanceMeters: 500);

    var heading = Triangulation.CameraHeadingFromTwoLandmarks(
      camera,
      new Landmark(landA, pixelA),
      new Landmark(landB, pixelB),
      imageWidth: imageWidth,
      horizontalFovDegrees: fov
    );

    Assert.That(heading, Is.EqualTo(expectedHeading).Within(0.1));
  }

  [Test]
  public void CameraHeadingFromTwoLandmarks_AroundNorthSeam_AveragesCorrectly() {
    // Both landmarks very close to north → per-point headings 359° and 1°
    // should average to 0°, not 180° (the naive arithmetic mean trap).
    var camera = new GpsCoordinate(0, 0);

    var almostNorthLeft  = GreatCircle.Destination(camera, 359, 1000);
    var almostNorthRight = GreatCircle.Destination(camera, 1, 1000);

    var heading = Triangulation.CameraHeadingFromTwoLandmarks(
      camera,
      new Landmark(almostNorthLeft,  500),  // both at centre → per-point heading = their bearing
      new Landmark(almostNorthRight, 500),
      imageWidth: 1000,
      horizontalFovDegrees: 60
    );

    // Circular mean of 359° and 1° is 0°.
    Assert.That(Math.Min(heading, 360 - heading), Is.EqualTo(0).Within(0.1),
      "circular mean across the 0°/360° seam should land at 0°, not 180°");
  }

  [Test]
  public void TargetFromCameraAndHeading_DefaultDistance_100Meters() {
    var camera = new GpsCoordinate(48.8584, 2.2945);
    var target = Triangulation.TargetFromCameraAndHeading(camera, headingDegrees: 90);

    Assert.That(GreatCircle.DistanceMeters(camera, target), Is.EqualTo(100).Within(0.5));
    Assert.That(GreatCircle.BearingDegrees(camera, target), Is.EqualTo(90).Within(0.01));
  }

  [Test]
  public void HorizontalFovFromFocalLength35_50mm_Approx40Degrees() {
    var fov = Triangulation.HorizontalFovFromFocalLength35(50);
    // Standard reference: 50mm on FF is ~39.6° horizontal.
    Assert.That(fov, Is.EqualTo(39.6).Within(0.2));
  }

  [Test]
  public void HorizontalFovFromFocalLength35_24mm_Approx74Degrees() {
    var fov = Triangulation.HorizontalFovFromFocalLength35(24);
    Assert.That(fov, Is.EqualTo(73.7).Within(0.3));
  }

  [Test]
  public void AverageBearings_AcrossNorthSeam_Zero() {
    Assert.That(Triangulation.AverageBearings(359, 1), Is.EqualTo(0).Within(1e-6));
  }

  [Test]
  public void AverageBearings_SymmetricAroundSouth_Is180() {
    Assert.That(Triangulation.AverageBearings(170, 190), Is.EqualTo(180).Within(1e-6));
  }

  [Test]
  public void HorizontalFovFromFocalLength35_InvalidInput_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(() => Triangulation.HorizontalFovFromFocalLength35(0));
    Assert.Throws<ArgumentOutOfRangeException>(() => Triangulation.HorizontalFovFromFocalLength35(-5));
  }
}
