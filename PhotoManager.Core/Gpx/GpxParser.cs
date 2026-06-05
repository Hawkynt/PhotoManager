using System.Globalization;
using System.Xml.Linq;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Gpx;

/// <summary>
/// Parses GPX 1.0 and 1.1 into a flat, time-ordered list of trackpoints.
/// Handles both <c>&lt;trkpt&gt;</c> (tracks) and <c>&lt;rtept&gt;</c> (routes);
/// ignores waypoints that have no timestamp since the whole workflow hinges
/// on matching by time.
/// </summary>
public static class GpxParser {
  public static GpxTrack Parse(string xml) {
    ArgumentException.ThrowIfNullOrWhiteSpace(xml);

    XDocument doc;
    try {
      doc = XDocument.Parse(xml);
    } catch (Exception ex) {
      throw new InvalidDataException("Not a valid GPX file.", ex);
    }

    if (doc.Root == null || doc.Root.Name.LocalName != "gpx")
      throw new InvalidDataException("Root element must be <gpx>.");

    // Walk every element and pick up anything that looks like a trackpoint or
    // routepoint. Namespace-agnostic via LocalName so 1.0, 1.1, and vendor
    // extensions all flow through.
    var points = new List<GpxTrackPoint>();
    foreach (var el in doc.Root.Descendants()) {
      if (el.Name.LocalName is not ("trkpt" or "rtept" or "wpt"))
        continue;
      if (TryParsePoint(el) is { } point)
        points.Add(point);
    }

    points.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
    return new GpxTrack(points);
  }

  public static Task<GpxTrack> ParseFileAsync(FileInfo file, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(file);
    return Task.Run(() => {
      cancellationToken.ThrowIfCancellationRequested();
      if (!file.Exists)
        throw new FileNotFoundException("GPX file not found.", file.FullName);
      return Parse(File.ReadAllText(file.FullName));
    }, cancellationToken);
  }

  private static GpxTrackPoint? TryParsePoint(XElement el) {
    var latAttr = el.Attribute("lat")?.Value;
    var lonAttr = el.Attribute("lon")?.Value;
    if (latAttr == null || lonAttr == null)
      return null;

    if (!double.TryParse(latAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
        || !double.TryParse(lonAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
      return null;

    var timeEl = el.Elements().FirstOrDefault(c => c.Name.LocalName == "time");
    if (timeEl == null
        || !DateTime.TryParse(
          timeEl.Value,
          CultureInfo.InvariantCulture,
          DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
          out var time))
      return null;

    double? altitude = null;
    var eleEl = el.Elements().FirstOrDefault(c => c.Name.LocalName == "ele");
    if (eleEl != null && double.TryParse(eleEl.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ele))
      altitude = ele;

    return new GpxTrackPoint(DateTime.SpecifyKind(time, DateTimeKind.Utc), new GpsCoordinate(lat, lon, altitude));
  }
}
