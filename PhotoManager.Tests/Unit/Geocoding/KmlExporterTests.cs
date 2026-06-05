using System.Globalization;
using System.Xml.Linq;
using Hawkynt.PhotoManager.Core.Geocoding;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Tests.Unit.Geocoding;

[TestFixture]
public class KmlExporterTests {
  private static readonly XNamespace Kml22 = "http://www.opengis.net/kml/2.2";

  private static (FileInfo File, FullMetadata Metadata) Entry(
    string fileName,
    GpsCoordinate? gps = null,
    string? city = null,
    string? country = null,
    string? title = null,
    string? caption = null,
    DateTime? dateCreated = null
  ) {
    var file = new FileInfo(Path.Combine(Path.GetTempPath(), fileName));
    var md = new FullMetadata {
      Gps = gps,
      City = city,
      Country = country,
      Title = title,
      Caption = caption,
      DateCreated = dateCreated
    };
    return (file, md);
  }

  [Test]
  public void BuildDocument_EmptyEntries_NoPlacemarks() {
    var doc = KmlExporter.BuildDocument(Array.Empty<(FileInfo, FullMetadata)>(), new KmlExportOptions());

    var root = doc.Root!;
    Assert.That(root.Name, Is.EqualTo(Kml22 + "kml"));
    var document = root.Element(Kml22 + "Document");
    Assert.That(document, Is.Not.Null);
    Assert.That(document!.Element(Kml22 + "name")!.Value, Is.EqualTo("PhotoManager photo locations"));
    Assert.That(document.Elements(Kml22 + "Placemark"), Is.Empty);
  }

  [Test]
  public void BuildDocument_EntryWithGps_OnePlacemarkWithCoordinates() {
    var entry = Entry("IMG_0001.jpg", gps: new GpsCoordinate(52.52, 13.405));
    var doc = KmlExporter.BuildDocument(new[] { entry }, new KmlExportOptions());

    var placemarks = doc.Root!.Element(Kml22 + "Document")!.Elements(Kml22 + "Placemark").ToList();
    Assert.That(placemarks, Has.Count.EqualTo(1));

    var coords = placemarks[0].Element(Kml22 + "Point")!.Element(Kml22 + "coordinates")!.Value;
    // KML quirk: lon,lat (NOT lat,lon).
    Assert.That(coords, Does.EndWith("13.405,52.52"));
  }

  [Test]
  public void BuildDocument_EntryWithoutGps_SilentlySkipped() {
    var withGps = Entry("a.jpg", gps: new GpsCoordinate(10, 20));
    var withoutGps = Entry("b.jpg");
    var doc = KmlExporter.BuildDocument(new[] { withGps, withoutGps }, new KmlExportOptions());

    var placemarks = doc.Root!.Element(Kml22 + "Document")!.Elements(Kml22 + "Placemark").ToList();
    Assert.That(placemarks, Has.Count.EqualTo(1));
    Assert.That(placemarks[0].Element(Kml22 + "name")!.Value, Is.EqualTo("a.jpg"));
  }

  [Test]
  public void BuildDocument_AltitudePresent_FormatsLonLatAlt() {
    var entry = Entry("a.jpg", gps: new GpsCoordinate(52.52, 13.405, 55.0));
    var doc = KmlExporter.BuildDocument(new[] { entry }, new KmlExportOptions(IncludeAltitude: true));

    var coords = doc.Descendants(Kml22 + "coordinates").Single().Value;
    Assert.That(coords, Is.EqualTo("13.405,52.52,55"));
  }

  [Test]
  public void BuildDocument_AltitudeAbsent_FormatsLonLatOnly() {
    var entry = Entry("a.jpg", gps: new GpsCoordinate(52.52, 13.405));
    var doc = KmlExporter.BuildDocument(new[] { entry }, new KmlExportOptions(IncludeAltitude: true));

    var coords = doc.Descendants(Kml22 + "coordinates").Single().Value;
    Assert.That(coords, Is.EqualTo("13.405,52.52"));
  }

