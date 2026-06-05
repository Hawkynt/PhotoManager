using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Hawkynt.PhotoManager.UI.Services;

namespace Hawkynt.PhotoManager.UI.Services;

public sealed class AvaloniaFolderPicker : IFolderPicker {
  public async Task<string?> PickFolderAsync(string title, string? initialPath = null) {
    var topLevel = GetTopLevel();
    if (topLevel?.StorageProvider is not { CanPickFolder: true } storage)
      return null;

    IStorageFolder? suggestedStart = null;
    if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath)) {
      try {
        suggestedStart = await storage.TryGetFolderFromPathAsync(new Uri(initialPath));
      } catch {
        suggestedStart = null;
      }
    }

    var result = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions {
      Title = title,
      AllowMultiple = false,
      SuggestedStartLocation = suggestedStart
    });

    if (result.Count == 0)
      return null;

    return result[0].TryGetLocalPath();
  }

  private static TopLevel? GetTopLevel() {
    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
      return desktop.MainWindow;

    return null;
  }
}
