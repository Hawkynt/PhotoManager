using System.Text.Json;
using PhotoManager.UI.Models;

namespace PhotoManager.UI.Services;

public class SettingsService : ISettingsService {
  private readonly string _settingsPath;

  public SettingsService() {
    var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    var appFolder = Path.Combine(appDataPath, "PhotoManager");
    Directory.CreateDirectory(appFolder);
    _settingsPath = Path.Combine(appFolder, "settings.json");
  }

  public async Task<UserSettingsData> LoadAsync() {
    try {
      if (!File.Exists(_settingsPath))
        return new UserSettingsData();

      var json = await File.ReadAllTextAsync(_settingsPath);
      var settings = JsonSerializer.Deserialize<UserSettingsData>(json);
      return settings ?? new UserSettingsData();
    }
    catch {
      // Return default settings if loading fails
      return new UserSettingsData();
    }
  }

  public async Task SaveAsync(UserSettingsData settings) {
    try {
      var options = new JsonSerializerOptions {
        WriteIndented = true
      };
      var json = JsonSerializer.Serialize(settings, options);
      await File.WriteAllTextAsync(_settingsPath, json);
    }
    catch {
      // Silently fail if saving doesn't work
    }
  }
}