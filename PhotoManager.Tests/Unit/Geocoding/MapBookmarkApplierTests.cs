using Hawkynt.PhotoManager.Core.Geocoding;

namespace Hawkynt.PhotoManager.Tests.Unit.Geocoding;

[TestFixture]
public sealed class MapBookmarkApplierTests {
  [Test]
  public void BuildEdit_AlwaysSetsGps() {
    var bookmark = new MapBookmark {
      Name = "Pin",
      Latitude = 48.8566,
      Longitude = 2.3522
    };

    var edit = MapBookmarkApplier.BuildEdit(bookmark);

    Assert.That(edit.Gps.HasValue, Is.True);
    Assert.That(edit.Gps.Value, Is.Not.Null);
    Assert.That(edit.Gps.Value!.Value.Latitude, Is.EqualTo(48.8566).Within(1e-9));
    Assert.That(edit.Gps.Value!.Value.Longitude, Is.EqualTo(2.3522).Within(1e-9));
  }

  [Test]
  public void BuildEdit_OmitsBlankPlaceFields() {
    var bookmark = new MapBookmark {
      Name = "Pin",
      Latitude = 48.8566,
      Longitude = 2.3522,
      Location = string.Empty,
      City = "   ",
      State = null,
      Country = null,
      CountryCode = null
    };

    var edit = MapBookmarkApplier.BuildEdit(bookmark);

    Assert.Multiple(() => {
      Assert.That(edit.Location.HasValue, Is.False, "blank Location should not patch");
      Assert.That(edit.City.HasValue, Is.False, "whitespace City should not patch");
      Assert.That(edit.State.HasValue, Is.False);
      Assert.That(edit.Country.HasValue, Is.False);
      Assert.That(edit.CountryCode.HasValue, Is.False);
    });
  }

  [Test]
  public void BuildEdit_PopulatesAllPresentPlaceFields() {
    var bookmark = new MapBookmark {
      Name = "Pin",
      Latitude = 48.8566,
      Longitude = 2.3522,
      Location = "Champ de Mars",
      City = "Paris",
      State = "Île-de-France",
      Country = "France",
      CountryCode = "FR"
    };

    var edit = MapBookmarkApplier.BuildEdit(bookmark);

    Assert.Multiple(() => {
      Assert.That(edit.Location.HasValue, Is.True);
      Assert.That(edit.Location.Value, Is.EqualTo("Champ de Mars"));
      Assert.That(edit.City.Value, Is.EqualTo("Paris"));
      Assert.That(edit.State.Value, Is.EqualTo("Île-de-France"));
      Assert.That(edit.Country.Value, Is.EqualTo("France"));
      Assert.That(edit.CountryCode.Value, Is.EqualTo("FR"));
    });
  }

  [Test]
  public void BuildEdit_NullBookmark_Throws() {
    Assert.Throws<ArgumentNullException>(() => MapBookmarkApplier.BuildEdit(null!));
  }
}