  [Test]
  public void BuildDocument_AltitudeSuppressed_FormatsLonLatOnly() {
    var entry = Entry("a.jpg", gps: new GpsCoordinate(52.52, 13.405, 55.0));
    var doc = KmlExporter.BuildDocument(new[] { entry }, new KmlExportOptions(IncludeAltitude: false));

    var coords = doc.Descendants(Kml22 + "coordinates").Single().Value;
    Assert.That(coords, Is.EqualTo("13.405,52.52"));
  }

  [Test]
  public void BuildDocument_Coordinates_AlwaysInvariantCulture() {
    var original = System.Threading.Thread.CurrentThread.CurrentCulture;
    try {
      System.Threading.Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
      var entry = Entry("a.jpg", gps: new GpsCoordinate(52.52, 13.405, 55.5));
      var doc = KmlExporter.BuildDocument(new[] { entry }, new KmlExportOptions(IncludeAltitude: true));

      var coords = doc.Descendants(Kml22 + "coordinates").Single().Value;
      // German culture would produce "13,405" / "52,52"; we explicitly use
      // invariant culture so the dot stays.
      Assert.That(coords, Is.EqualTo("13.405,52.52,55.5"));
    } finally {
      System.Threading.Thread.CurrentThread.CurrentCulture = original;
    }
  }

