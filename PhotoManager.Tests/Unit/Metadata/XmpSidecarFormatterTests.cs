using PhotoManager.Core.Metadata;

namespace PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class XmpSidecarFormatterTests {
  [Test]
  public void Serialize_EmptyState_ProducesWellFormedXml() {
    var xml = XmpSidecarFormatter.Serialize(new FullMetadata());

    Assert.That(xml, Does.StartWith("<?xml"));
    Assert.That(xml, Does.Contain("xmpmeta"));
    Assert.That(xml, Does.Contain("rdf:Description"));
  }

  [Test]
  public void Serialize_WithGps_IncludesLatLonAltitude() {
    var state = new FullMetadata {
      Gps = new GpsCoordinate(37.7749, -122.4194, 42.5)
    };

    var xml = XmpSidecarFormatter.Serialize(state);

    Assert.That(xml, Does.Contain("GPSLatitude"));
    Assert.That(xml, Does.Contain("GPSLongitude"));
    Assert.That(xml, Does.Contain("GPSAltitude"));
    Assert.That(xml, Does.Contain("GPSAltitudeRef"));
  }

  [Test]
  public void Serialize_WithNegativeAltitude_SetsRefToBelowSeaLevel() {
    var state = new FullMetadata {
      Gps = new GpsCoordinate(0, 0, -5.0)
    };

    var xml = XmpSidecarFormatter.Serialize(state);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.That(parsed.Gps!.Value.AltitudeMeters, Is.EqualTo(-5.0).Within(1e-6));
  }

  [Test]
  public void RoundTrip_AllFields_Preserved() {
    var original = new FullMetadata {
      Gps = new GpsCoordinate(48.8566, 2.3522, 35.0),
      Rating = 4,
      ColorLabel = "Green",
      Keywords = new[] { "paris", "travel", "eiffel" },
      Title = "Trip to Paris",
      Caption = "View from Montmartre"
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.Gps!.Value.Latitude, Is.EqualTo(48.8566).Within(1e-4));
      Assert.That(parsed.Gps.Value.Longitude, Is.EqualTo(2.3522).Within(1e-4));
      Assert.That(parsed.Gps.Value.AltitudeMeters, Is.EqualTo(35.0).Within(1e-3));
      Assert.That(parsed.Rating, Is.EqualTo(4));
      Assert.That(parsed.ColorLabel, Is.EqualTo("Green"));
      Assert.That(parsed.Keywords, Is.EquivalentTo(new[] { "paris", "travel", "eiffel" }));
      Assert.That(parsed.Title, Is.EqualTo("Trip to Paris"));
      Assert.That(parsed.Caption, Is.EqualTo("View from Montmartre"));
    });
  }

  [Test]
  public void Serialize_RejectedRating_PreservesNegativeOne() {
    var state = new FullMetadata { Rating = -1 };

    var xml = XmpSidecarFormatter.Serialize(state);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.That(parsed.Rating, Is.EqualTo(-1));
  }

  [Test]
  public void Parse_ForeignElements_AreReturnedInDocument() {
    const string xml = """
      <?xml version="1.0" encoding="UTF-8"?>
      <x:xmpmeta xmlns:x="adobe:ns:meta/">
        <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:lr="http://ns.adobe.com/lightroom/1.0/"
            xmlns:xmp="http://ns.adobe.com/xap/1.0/">
            <xmp:Rating>3</xmp:Rating>
            <lr:privateRMMetadata>some-adobe-value</lr:privateRMMetadata>
          </rdf:Description>
        </rdf:RDF>
      </x:xmpmeta>
      """;

    var (_, sourceDoc) = XmpSidecarFormatter.Parse(xml);
    var reserialized = XmpSidecarFormatter.Serialize(new FullMetadata { Rating = 3 }, sourceDoc);

    // Check semantically (by namespace + local name), not by prefix — XML serializers
    // are free to use xmlns="..." or lr:... so long as the element/namespace pair matches.
    var roundTripped = System.Xml.Linq.XDocument.Parse(reserialized);
    System.Xml.Linq.XNamespace lr = "http://ns.adobe.com/lightroom/1.0/";
    var foreignEl = roundTripped.Descendants(lr + "privateRMMetadata").SingleOrDefault();

    Assert.That(foreignEl, Is.Not.Null, "foreign lr: field should round-trip");
    Assert.That(foreignEl!.Value, Is.EqualTo("some-adobe-value"));
  }

  [Test]
  public void Serialize_EmptyKeywords_OmitsSubjectElement() {
    var state = new FullMetadata { Keywords = Array.Empty<string>() };

    var xml = XmpSidecarFormatter.Serialize(state);

    Assert.That(xml, Does.Not.Contain("dc:subject"));
  }
}
