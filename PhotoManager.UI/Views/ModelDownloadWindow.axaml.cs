using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PhotoManager.Core;
using PhotoManager.Core.Models;
using PhotoManager.UI.Models;
using PhotoManager.UI.Services;

namespace PhotoManager.UI.Views;

/// <summary>
/// Dialog that lists the installable detection models and lets the user
/// download them into <see cref="AppDataPaths"/>. Shows per-entry progress
/// so multi-megabyte YOLO weights don't look like they've hung.
/// </summary>
public partial class ModelDownloadWindow : Window {
  private readonly List<ModelCatalogEntry> _entries;

  public ModelDownloadWindow() {
    this.InitializeComponent();

    this._entries = ModelRegistry.All.Select(m => new ModelCatalogEntry(m)).ToList();
    if (this.FindControl<ItemsControl>("EntriesList") is { } list)
      list.ItemsSource = this._entries;
  }

  private async void OnDownloadClick(object? sender, RoutedEventArgs e) {
    if (sender is not Button { Tag: string name })
      return;

    var entry = this._entries.FirstOrDefault(en => en.Model.Name == name);
    if (entry == null)
      return;

    entry.IsDownloading = true;
    entry.DownloadFraction = 0;
    entry.StatusText = "Starting download...";

    using var downloader = new ModelDownloader();

    // Marshal progress updates back to the UI thread — the download runs on
    // a thread-pool thread so binding updates from there would fail.
    var progress = new Progress<ModelDownloadProgress>(p => {
      Dispatcher.UIThread.Post(() => {
        entry.DownloadFraction = p.Fraction ?? 0;
        entry.StatusText = p.TotalBytes is { } total
          ? $"{p.BytesReceived / 1024:0} / {total / 1024:0} KB  ({(p.Fraction ?? 0) * 100:0}%)"
          : $"{p.BytesReceived / 1024:0} KB received...";
      });
    });

    try {
      await downloader.DownloadAsync(entry.Model, progress);
      entry.StatusText = $"Installed at {entry.Model.ResolveDestination().FullName}";
      entry.DownloadFraction = 1;
    } catch (Exception ex) {
      entry.StatusText = $"Download failed: {ex.Message}";
    } finally {
      entry.IsDownloading = false;
      entry.Refresh();
    }
  }

  private async void OnInstallFromFileClick(object? sender, RoutedEventArgs e) {
    if (sender is not Button { Tag: string name })
      return;
    var entry = this._entries.FirstOrDefault(en => en.Model.Name == name);
    if (entry == null)
      return;

    var topLevel = TopLevel.GetTopLevel(this);
    if (topLevel?.StorageProvider is not { CanOpen: true } storage) {
      entry.StatusText = "Picker unavailable.";
      entry.Refresh();
      return;
    }

    var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = $"Pick {entry.Model.DisplayName} ONNX file",
      AllowMultiple = false,
      FileTypeFilter = [new FilePickerFileType("ONNX models") { Patterns = ["*.onnx"] }]
    });
    if (picked.Count == 0)
      return;
    var source = picked[0].TryGetLocalPath();
    if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) {
      entry.StatusText = "Picked file doesn't exist.";
      entry.Refresh();
      return;
    }

    try {
      var destination = entry.Model.ResolveDestination();
      destination.Directory?.Create();
      File.Copy(source, destination.FullName, overwrite: true);
      entry.StatusText = $"Installed at {destination.FullName}";
      entry.DownloadFraction = 1;
    } catch (Exception ex) {
      entry.StatusText = $"Install failed: {ex.Message}";
    } finally {
      entry.Refresh();
    }
  }

  private void OnOpenFolderClick(object? sender, RoutedEventArgs e) {
    var dir = AppDataPaths.SubDirectory("models");
    try {
      ShellLauncher.OpenInDefaultViewer(dir.FullName);
    } catch {
      // Failing to open the file browser is cosmetic — the path is visible
      // in the entry status text.
    }
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();
}
