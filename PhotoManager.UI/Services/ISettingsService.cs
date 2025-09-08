using PhotoManager.UI.Models;

namespace PhotoManager.UI.Services;

public interface ISettingsService {
  Task<UserSettingsData> LoadAsync();
  Task SaveAsync(UserSettingsData settings);
}