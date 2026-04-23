using PhotoManager.Core.Metadata;

namespace PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class DirectionTargetPlacesXmpTests {
  [Test]
  public void RoundTrip_ImageDirection_PreservesDegreesAndReference() {
    var original = new FullMetadata {
      ImageDirection = new ImageDirection(123.45, DirectionReference.Magnetic)
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.ImageDirection, Is.Not.Null);
      Assert.That(parsed.ImageDirection!.Value.Degrees, Is.EqualTo(123.45).Within(1e-3));
      Assert.That(parsed.ImageDirection.Value.Reference, Is.EqualTo(DirectionReference.Magnetic));
    });
  }

  [Test]
  public void RoundTrip_ImageDirection_TrueRefDefault() {
    var original = new FullMetadata {
      ImageDirection = new ImageDirection(270.0)  // default is True
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.That(parsed.ImageDirection!.Value.Reference, Is.EqualTo(DirectionReference.True));
  }

  [Test]
  public void RoundTrip_TargetGps_PreservesCoordinates() {
    var original = new FullMetadata {
      TargetGps = new GpsCoordinate(48.8584, 2.2945)
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.TargetGps, Is.Not.Null);
      Assert.That(parsed.TargetGps!.Value.Latitude, Is.EqualTo(48.8584).Within(1e-4));
      Assert.That(parsed.TargetGps.Value.Longitude, Is.EqualTo(2.2945).Within(1e-4));
    });
  }

  [Test]
  public void RoundTrip_AllPlaceFields_Preserved() {
    var original = new FullMetadata {
      Location = "Eiffel Tower",
      City = "Paris",
      State = "Île-de-France",
      Country = "France",
      CountryCode = "FR"
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.Location, Is.EqualTo("Eiffel Tower"));
      Assert.That(parsed.City, Is.EqualTo("Paris"));
      Assert.That(parsed.State, Is.EqualTo("Île-de-France"));
      Assert.That(parsed.Country, Is.EqualTo("France"));
      Assert.That(parsed.CountryCode, Is.EqualTo("FR"));
    });
  }

  [Test]
  public void Parse_GeoSetterStyleXmp_ExtractsCityAndCountry() {
    // GeoSetter writes photoshop:City/State/Country + Iptc4xmpCore:Location/CountryCode.
    const string xml = """
      <?xml version="1.0" encoding="UTF-8"?>
      <x:xmpmeta xmlns:x="adobe:ns:meta/">
        <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/"
            xmlns:Iptc4xmpCore="http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/">
            <photoshop:City>Berlin</photoshop:City>
            <photoshop:State>Berlin</photoshop:State>
            <photoshop:Country>Germany</photoshop:Country>
            <Iptc4xmpCore:Location>Brandenburger Tor</Iptc4xmpCore:Location>
            <Iptc4xmpCore:CountryCode>DE</Iptc4xmpCore:CountryCode>
          </rdf:Description>
        </rdf:RDF>
      </x:xmpmeta>
      """;

    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.City, Is.EqualTo("Berlin"));
      Assert.That(parsed.Country, Is.EqualTo("Germany"));
      Assert.That(parsed.Location, Is.EqualTo("Brandenburger Tor"));
      Assert.That(parsed.CountryCode, Is.EqualTo("DE"));
    });
  }

  [Test]
  public void ImageDirection_IsValid_RejectsOutOfRange() {
    Assert.Multiple(() => {
      Assert.That(new ImageDirection(-1).IsValid, Is.False);
      Assert.That(new ImageDirection(361).IsValid, Is.False);
      Assert.That(new ImageDirection(0).IsValid, Is.True);
      Assert.That(new ImageDirection(360).IsValid, Is.True);
      Assert.That(new ImageDirection(180).IsValid, Is.True);
    });
  }
}
