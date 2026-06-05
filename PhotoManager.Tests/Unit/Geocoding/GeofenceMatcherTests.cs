using Hawkynt.PhotoManager.Core.Geocoding;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Tests.Unit.Geocoding;

[TestFixture]
public class GeofenceMatcherTests {
  private static readonly GpsCoordinate BrandenburgGate = new(52.5163, 13.3777);
  private static MapBookmark Home(string name, GpsCoordinate at, double radiusM) =>
    new() { Name = name, Latitude = at.Latitude, Longitude = at.Longitude, RadiusMeters = radiusM, City = name };

  [Test]
  public void MatchAll_CoordinateInsideRadius_ReturnsBookmark() {
    var bookmark = Home("Berlin", new GpsCoordinate(52.5200, 13.4050), 5000);
    var hits = GeofenceMatcher.MatchAll(new[] { bookmark }, BrandenburgGate);
    Assert.That(hits, Has.Count.EqualTo(1));
    Assert.That(hits[0].Name, Is.EqualTo("Berlin"));
  }

  [Test]
  public void MatchAll_CoordinateOutsideRadius_NoMatch() {
    var bookmark = Home("Reichstag", new GpsCoordinate(52.5186, 13.3762), 50);
    // ~600 m away — well outside the 50 m fence.
    var faraway = new GpsCoordinate(52.5163, 13.3777);
    var hits = GeofenceMatcher.MatchAll(new[] { bookmark }, faraway);
    Assert.That(hits, Is.Empty);
  }

  [Test]
  public void MatchAll_JustOutsideRadius_NoMatch() {
    var center = new GpsCoordinate(0, 0);
    var bookmark = Home("Origin", center, 500);
    // 0.01 deg lat at the equator ≈ 1112 m — clearly outside 500 m.
    var outside = new GpsCoordinate(0.01, 0);
    var hits = GeofenceMatcher.MatchAll(new[] { bookmark }, outside);
    Assert.That(hits, Is.Empty);
  }

  [Test]
  public void MatchAll_JustInsideRadius_Matches() {
    var center = new GpsCoordinate(0, 0);
    var bookmark = Home("Origin", center, 500);
    // 0.001 deg lat ≈ 111 m — well inside 500 m.
    var inside = new GpsCoordinate(0.001, 0);
    var hits = GeofenceMatcher.MatchAll(new[] { bookmark }, inside);
    Assert.That(hits, Has.Count.EqualTo(1));
  }

  [Test]
  public void MatchAll_OverlappingBookmarks_AllMatch() {
    var inner = Home("Inner", BrandenburgGate, 500);
    var outer = Home("Outer", BrandenburgGate, 5000);
    var hits = GeofenceMatcher.MatchAll(new[] { inner, outer }, BrandenburgGate);
    Assert.That(hits, Has.Count.EqualTo(2));
    Assert.That(hits.Select(h => h.Name), Is.EquivalentTo(new[] { "Inner", "Outer" }));
  }

  [Test]
  public void MatchAll_InvalidCoordinate_ReturnsEmpty() {
    var bookmark = Home("X", new GpsCoordinate(0, 0), 500);
    var hits = GeofenceMatcher.MatchAll(new[] { bookmark }, new GpsCoordinate(999, 999));
    Assert.That(hits, Is.Empty);
  }

  [Test]
  public void MatchAll_NonPositiveRadius_Skipped() {
    var bookmark = Home("X", BrandenburgGate, 0);
    var hits = GeofenceMatcher.MatchAll(new[] { bookmark }, BrandenburgGate);
    Assert.That(hits, Is.Empty);
  }

  [Test]
  public void MatchAll_500mRadius_InsideMatchesOutsideDoesNot() {
    var center = new GpsCoordinate(48.8584, 2.2945); // Eiffel Tower
    var bookmark = Home("Eiffel", center, 500);

    // ~250 m east → still inside.
    var inside = GreatCircle.Destination(center, bearingDegrees: 90, distanceMeters: 250);
    Assert.That(GeofenceMatcher.MatchAll(new[] { bookmark }, inside), Has.Count.EqualTo(1));

    // ~600 m east → just outside.
    var outside = GreatCircle.Destination(center, bearingDegrees: 90, distanceMeters: 600);
    Assert.That(GeofenceMatcher.MatchAll(new[] { bookmark }, outside), Is.Empty);
  }
}
