using Hawkynt.PhotoManager.Core.Gpx;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Tests.Unit.Gpx;

[TestFixture]
public class GpxTimelineMatcherTests {
  private static GpxTrack Track(params (DateTime time, double lat, double lon)[] points)
    => new(points.Select(p =>
        new GpxTrackPoint(DateTime.SpecifyKind(p.time, DateTimeKind.Utc), new GpsCoordinate(p.lat, p.lon))
      ).ToList());

  [Test]
  public void Match_ExactTimestamp_ReturnsPoint() {
    var track = Track(
      (new DateTime(2026, 4, 23, 12, 0, 0), 47.5, 9.5),
      (new DateTime(2026, 4, 23, 12, 0, 30), 47.51, 9.51),
      (new DateTime(2026, 4, 23, 12, 1, 0), 47.52, 9.52)
    );
    var m = new GpxTimelineMatcher { Interpolate = false };
    var r = m.Match(track, DateTime.SpecifyKind(new DateTime(2026, 4, 23, 12, 0, 30), DateTimeKind.Utc));
    Assert.That(r!.Value.Latitude, Is.EqualTo(47.51).Within(1e-6));
  }

  [Test]
  public void Match_Interpolates_BetweenStraddlingPoints() {
    var track = Track(
      (new DateTime(2026, 4, 23, 12, 0, 0), 47.5, 9.5),
      (new DateTime(2026, 4, 23, 12, 1, 0), 47.6, 9.6)
    );
    var m = new GpxTimelineMatcher { Interpolate = true, MaxToleranceSeconds = 120 };
    var r = m.Match(track, DateTime.SpecifyKind(new DateTime(2026, 4, 23, 12, 0, 30), DateTimeKind.Utc));
    Assert.Multiple(() => {
      Assert.That(r, Is.Not.Null);
      Assert.That(r!.Value.Latitude, Is.EqualTo(47.55).Within(1e-6));
      Assert.That(r!.Value.Longitude, Is.EqualTo(9.55).Within(1e-6));
    });
  }

  [Test]
  public void Match_BeforeTrackStart_WithinTolerance_ReturnsFirst() {
    var track = Track(
      (new DateTime(2026, 4, 23, 12, 0, 0), 47.5, 9.5),
      (new DateTime(2026, 4, 23, 12, 0, 30), 47.51, 9.51)
    );
    var m = new GpxTimelineMatcher { Interpolate = false };
    var r = m.Match(track, DateTime.SpecifyKind(new DateTime(2026, 4, 23, 11, 59, 30), DateTimeKind.Utc));
    Assert.That(r!.Value.Latitude, Is.EqualTo(47.5).Within(1e-6));
  }

  [Test]
  public void Match_BeyondTolerance_ReturnsNull() {
    var track = Track(
      (new DateTime(2026, 4, 23, 12, 0, 0), 47.5, 9.5)
    );
    var m = new GpxTimelineMatcher { MaxToleranceSeconds = 60 };
    var r = m.Match(track, DateTime.SpecifyKind(new DateTime(2026, 4, 23, 13, 0, 0), DateTimeKind.Utc));
    Assert.That(r, Is.Null);
  }

  [Test]
  public void Match_EmptyTrack_ReturnsNull() {
    var m = new GpxTimelineMatcher();
    var r = m.Match(new GpxTrack(Array.Empty<GpxTrackPoint>()), DateTime.UtcNow);
    Assert.That(r, Is.Null);
  }
}
