using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class GpsCoordinateTests {
  [Test]
  public void IsValid_WithinBounds_IsTrue() {
    var coord = new GpsCoordinate(37.7749, -122.4194);
    Assert.That(coord.IsValid, Is.True);
  }

  [Test]
  [TestCase(91.0, 0.0)]
  [TestCase(-91.0, 0.0)]
  [TestCase(0.0, 181.0)]
  [TestCase(0.0, -181.0)]
  public void IsValid_OutOfRange_IsFalse(double lat, double lon) {
    var coord = new GpsCoordinate(lat, lon);
    Assert.That(coord.IsValid, Is.False);
  }

  [Test]
  public void LatitudeAsXmpString_NorthernHemisphere_EndsWithN() {
    var coord = new GpsCoordinate(37.5, 0);
    Assert.That(coord.LatitudeAsXmpString(), Does.EndWith("N"));
  }

  [Test]
  public void LatitudeAsXmpString_SouthernHemisphere_EndsWithS() {
    var coord = new GpsCoordinate(-37.5, 0);
    Assert.That(coord.LatitudeAsXmpString(), Does.EndWith("S"));
  }

  [Test]
  public void LongitudeAsXmpString_EasternHemisphere_EndsWithE() {
    var coord = new GpsCoordinate(0, 122.5);
    Assert.That(coord.LongitudeAsXmpString(), Does.EndWith("E"));
  }

  [Test]
  public void LongitudeAsXmpString_WesternHemisphere_EndsWithW() {
    var coord = new GpsCoordinate(0, -122.5);
    Assert.That(coord.LongitudeAsXmpString(), Does.EndWith("W"));
  }

  [Test]
  public void XmpLatitude_RoundTrips_PreservesDecimalDegrees() {
    var original = new GpsCoordinate(37.8054, 0);
    var formatted = original.LatitudeAsXmpString();

    Assert.That(GpsCoordinate.TryParseXmpLatitude(formatted, out var parsed), Is.True);
    Assert.That(parsed, Is.EqualTo(37.8054).Within(1e-6));
  }

  [Test]
  public void XmpLongitude_RoundTrips_PreservesNegativeValues() {
    var original = new GpsCoordinate(0, -122.4194);
    var formatted = original.LongitudeAsXmpString();

    Assert.That(GpsCoordinate.TryParseXmpLongitude(formatted, out var parsed), Is.True);
    Assert.That(parsed, Is.EqualTo(-122.4194).Within(1e-6));
  }

  [Test]
  [TestCase("not-a-coordinate")]
  [TestCase("")]
  [TestCase("37,48.324")]     // missing hemisphere suffix
  [TestCase("37,48.324Z")]    // invalid suffix for latitude
  public void TryParseXmpLatitude_Invalid_ReturnsFalse(string input) {
    Assert.That(GpsCoordinate.TryParseXmpLatitude(input, out _), Is.False);
  }
}
