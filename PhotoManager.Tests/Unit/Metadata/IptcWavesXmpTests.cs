using PhotoManager.Core.Metadata;

namespace PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class IptcWavesXmpTests {
  [Test]
  public void RoundTrip_Wave1_AccessibilityAdminAndLocationShown_PreservesValues() {
    var original = new FullMetadata {
      AltTextAccessibility = "A sunset over Lake Constance.",
      ExtendedDescriptionAccessibility = "Golden-hour sunset with mountains in the background.",
      DescriptionWriter = "Photo editor",
      JobIdentifier = "JOB-2026-04-23-001",
      DigitalSourceType = "http://cv.iptc.org/newscodes/digitalsourcetype/digitalCapture",
      WebStatementOfRights = "https://example.com/rights/2026",
      Genre = "http://cv.iptc.org/newscodes/genre/Actuality",
      IptcImageRating = 4.5,
      WorldRegionCreated = "Europe",
      LocationCreatedId = "https://www.wikidata.org/wiki/Q3012",
      LocationShownCity = "Paris",
      LocationShownCountry = "France",
      LocationShownCountryCode = "FR",
      LocationShownWorldRegion = "Europe",
      LocationShownId = "https://www.wikidata.org/wiki/Q90"
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.AltTextAccessibility, Is.EqualTo(original.AltTextAccessibility));
      Assert.That(parsed.ExtendedDescriptionAccessibility, Is.EqualTo(original.ExtendedDescriptionAccessibility));
      Assert.That(parsed.DescriptionWriter, Is.EqualTo(original.DescriptionWriter));
      Assert.That(parsed.JobIdentifier, Is.EqualTo(original.JobIdentifier));
      Assert.That(parsed.DigitalSourceType, Is.EqualTo(original.DigitalSourceType));
      Assert.That(parsed.WebStatementOfRights, Is.EqualTo(original.WebStatementOfRights));
      Assert.That(parsed.Genre, Is.EqualTo(original.Genre));
      Assert.That(parsed.IptcImageRating, Is.EqualTo(original.IptcImageRating));
      Assert.That(parsed.WorldRegionCreated, Is.EqualTo(original.WorldRegionCreated));
      Assert.That(parsed.LocationCreatedId, Is.EqualTo(original.LocationCreatedId));
      Assert.That(parsed.LocationShownCity, Is.EqualTo(original.LocationShownCity));
      Assert.That(parsed.LocationShownCountry, Is.EqualTo(original.LocationShownCountry));
      Assert.That(parsed.LocationShownCountryCode, Is.EqualTo(original.LocationShownCountryCode));
      Assert.That(parsed.LocationShownWorldRegion, Is.EqualTo(original.LocationShownWorldRegion));
      Assert.That(parsed.LocationShownId, Is.EqualTo(original.LocationShownId));
    });
  }

  [Test]
  public void RoundTrip_Wave2_ContactPersonsEvent_PreservesValues() {
    var original = new FullMetadata {
      CreatorJobTitle = "Staff Photographer",
      CreatorContactAddress = "123 Main St",
      CreatorContactCity = "Berlin",
      CreatorContactState = "Berlin",
      CreatorContactPostalCode = "10115",
      CreatorContactCountry = "Germany",
      CreatorContactPhone = "+49 30 12345678",
      CreatorContactEmail = "photo@example.com",
      CreatorContactWebsite = "https://example.com",
      PersonsShown = new[] { "Alice Example", "Bob Sample" },
      Event = "2026 Spring Conference",
      EventId = "https://example.com/events/2026-spring"
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.CreatorJobTitle, Is.EqualTo(original.CreatorJobTitle));
      Assert.That(parsed.CreatorContactAddress, Is.EqualTo(original.CreatorContactAddress));
      Assert.That(parsed.CreatorContactCity, Is.EqualTo(original.CreatorContactCity));
      Assert.That(parsed.CreatorContactState, Is.EqualTo(original.CreatorContactState));
      Assert.That(parsed.CreatorContactPostalCode, Is.EqualTo(original.CreatorContactPostalCode));
      Assert.That(parsed.CreatorContactCountry, Is.EqualTo(original.CreatorContactCountry));
      Assert.That(parsed.CreatorContactPhone, Is.EqualTo(original.CreatorContactPhone));
      Assert.That(parsed.CreatorContactEmail, Is.EqualTo(original.CreatorContactEmail));
      Assert.That(parsed.CreatorContactWebsite, Is.EqualTo(original.CreatorContactWebsite));
      Assert.That(parsed.PersonsShown, Is.EqualTo(original.PersonsShown).AsCollection);
      Assert.That(parsed.Event, Is.EqualTo(original.Event));
      Assert.That(parsed.EventId, Is.EqualTo(original.EventId));
    });
  }

  [Test]
  public void RoundTrip_Wave3_ReleasesLicensorArtwork_PreservesValues() {
    var original = new FullMetadata {
      ModelReleaseStatus = "http://ns.useplus.org/ldf/vocab/MR-NAP",
      ModelReleaseId = "MR-1234",
      PropertyReleaseStatus = "http://ns.useplus.org/ldf/vocab/PR-NAP",
      PropertyReleaseId = "PR-5678",
      DataMining = "http://ns.useplus.org/ldf/vocab/DMI-PROHIBITED",
      LicensorName = "Agency X",
      LicensorId = "https://example.com/agencies/x",
      ImageSupplierName = "Supplier Y",
      ImageSupplierId = "https://example.com/suppliers/y",
      SupplierImageId = "SUP-0001",
      CopyrightOwnerName = "Jane Doe Estate",
      CopyrightOwnerId = "https://example.com/rights/jane-doe",
      ArtworkTitle = "The Starry Night",
      ArtworkCreator = "Vincent van Gogh",
      ArtworkDateCreated = "1889",
      ArtworkSource = "Museum of Modern Art",
      ArtworkCopyright = "Public Domain",
      ProductName = "Example Camera Pro",
      ProductGtin = "01234567890123",
      ProductDescription = "A high-end mirrorless camera."
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.ModelReleaseStatus, Is.EqualTo(original.ModelReleaseStatus));
      Assert.That(parsed.ModelReleaseId, Is.EqualTo(original.ModelReleaseId));
      Assert.That(parsed.PropertyReleaseStatus, Is.EqualTo(original.PropertyReleaseStatus));
      Assert.That(parsed.PropertyReleaseId, Is.EqualTo(original.PropertyReleaseId));
      Assert.That(parsed.DataMining, Is.EqualTo(original.DataMining));
      Assert.That(parsed.LicensorName, Is.EqualTo(original.LicensorName));
      Assert.That(parsed.LicensorId, Is.EqualTo(original.LicensorId));
      Assert.That(parsed.ImageSupplierName, Is.EqualTo(original.ImageSupplierName));
      Assert.That(parsed.ImageSupplierId, Is.EqualTo(original.ImageSupplierId));
      Assert.That(parsed.SupplierImageId, Is.EqualTo(original.SupplierImageId));
      Assert.That(parsed.CopyrightOwnerName, Is.EqualTo(original.CopyrightOwnerName));
      Assert.That(parsed.CopyrightOwnerId, Is.EqualTo(original.CopyrightOwnerId));
      Assert.That(parsed.ArtworkTitle, Is.EqualTo(original.ArtworkTitle));
      Assert.That(parsed.ArtworkCreator, Is.EqualTo(original.ArtworkCreator));
      Assert.That(parsed.ArtworkDateCreated, Is.EqualTo(original.ArtworkDateCreated));
      Assert.That(parsed.ArtworkSource, Is.EqualTo(original.ArtworkSource));
      Assert.That(parsed.ArtworkCopyright, Is.EqualTo(original.ArtworkCopyright));
      Assert.That(parsed.ProductName, Is.EqualTo(original.ProductName));
      Assert.That(parsed.ProductGtin, Is.EqualTo(original.ProductGtin));
      Assert.That(parsed.ProductDescription, Is.EqualTo(original.ProductDescription));
    });
  }

  [Test]
  public void LocationShownGps_RoundTrips() {
    var original = new FullMetadata {
      LocationShownGps = new GpsCoordinate(48.8584, 2.2945, 35)
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.That(parsed.LocationShownGps, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(parsed.LocationShownGps!.Value.Latitude, Is.EqualTo(48.8584).Within(1e-3));
      Assert.That(parsed.LocationShownGps!.Value.Longitude, Is.EqualTo(2.2945).Within(1e-3));
    });
  }
}
