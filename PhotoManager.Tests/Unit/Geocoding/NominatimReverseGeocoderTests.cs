using Hawkynt.PhotoManager.Core.Geocoding;
using static Hawkynt.PhotoManager.Core.Geocoding.NominatimReverseGeocoder;

namespace Hawkynt.PhotoManager.Tests.Unit.Geocoding;

[TestFixture]
public class NominatimReverseGeocoderTests {
  [Test]
  public void Map_TypicalCityResponse_PopulatesAllExpectedFields() {
    var payload = new NominatimResponse {
      DisplayName = "Eiffel Tower, 75007 Paris, France",
      Address = new NominatimAddress {
        Road = "Avenue Anatole France",
        City = "Paris",
        State = "Île-de-France",
        Country = "France",
        CountryCode = "fr"
      }
    };

    var result = Map(payload);

    Assert.Multiple(() => {
      Assert.That(result.Location, Is.EqualTo("Avenue Anatole France"));
      Assert.That(result.City, Is.EqualTo("Paris"));
      Assert.That(result.State, Is.EqualTo("Île-de-France"));
      Assert.That(result.Country, Is.EqualTo("France"));
      Assert.That(result.CountryCode, Is.EqualTo("FR"),
        "country_code should be normalized to uppercase");
    });
  }

  [Test]
  public void Map_VillageWithoutCity_FallsThroughToVillageField() {
    var payload = new NominatimResponse {
      Address = new NominatimAddress {
        Village = "Hallstatt",
        State = "Upper Austria",
        Country = "Austria",
        CountryCode = "at"
      }
    };

    var result = Map(payload);
    Assert.That(result.City, Is.EqualTo("Hallstatt"),
      "city fallback should pick the village name when no explicit city is set");
  }

  [Test]
  public void Map_HamletAndMunicipalityFallbacks_Ordered() {
    // Town > Village > Hamlet > Municipality ordering.
    var payload = new NominatimResponse {
      Address = new NominatimAddress {
        Hamlet = "Oberfrontenhausen",
        Municipality = "Frontenhausen",
        Country = "Germany"
      }
    };

    var result = Map(payload);
    Assert.That(result.City, Is.EqualTo("Oberfrontenhausen"),
      "hamlet takes precedence over municipality");
  }

  [Test]
  public void Map_SuburbOnly_PopulatesLocation() {
    var payload = new NominatimResponse {
      Address = new NominatimAddress {
        Suburb = "Kreuzberg",
        City = "Berlin",
        Country = "Germany"
      }
    };

    var result = Map(payload);
    Assert.That(result.Location, Is.EqualTo("Kreuzberg"),
      "location falls back to suburb when road/pedestrian/path/neighbourhood aren't set");
  }

  [Test]
  public void Map_EmptyAddress_ReturnsAllNullExceptDisplayName() {
    var payload = new NominatimResponse {
      DisplayName = "Middle of the Atlantic Ocean"
    };

    var result = Map(payload);

    Assert.Multiple(() => {
      Assert.That(result.Location, Is.EqualTo("Middle of the Atlantic Ocean"),
        "with no structured address, display_name is the best we can do");
      Assert.That(result.City, Is.Null);
      Assert.That(result.State, Is.Null);
      Assert.That(result.Country, Is.Null);
    });
  }

  [Test]
  public void Map_HouseNumberWithGermanCountry_AppendsNumberAfterStreet() {
    var payload = new NominatimResponse {
      Address = new NominatimAddress {
        HouseNumber = "42",
        Road = "Hauptstraße",
        City = "Berlin",
        Country = "Germany",
        CountryCode = "de"
      }
    };

    var result = Map(payload);
    Assert.That(result.Location, Is.EqualTo("Hauptstraße 42"),
      "European convention puts the house number after the street name");
  }

  [Test]
  public void Map_HouseNumberWithUsCountry_PutsNumberFirst() {
    var payload = new NominatimResponse {
      Address = new NominatimAddress {
        HouseNumber = "1600",
        Road = "Pennsylvania Avenue NW",
        City = "Washington",
        Country = "United States",
        CountryCode = "us"
      }
    };

    var result = Map(payload);
    Assert.That(result.Location, Is.EqualTo("1600 Pennsylvania Avenue NW"),
      "US/UK convention puts the number before the street");
  }

  [Test]
  public void Map_HouseNumberWithoutStreet_DropsTheNumber() {
    // A number alone is meaningless, so if there's nothing to attach it to
    // we drop it rather than surfacing "42" as a Location.
    var payload = new NominatimResponse {
      Address = new NominatimAddress {
        HouseNumber = "42",
        Suburb = null,
        City = "Berlin",
        Country = "Germany",
        CountryCode = "de"
      }
    };

    var result = Map(payload);
    Assert.That(result.Location, Is.Null);
  }

  [Test]
  public void GeocodingResult_HasAny_FalseWhenEverythingNull() {
    var empty = new GeocodingResult(null, null, null, null, null);
    Assert.That(empty.HasAny, Is.False);
  }

  [Test]
  public void GeocodingResult_HasAny_TrueWhenAnythingSet() {
    var oneField = new GeocodingResult(null, null, null, "Germany", null);
    Assert.That(oneField.HasAny, Is.True);
  }
}
