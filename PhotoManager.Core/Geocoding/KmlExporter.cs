using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Geocoding;

/// <summary>
/// User-tunable knobs for KML output. Defaults match the most common Google
/// Earth / GPX-viewer expectations: altitude when available, a per-pin
/// timestamp, and a description string built from the photo's place fields.
/// </summary>
public sealed record KmlExportOptions(
  string DocumentName = "PhotoManager photo locations",
  bool IncludeAltitude = true,
  bool IncludeTimestamp = true,
  bool IncludePlaceFields = true
);

/// <summary>
/// Writes a single KML 2.2 document — one <c>&lt;Placemark&gt;</c> per
/// geotagged photo — that any KML reader (Google Earth, gpx.studio, QGIS…)
/// can ingest. Coordinates are emitted in the KML quirk order
/// <c>lon,lat,alt</c> with invariant-culture decimals so a German locale
/// doesn't sneak commas into the numbers.
/// </summary>
public static class KmlExporter {
  private static readonly XNamespace Kml22 = "http://www.opengis.net/kml/2.2";

  /// <summary>
  /// Build the KML document in memory. Pure function so tests can assert on
  /// the document tree without ever touching disk.
  /// </summary>
  public static XDocument BuildDocument(
    IReadOnlyList<(FileInfo File, FullMetadata Metadata)> entries,
    KmlExportOptions options
  ) {
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(options);

    var document = new XElement(Kml22 + "Document",
      new XElement(Kml22 + "name", options.DocumentName ?? string.Empty)
    );

    foreach (var (file, metadata) in entries) {
      if (metadata.Gps is not { IsValid: true } gps)
        continue;
      document.Add(BuildPlacemark(file, metadata, gps, options));
    }

    return new XDocument(
      new XDeclaration("1.0", "UTF-8", null),
      new XElement(Kml22 + "kml", document)
    );
  }

  /// <summary>
  /// Build the document and persist it to <paramref name="destination"/>.
  /// Writes to a sibling <c>.tmp</c> file first then atomically replaces the
  /// target so an interrupted export can never truncate an existing file.
  /// </summary>
  public static async Task ExportAsync(
    IReadOnlyList<(FileInfo File, FullMetadata Metadata)> entries,
    FileInfo destination,
    KmlExportOptions? options = null,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(entries);
    ArgumentNullException.ThrowIfNull(destination);

    var doc = BuildDocument(entries, options ?? new KmlExportOptions());
    if (destination.Directory is { } dir && !dir.Exists)
      dir.Create();

    var tempPath = destination.FullName + ".tmp";
    var settings = new System.Xml.XmlWriterSettings {
      Async = true,
      Encoding = new UTF8Encoding(false),
      Indent = true
    };

    await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
    await using (var writer = System.Xml.XmlWriter.Create(stream, settings)) {
      await Task.Run(() => doc.Save(writer), cancellationToken);
      await writer.FlushAsync();
    }

    if (File.Exists(destination.FullName))
      File.Replace(tempPath, destination.FullName, destinationBackupFileName: null);
    else
      File.Move(tempPath, destination.FullName);
  }

  private static XElement BuildPlacemark(
    FileInfo file,
    FullMetadata metadata,
    GpsCoordinate gps,
    KmlExportOptions options
  ) {
    var placemark = new XElement(Kml22 + "Placemark",
      new XElement(Kml22 + "name", BuildName(file, metadata))
    );

    var description = options.IncludePlaceFields ? BuildDescription(metadata) : string.Empty;
    placemark.Add(new XElement(Kml22 + "description", description));

    if (options.IncludeTimestamp && metadata.DateCreated is { } captured) {
      var iso = ToIsoUtc(captured);
      placemark.Add(new XElement(Kml22 + "TimeStamp",
        new XElement(Kml22 + "when", iso)
      ));
    }

    placemark.Add(new XElement(Kml22 + "Point",
      new XElement(Kml22 + "coordinates", FormatCoordinates(gps, options.IncludeAltitude))
    ));

    return placemark;
  }

  private static string BuildName(FileInfo file, FullMetadata metadata) {
    if (!string.IsNullOrWhiteSpace(metadata.Title))
      return metadata.Title!;
    return file.Name;
  }

  private static string BuildDescription(FullMetadata metadata) {
    var place = JoinNonEmpty(", ", metadata.City, metadata.Country);
    var caption = metadata.Caption?.Trim();

    if (!string.IsNullOrEmpty(caption) && !string.IsNullOrEmpty(place))
      return $"{caption}  ·  {place}";
    if (!string.IsNullOrEmpty(caption))
      return caption;
    return place;
  }

  private static string JoinNonEmpty(string separator, params string?[] parts) {
    var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim());
    return string.Join(separator, kept);
  }

  private static string FormatCoordinates(GpsCoordinate gps, bool includeAltitude) {
    var lon = gps.Longitude.ToString("0.######", CultureInfo.InvariantCulture);
    var lat = gps.Latitude.ToString("0.######", CultureInfo.InvariantCulture);
    if (includeAltitude && gps.AltitudeMeters is { } alt) {
      var altText = alt.ToString("0.###", CultureInfo.InvariantCulture);
      return $"{lon},{lat},{altText}";
    }
    return $"{lon},{lat}";
  }

  private static string ToIsoUtc(DateTime value) {
    var utc = value.Kind switch {
      DateTimeKind.Utc => value,
      DateTimeKind.Local => value.ToUniversalTime(),
      _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
    return utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
  }
}
