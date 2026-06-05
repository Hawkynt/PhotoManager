using Hawkynt.PhotoManager.UI.Models;

namespace Hawkynt.PhotoManager.UI.Services;

public interface ISettingsService {
  Task<UserSettingsData> LoadAsync();
  Task SaveAsync(UserSettingsData settings);
}
