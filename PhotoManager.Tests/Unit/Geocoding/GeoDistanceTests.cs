using PhotoManager.Core.Geocoding;

namespace PhotoManager.Tests.Unit.Geocoding;

[TestFixture]
public class GeoDistanceTests {
  // Reference distances are great-circle (haversine) values; any correct
  // implementation should land within 1% of these published numbers.

  [Test]
  public void HaversineMeters_BerlinToMunich_About504km() {
    var berlinLat = 52.5200; var berlinLon = 13.4050;
    var munichLat = 48.1351; var munichLon = 11.5820;

    var d = GeoDistance.HaversineMeters(berlinLat, berlinLon, munichLat, munichLon);
    Assert.That(d / 1000.0, Is.EqualTo(504.0).Within(504.0 * 0.01),
      "Berlin → Munich great-circle ≈ 504 km");
  }

  [Test]
  public void HaversineMeters_BerlinToParis_About880km() {
    var berlinLat = 52.5200; var berlinLon = 13.4050;
    var parisLat  = 48.8566; var parisLon  = 2.3522;

    var d = GeoDistance.HaversineMeters(berlinLat, berlinLon, parisLat, parisLon);
    Assert.That(d / 1000.0, Is.EqualTo(880.0).Within(880.0 * 0.01),
      "Berlin → Paris great-circle ≈ 880 km");
  }

  [Test]
  public void HaversineMeters_SamePoint_IsZero() {
    var d = GeoDistance.HaversineMeters(48.8584, 2.2945, 48.8584, 2.2945);
    Assert.That(d, Is.EqualTo(0).Within(1e-6));
  }

  [Test]
  public void HaversineMeters_Symmetric() {
    // d(A,B) == d(B,A) — basic sanity for any distance metric.
    var ab = GeoDistance.HaversineMeters(40.7128, -74.0060, 51.5074, -0.1278);
    var ba = GeoDistance.HaversineMeters(51.5074, -0.1278, 40.7128, -74.0060);
    Assert.That(ab, Is.EqualTo(ba).Within(1e-6));
  }

  [Test]
  public void HaversineMeters_OneDegreeAtEquator_About111km() {
    // Definition: a degree of latitude at the equator is ~111.32 km.
    var d = GeoDistance.HaversineMeters(0, 0, 0, 1);
    Assert.That(d / 1000.0, Is.EqualTo(111.32).Within(0.5));
  }
}
