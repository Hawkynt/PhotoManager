using PhotoManager.Core.Geocoding;
using PhotoManager.Core.Metadata;

namespace PhotoManager.Tests.Unit.Geocoding;

[TestFixture]
public class GreatCircleTests {
  // Well-known pairs — reference values computed with the classic spherical
  // formulas; any correct implementation should land within a degree or two.
  [Test]
  public void Bearing_BerlinToParis_Southwestish() {
    var berlin = new GpsCoordinate(52.5200, 13.4050);
    var paris  = new GpsCoordinate(48.8566, 2.3522);

    var bearing = GreatCircle.BearingDegrees(berlin, paris);
    // Great-circle initial bearing Berlin → Paris is ~246.7°. Rhumb is ~253°.
    // Both are "west-southwest", which is what a user expects.
    Assert.That(bearing, Is.EqualTo(246.7).Within(1.0));
  }

  [Test]
  public void Bearing_DueNorth_Is0() {
    var a = new GpsCoordinate(0, 0);
    var b = new GpsCoordinate(1, 0);
    Assert.That(GreatCircle.BearingDegrees(a, b), Is.EqualTo(0).Within(1e-6));
  }

  [Test]
  public void Bearing_DueEast_Is90() {
    var a = new GpsCoordinate(0, 0);
    var b = new GpsCoordinate(0, 1);
    Assert.That(GreatCircle.BearingDegrees(a, b), Is.EqualTo(90).Within(1e-6));
  }

  [Test]
  public void Bearing_DueWest_Is270() {
    var a = new GpsCoordinate(0, 0);
    var b = new GpsCoordinate(0, -1);
    Assert.That(GreatCircle.BearingDegrees(a, b), Is.EqualTo(270).Within(1e-6));
  }

  [Test]
  public void Distance_Zero_WhenSamePoint() {
    var a = new GpsCoordinate(48.8584, 2.2945);
    Assert.That(GreatCircle.DistanceMeters(a, a), Is.EqualTo(0).Within(1e-6));
  }

  [Test]
  public void Distance_NewYorkToLondon_Approx5570km() {
    var ny = new GpsCoordinate(40.7128, -74.0060);
    var ld = new GpsCoordinate(51.5074, -0.1278);

    var d = GreatCircle.DistanceMeters(ny, ld);
    Assert.That(d / 1000, Is.EqualTo(5570).Within(30), "NY-London great-circle distance ~5570 km");
  }

  [Test]
  public void Destination_OneKilometerNorth_LandsOnExpectedLatitude() {
    var start = new GpsCoordinate(0, 0);
    var end = GreatCircle.Destination(start, bearingDegrees: 0, distanceMeters: 1000);

    // ~111.32 km per degree at the equator → 1 km ≈ 0.00898°.
    Assert.That(end.Latitude, Is.EqualTo(0.00898).Within(1e-4));
    Assert.That(end.Longitude, Is.EqualTo(0).Within(1e-4));
  }

  [Test]
  public void Destination_RoundTripsWithBearingAndDistance() {
    var start = new GpsCoordinate(48.8584, 2.2945);
    var bearing = 73.5;
    var distance = 4_250d;

    var dest = GreatCircle.Destination(start, bearing, distance);

    Assert.Multiple(() => {
      Assert.That(GreatCircle.DistanceMeters(start, dest), Is.EqualTo(distance).Within(1),
        "distance from start back to destination should round-trip within ~1 meter");
      Assert.That(GreatCircle.BearingDegrees(start, dest), Is.EqualTo(bearing).Within(0.01),
        "bearing from start to destination should match the input bearing");
    });
  }

  [Test]
  [TestCase(370, 10)]
  [TestCase(-90, 270)]
  [TestCase(720, 0)]
  [TestCase(0, 0)]
  public void NormalizeDegrees_WrapsCorrectly(double input, double expected) {
    Assert.That(GreatCircle.NormalizeDegrees(input), Is.EqualTo(expected).Within(1e-9));
  }
}