  [Test]
  public void BuildDocument_DateCreated_EmittedAsIso8601Utc() {
    var captured = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    var entry = Entry("a.jpg", gps: new GpsCoordinate(52.52, 13.405), dateCreated: captured);
    var doc = KmlExporter.BuildDocument(new[] { entry }, new KmlExportOptions(IncludeTimestamp: true));

    var when = doc.Descendants(Kml22 + "when").Single().Value;
    Assert.That(when, Is.EqualTo("2024-06-15T12:00:00Z"));

    // Must round-trip through DateTimeOffset.Parse with ISO 8601 semantics.
    var parsed = DateTimeOffset.Parse(when, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    Assert.That(parsed.UtcDateTime, Is.EqualTo(captured));
  }

  [Test]
  public void BuildDocument_TimestampSuppressed_NoTimeStampElement() {
    var entry = Entry("a.jpg", gps: new GpsCoordinate(52.52, 13.405), dateCreated: DateTime.UtcNow);
    var doc = KmlExporter.BuildDocument(new[] { entry }, new KmlExportOptions(IncludeTimestamp: false));

    Assert.That(doc.Descendants(Kml22 + "TimeStamp"), Is.Empty);
  }

  [Test]
  public void BuildDocument_CityAndCountry_BothFlowIntoDescription() {
    var entry = Entry("a.jpg", gps: new GpsCoordinate(0, 0), city: "Berlin", country: "Germany");
    var doc = KmlExporter.BuildDocument(new[] { entry }, new KmlExportOptions());

    var description = doc.Descendants(Kml22 + "description").Single().Value;
    Assert.That(description, Does.Contain("Berlin"));
    Assert.That(description, Does.Contain("Germany"));
  }

  [Test]
  public void BuildDocument_OnlyCity_DescriptionHasCity() {
    var entry = Entry("a.jpg", gps: new GpsCoordinate(0, 0), city: "Berlin");
    var doc = KmlExporter.BuildDocument(new[] { entry }, new KmlExportOptions());

    var description = doc.Descendants(Kml22 + "description").Single().Value;
    Assert.That(description, Is.EqualTo("Berlin"));
  }

  [Test]
  public void BuildDocument_NoCityNoCountryNoCaption_DescriptionEmpty() {
    var entry = Entry("a.jpg", gps: new GpsCoordinate(0, 0));
    var doc = KmlExporter.BuildDocument(new[] { entry }, new KmlExportOptions());

    var description = doc.Descendants(Kml22 + "description").Single().Value;
    Assert.That(description, Is.Empty);
  }

  [Test]
  public void BuildDocument_PlaceFieldsSuppressed_DescriptionEmptyEvenWithCity() {
    var entry = Entry("a.jpg", gps: new GpsCoordinate(0, 0), city: "Berlin", country: "Germany");
    var doc = KmlExporter.BuildDocument(new[] { entry }, new KmlExportOptions(IncludePlaceFields: false));

    var description = doc.Descendants(Kml22 + "description").Single().Value;
    Assert.That(description, Is.Empty);
  }

  [Test]
  public void BuildDocument_TitlePresent_OverridesFileNameInPlacemarkName() {
    var entry = Entry("IMG_0001.jpg", gps: new GpsCoordinate(0, 0), title: "Sunset over the Spree");
    var doc = KmlExporter.BuildDocument(new[] { entry }, new KmlExportOptions());

    var name = doc.Descendants(Kml22 + "Placemark").Single().Element(Kml22 + "name")!.Value;
    Assert.That(name, Is.EqualTo("Sunset over the Spree"));
  }

  [Test]
  public void BuildDocument_NoTitle_FallsBackToFileName() {
    var entry = Entry("IMG_0001.jpg", gps: new GpsCoordinate(0, 0));
    var doc = KmlExporter.BuildDocument(new[] { entry }, new KmlExportOptions());

    var name = doc.Descendants(Kml22 + "Placemark").Single().Element(Kml22 + "name")!.Value;
    Assert.That(name, Is.EqualTo("IMG_0001.jpg"));
  }

  [Test]
  public void BuildDocument_DocumentName_IsCustomisable() {
    var doc = KmlExporter.BuildDocument(
      Array.Empty<(FileInfo, FullMetadata)>(),
      new KmlExportOptions(DocumentName: "My Trip 2024"));

    var name = doc.Root!.Element(Kml22 + "Document")!.Element(Kml22 + "name")!.Value;
    Assert.That(name, Is.EqualTo("My Trip 2024"));
  }

  [Test]
  public async Task ExportAsync_RoundTripThroughDisk_PreservesPlacemarks() {
    var entries = new[] {
      Entry("a.jpg", gps: new GpsCoordinate(52.52, 13.405), city: "Berlin", country: "Germany"),
      Entry("b.jpg", gps: new GpsCoordinate(48.8566, 2.3522, 35.0), city: "Paris", country: "France"),
      Entry("c.jpg") // skipped
    };

    var temp = new FileInfo(Path.Combine(Path.GetTempPath(), $"kml-export-{Guid.NewGuid():N}.kml"));
    try {
      await KmlExporter.ExportAsync(entries, temp);

      Assert.That(temp.Exists, Is.True);

      var loaded = XDocument.Load(temp.FullName);
      var placemarks = loaded.Descendants(Kml22 + "Placemark").ToList();
      Assert.That(placemarks, Has.Count.EqualTo(2));

      var firstCoords = placemarks[0].Element(Kml22 + "Point")!.Element(Kml22 + "coordinates")!.Value;
      Assert.That(firstCoords, Is.EqualTo("13.405,52.52"));

      var secondCoords = placemarks[1].Element(Kml22 + "Point")!.Element(Kml22 + "coordinates")!.Value;
      Assert.That(secondCoords, Is.EqualTo("2.3522,48.8566,35"));
    } finally {
      temp.Refresh();
      if (temp.Exists)
        temp.Delete();
    }
  }

  [Test]
  public async Task ExportAsync_OverwritesExistingFile_ViaTempFile() {
    var temp = new FileInfo(Path.Combine(Path.GetTempPath(), $"kml-overwrite-{Guid.NewGuid():N}.kml"));
    try {
      await File.WriteAllTextAsync(temp.FullName, "old content");

      var entries = new[] { Entry("a.jpg", gps: new GpsCoordinate(0, 0)) };
      await KmlExporter.ExportAsync(entries, temp);

      var loaded = XDocument.Load(temp.FullName);
      Assert.That(loaded.Root!.Name, Is.EqualTo(Kml22 + "kml"));
      Assert.That(loaded.Descendants(Kml22 + "Placemark").Count(), Is.EqualTo(1));

      // Tmp file should be cleaned up.
      Assert.That(File.Exists(temp.FullName + ".tmp"), Is.False);
    } finally {
      temp.Refresh();
      if (temp.Exists)
        temp.Delete();
    }
  }
}
