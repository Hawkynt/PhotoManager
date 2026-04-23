using PhotoManager.Core.Gpx;

namespace PhotoManager.Tests.Unit.Gpx;

[TestFixture]
public class GpxParserTests {
  private const string Sample = """
    <?xml version="1.0" encoding="UTF-8"?>
    <gpx version="1.1" creator="Test"
         xmlns="http://www.topografix.com/GPX/1/1">
      <trk>
        <name>Test track</name>
        <trkseg>
          <trkpt lat="47.5000" lon="9.5000">
            <ele>400</ele>
            <time>2026-04-23T08:00:00Z</time>
          </trkpt>
          <trkpt lat="47.5001" lon="9.5005">
            <time>2026-04-23T08:00:30Z</time>
          </trkpt>
          <trkpt lat="47.5003" lon="9.5009">
            <ele>405</ele>
            <time>2026-04-23T08:01:00Z</time>
          </trkpt>
        </trkseg>
      </trk>
    </gpx>
    """;

  [Test]
  public void Parse_WellFormedGpx_ProducesOrderedPoints() {
    var track = GpxParser.Parse(Sample);

    Assert.Multiple(() => {
      Assert.That(track.PointCount, Is.EqualTo(3));
      Assert.That(track.Points[0].Coordinate.Latitude, Is.EqualTo(47.5000).Within(1e-6));
      Assert.That(track.Points[0].TimeUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
      Assert.That(track.Points[0].Coordinate.AltitudeMeters, Is.EqualTo(400));
      Assert.That(track.Points[1].Coordinate.AltitudeMeters, Is.Null);
      Assert.That(track.StartUtc, Is.EqualTo(new DateTime(2026, 4, 23, 8, 0, 0, DateTimeKind.Utc)));
      Assert.That(track.EndUtc, Is.EqualTo(new DateTime(2026, 4, 23, 8, 1, 0, DateTimeKind.Utc)));
    });
  }

  [Test]
  public void Parse_RejectsNonGpx() {
    Assert.Throws<InvalidDataException>(() => GpxParser.Parse("<svg/>"));
  }

  [Test]
  public void Parse_IgnoresPointsWithoutTime() {
    const string xml = """
      <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
        <trk><trkseg>
          <trkpt lat="47.5" lon="9.5"><time>2026-01-01T00:00:00Z</time></trkpt>
          <trkpt lat="47.5" lon="9.5"/>
        </trkseg></trk>
      </gpx>
      """;

    var track = GpxParser.Parse(xml);
    Assert.That(track.PointCount, Is.EqualTo(1));
  }

  [Test]
  public void Parse_SupportsRoutes() {
    const string xml = """
      <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
        <rte>
          <rtept lat="47.5" lon="9.5"><time>2026-01-01T00:00:00Z</time></rtept>
        </rte>
      </gpx>
      """;

    var track = GpxParser.Parse(xml);
    Assert.That(track.PointCount, Is.EqualTo(1));
  }

  [Test]
  public void Parse_NonUtcTimestamp_ConvertsToUtc() {
    // 10:00 CEST = 08:00 UTC
    const string xml = """
      <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
        <trk><trkseg>
          <trkpt lat="47.5" lon="9.5"><time>2026-06-15T10:00:00+02:00</time></trkpt>
        </trkseg></trk>
      </gpx>
      """;

    var track = GpxParser.Parse(xml);
    Assert.Multiple(() => {
      Assert.That(track.Points[0].TimeUtc, Is.EqualTo(new DateTime(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc)));
    });
  }

  [Test]
  public void Parse_SortsOutOfOrderPoints() {
    const string xml = """
      <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
        <trk><trkseg>
          <trkpt lat="47.5" lon="9.5"><time>2026-01-01T00:01:00Z</time></trkpt>
          <trkpt lat="47.5" lon="9.5"><time>2026-01-01T00:00:00Z</time></trkpt>
        </trkseg></trk>
      </gpx>
      """;

    var track = GpxParser.Parse(xml);
    Assert.That(track.Points[0].TimeUtc, Is.LessThan(track.Points[1].TimeUtc));
  }
}
