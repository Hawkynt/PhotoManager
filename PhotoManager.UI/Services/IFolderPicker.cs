namespace Hawkynt.PhotoManager.UI.Services;

/// <summary>
/// UI-framework-agnostic folder picker. Implemented by each front-end
/// (WinForms uses FolderBrowserDialog; Avalonia uses IStorageProvider).
/// </summary>
public interface IFolderPicker {
  Task<string?> PickFolderAsync(string title, string? initialPath = null);
}
