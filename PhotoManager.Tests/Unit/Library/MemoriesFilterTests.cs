using PhotoManager.Core.Library;
using PhotoManager.Core.Metadata;

namespace PhotoManager.Tests.Unit.Library;

[TestFixture]
public class MemoriesFilterTests {
  private static (FileInfo, FullMetadata) Sample(string name, DateTime? captured, GpsCoordinate? gps = null) =>
    (new FileInfo(name), new FullMetadata { DateCreated = captured, Gps = gps });

  [Test]
  public void OnThisDay_ExcludesCurrentYear() {
    var today = new DateTime(2026, 4, 29);
    var photos = new[] {
      Sample("a.jpg", new DateTime(2026, 4, 29, 10, 0, 0)),  // today — excluded
      Sample("b.jpg", new DateTime(2024, 4, 29, 12, 0, 0)),  // matches
      Sample("c.jpg", new DateTime(2020, 4, 29, 8, 0, 0)),   // matches
      Sample("d.jpg", new DateTime(2024, 4, 30, 8, 0, 0))    // wrong day
    };
    var result = MemoriesFilter.OnThisDay(photos, today).ToList();
    Assert.That(result.Select(r => r.File.Name), Is.EquivalentTo(new[] { "b.jpg", "c.jpg" }));
  }

  [Test]
  public void OnThisDay_HandlesNoDate() {
    var today = new DateTime(2026, 4, 29);
    var photos = new[] {
      Sample("nodate.jpg", null),
      Sample("match.jpg", new DateTime(2024, 4, 29))
    };
    var result = MemoriesFilter.OnThisDay(photos, today).ToList();
    Assert.That(result.Select(r => r.File.Name), Is.EqualTo(new[] { "match.jpg" }));
  }

  [Test]
  public void OnThisTrip_RespectsRadius() {
    var anchor = new GpsCoordinate(52.52, 13.405);  // Berlin
    var anchorTime = new DateTime(2024, 6, 15, 12, 0, 0);
    var photos = new[] {
      Sample("near.jpg",  new DateTime(2024, 6, 15, 14, 0, 0), new GpsCoordinate(52.52, 13.41)),   // ~340 m
      Sample("far.jpg",   new DateTime(2024, 6, 15, 14, 0, 0), new GpsCoordinate(48.86, 2.35)),    // Paris
      Sample("anchor.jpg", anchorTime, anchor),                                                     // self → excluded
    };
    var result = MemoriesFilter.OnThisTrip(photos, anchor, anchorTime, radiusKm: 5).ToList();
    Assert.That(result.Select(r => r.File.Name), Is.EqualTo(new[] { "near.jpg" }));
  }

  [Test]
  public void OnThisTrip_RespectsTimeWindow() {
    var anchor = new GpsCoordinate(52.52, 13.405);
    var anchorTime = new DateTime(2024, 6, 15, 12, 0, 0);
    var photos = new[] {
      Sample("inside.jpg",  new DateTime(2024, 6, 17), new GpsCoordinate(52.521, 13.41)),  // +2 days
      Sample("outside.jpg", new DateTime(2024, 6, 25), new GpsCoordinate(52.521, 13.41)),  // +10 days
    };
    var result = MemoriesFilter.OnThisTrip(photos, anchor, anchorTime, radiusKm: 5, window: TimeSpan.FromDays(3)).ToList();
    Assert.That(result.Select(r => r.File.Name), Is.EqualTo(new[] { "inside.jpg" }));
  }

  [Test]
  public void OnThisTrip_SkipsMissingGpsOrDate() {
    var anchor = new GpsCoordinate(52.52, 13.405);
    var anchorTime = new DateTime(2024, 6, 15);
    var photos = new[] {
      Sample("no-gps.jpg", new DateTime(2024, 6, 15)),
      Sample("no-date.jpg", null, new GpsCoordinate(52.521, 13.41)),
      Sample("ok.jpg", new DateTime(2024, 6, 15, 13, 0, 0), new GpsCoordinate(52.521, 13.41))
    };
    var result = MemoriesFilter.OnThisTrip(photos, anchor, anchorTime).ToList();
    Assert.That(result.Select(r => r.File.Name), Is.EqualTo(new[] { "ok.jpg" }));
  }
}
