using System.Text.Json;
using PhotoManager.UI.Models;

namespace PhotoManager.Tests.Unit.Models;

/// <summary>
/// Defaults + backwards-compat coverage for the D2 settings additions
/// (rename template, recent-folder depth, auto-detect toggles, geocoder
/// flags, rate limit). Anything that lands on disk in the JSON without
/// these fields must keep loading and pick up sensible defaults.
/// </summary>
[TestFixture]
public class UserSettingsDataExtraTests {
  [Test]
  public void NewInstance_HasSensibleDefaults() {
    var s = new UserSettingsData();
    Assert.Multiple(() => {
      Assert.That(s.DefaultRenameTemplate, Is.EqualTo("{date:yyyy-MM-dd}_{name}"));
      Assert.That(s.RecentFoldersDepth, Is.EqualTo(5));
      Assert.That(s.AutoDetectFacesOnScan, Is.False);
      Assert.That(s.AutoDetectObjectsOnScan, Is.False);
      Assert.That(s.ReverseGeocoderEnabled, Is.True);
      Assert.That(s.ElevationLookupEnabled, Is.True);
      Assert.That(s.GeocoderRateLimitPerSecond, Is.EqualTo(1));
      Assert.That(s.RecentFolders, Is.Not.Null);
    });
  }

  [Test]
  public void Deserialize_LegacyJsonWithoutNewFields_PicksUpDefaults() {
    const string legacyJson = """
      {
        "LastSourceDirectory": "C:\\Photos",
        "LastDestinationDirectory": "C:\\Sorted",
        "Recursive": true,
        "PreserveOriginals": false,
        "Theme": 1
      }
      """;

    var s = JsonSerializer.Deserialize<UserSettingsData>(legacyJson);

    Assert.That(s, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(s!.LastSourceDirectory, Is.EqualTo(@"C:\Photos"));
      Assert.That(s.Theme, Is.EqualTo(ThemeVariantPreference.Light));
      Assert.That(s.DefaultRenameTemplate, Is.EqualTo("{date:yyyy-MM-dd}_{name}"));
      Assert.That(s.RecentFoldersDepth, Is.EqualTo(5));
      Assert.That(s.AutoDetectFacesOnScan, Is.False);
      Assert.That(s.AutoDetectObjectsOnScan, Is.False);
      Assert.That(s.ReverseGeocoderEnabled, Is.True);
      Assert.That(s.ElevationLookupEnabled, Is.True);
      Assert.That(s.GeocoderRateLimitPerSecond, Is.EqualTo(1));
      Assert.That(s.RecentFolders, Is.Not.Null);
      Assert.That(s.RecentFolders.SourceFolders, Is.Empty);
      Assert.That(s.RecentFolders.OutputFolders, Is.Empty);
    });
  }

  [Test]
  public void RoundTrip_PreservesAllNewFields() {
    var original = new UserSettingsData {
      DefaultRenameTemplate = "{year}_{name}",
      RecentFoldersDepth = 12,
      AutoDetectFacesOnScan = true,
      AutoDetectObjectsOnScan = true,
      ReverseGeocoderEnabled = false,
      ElevationLookupEnabled = false,
      GeocoderRateLimitPerSecond = 5
    };

    var json = JsonSerializer.Serialize(original);
    var restored = JsonSerializer.Deserialize<UserSettingsData>(json);

    Assert.That(restored, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(restored!.DefaultRenameTemplate, Is.EqualTo("{year}_{name}"));
      Assert.That(restored.RecentFoldersDepth, Is.EqualTo(12));
      Assert.That(restored.AutoDetectFacesOnScan, Is.True);
      Assert.That(restored.AutoDetectObjectsOnScan, Is.True);
      Assert.That(restored.ReverseGeocoderEnabled, Is.False);
      Assert.That(restored.ElevationLookupEnabled, Is.False);
      Assert.That(restored.GeocoderRateLimitPerSecond, Is.EqualTo(5));
    });
  }

  [Test]
  public void Deserialize_EmptyJsonObject_AllDefaults() {
    var s = JsonSerializer.Deserialize<UserSettingsData>("{}");
    var expected = new UserSettingsData();
    Assert.That(s, Is.Not.Null);
    Assert.Multiple(() => {
      // Compare field-by-field — record equality on UserSettingsData would
      // use reference equality on the collection-typed fields, which is
      // never true across two fresh instances.
      Assert.That(s!.LastSourceDirectory, Is.EqualTo(expected.LastSourceDirectory));
      Assert.That(s.LastDestinationDirectory, Is.EqualTo(expected.LastDestinationDirectory));
      Assert.That(s.DuplicateHandling, Is.EqualTo(expected.DuplicateHandling));
      Assert.That(s.Recursive, Is.EqualTo(expected.Recursive));
      Assert.That(s.PreserveOriginals, Is.EqualTo(expected.PreserveOriginals));
      Assert.That(s.Theme, Is.EqualTo(expected.Theme));
      Assert.That(s.DefaultRenameTemplate, Is.EqualTo(expected.DefaultRenameTemplate));
      Assert.That(s.RecentFoldersDepth, Is.EqualTo(expected.RecentFoldersDepth));
      Assert.That(s.AutoDetectFacesOnScan, Is.EqualTo(expected.AutoDetectFacesOnScan));
      Assert.That(s.AutoDetectObjectsOnScan, Is.EqualTo(expected.AutoDetectObjectsOnScan));
      Assert.That(s.ReverseGeocoderEnabled, Is.EqualTo(expected.ReverseGeocoderEnabled));
      Assert.That(s.ElevationLookupEnabled, Is.EqualTo(expected.ElevationLookupEnabled));
      Assert.That(s.GeocoderRateLimitPerSecond, Is.EqualTo(expected.GeocoderRateLimitPerSecond));
      Assert.That(s.TreeViewPaths, Is.Empty);
      Assert.That(s.SavedSearches, Is.Empty);
      Assert.That(s.RecentFolders.SourceFolders, Is.Empty);
      Assert.That(s.RecentFolders.OutputFolders, Is.Empty);
    });
  }
}
