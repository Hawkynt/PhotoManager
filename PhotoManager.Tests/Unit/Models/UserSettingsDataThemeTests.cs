using System.Text.Json;
using PhotoManager.UI.Models;

namespace PhotoManager.Tests.Unit.Models;

[TestFixture]
public class UserSettingsDataThemeTests {
  [Test]
  public void NewInstance_DefaultsToSystemTheme() {
    var settings = new UserSettingsData();
    Assert.That(settings.Theme, Is.EqualTo(ThemeVariantPreference.System));
  }

  [Test]
  public void RoundTrip_PreservesDarkPreference() {
    var original = new UserSettingsData { Theme = ThemeVariantPreference.Dark };
    var json = JsonSerializer.Serialize(original);
    var restored = JsonSerializer.Deserialize<UserSettingsData>(json);
    Assert.That(restored, Is.Not.Null);
    Assert.That(restored!.Theme, Is.EqualTo(ThemeVariantPreference.Dark));
  }

  [Test]
  public void RoundTrip_PreservesLightPreference() {
    var original = new UserSettingsData { Theme = ThemeVariantPreference.Light };
    var json = JsonSerializer.Serialize(original);
    var restored = JsonSerializer.Deserialize<UserSettingsData>(json);
    Assert.That(restored, Is.Not.Null);
    Assert.That(restored!.Theme, Is.EqualTo(ThemeVariantPreference.Light));
  }

  [Test]
  public void Deserialize_LegacyJsonWithoutThemeField_DefaultsToSystem() {
    const string legacyJson = """
      {
        "LastSourceDirectory": "C:\\Photos",
        "LastDestinationDirectory": "C:\\Sorted",
        "Recursive": true,
        "PreserveOriginals": false
      }
      """;
    var restored = JsonSerializer.Deserialize<UserSettingsData>(legacyJson);
    Assert.That(restored, Is.Not.Null);
    Assert.That(restored!.Theme, Is.EqualTo(ThemeVariantPreference.System));
    Assert.That(restored.LastSourceDirectory, Is.EqualTo("C:\\Photos"));
  }
}
