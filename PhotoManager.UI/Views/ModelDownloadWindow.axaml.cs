using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hawkynt.PhotoManager.Core;
using Hawkynt.PhotoManager.Core.Models;
using Hawkynt.PhotoManager.UI.Models;
using Hawkynt.PhotoManager.UI.Services;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Dialog that lists the installable detection models and lets the user
/// download them into <see cref="AppDataPaths"/>. Shows per-entry progress
/// so multi-megabyte YOLO weights don't look like they've hung. Each row
/// has a tinted green background when the model is already on disk; the
/// download button reads "Re-download" in that case so the user can refresh
/// without confusion.
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

    await this.DownloadEntryAsync(entry);
  }

  /// <summary>
  /// Sequentially download every model that isn't already installed.
  /// Skips entries whose <see cref="ModelInfo.IsInstalled"/> already
  /// reports true so the user doesn't pay the bandwidth twice.
  /// </summary>
  private async void OnDownloadAllClick(object? sender, RoutedEventArgs e) {
    var missing = this._entries.Where(en => !en.IsInstalled && !en.IsDownloading).ToList();
    if (missing.Count == 0) {
      // Everything is already on disk — surface that on the first row's
      // status so the user gets feedback even though nothing happens.
      if (this._entries.FirstOrDefault() is { } first)
        first.StatusText = "Every registered model is already installed — nothing to download.";
      return;
    }

    if (this.FindControl<Button>("DownloadAllButton") is { } button)
      button.IsEnabled = false;
    try {
      foreach (var entry in missing)
        await this.DownloadEntryAsync(entry);
    } finally {
      if (this.FindControl<Button>("DownloadAllButton") is { } button2)
        button2.IsEnabled = true;
    }
  }

  private async Task DownloadEntryAsync(ModelCatalogEntry entry) {
    entry.IsDownloading = true;
    entry.DownloadFraction = 0;
    entry.StatusText = "Starting download…";
    entry.ProgressDetail = string.Empty;

    using var downloader = new ModelDownloader();

    // Stopwatch + last-update throttling so the per-row label stays
    // readable on fast connections (otherwise the ETA flickers per packet).
    var stopwatch = Stopwatch.StartNew();
    var lastUiUpdate = TimeSpan.Zero;

    var progress = new Progress<ModelDownloadProgress>(p => {
      var elapsed = stopwatch.Elapsed;
      // Throttle to ~10 Hz so the label is stable + cheap.
      if (elapsed - lastUiUpdate < TimeSpan.FromMilliseconds(100) && p.Fraction is not (>= 0.999))
        return;
      lastUiUpdate = elapsed;

      Dispatcher.UIThread.Post(() => {
        entry.DownloadFraction = p.Fraction ?? 0;
        entry.StatusText = FormatTransferred(p);
        entry.ProgressDetail = FormatProgressDetail(p, elapsed);
      });
    });

    try {
      await downloader.DownloadAsync(entry.Model, progress);
      stopwatch.Stop();
      entry.StatusText = $"Installed at {entry.Model.ResolveDestination().FullName}";
      entry.ProgressDetail = $"100% · finished in {FormatDuration(stopwatch.Elapsed)}";
      entry.DownloadFraction = 1;
    } catch (Exception ex) {
      stopwatch.Stop();
      entry.StatusText = $"Download failed: {ex.Message}";
      entry.ProgressDetail = $"failed after {FormatDuration(stopwatch.Elapsed)}";
    } finally {
      entry.IsDownloading = false;
      entry.Refresh();
    }
  }

  /// <summary>
  /// "12.3 / 67.0 MB (18%)" when total is known, "12.3 MB received" otherwise.
  /// </summary>
  private static string FormatTransferred(ModelDownloadProgress p) {
    if (p.TotalBytes is { } total)
      return $"{FormatMb(p.BytesReceived)} / {FormatMb(total)} ({(p.Fraction ?? 0) * 100:0}%)";
    return $"{FormatMb(p.BytesReceived)} received…";
  }

  /// <summary>
  /// Right-side detail line under the bar — keeps the eye on percent /
  /// elapsed / ETA / throughput in a fixed-width layout. ETA only appears
  /// after we have a known total and at least one second of history (so
  /// the divisor isn't noisy at the very start of the transfer).
  /// </summary>
  private static string FormatProgressDetail(ModelDownloadProgress p, TimeSpan elapsed) {
    var pct = p.Fraction is { } f ? $"{f * 100:0.0}%" : "—";
    var rate = elapsed.TotalSeconds > 0.5 ? p.BytesReceived / elapsed.TotalSeconds : 0;
    var rateText = rate > 0 ? $"{FormatMb((long)rate)}/s" : "—";

    string eta = "—";
    if (p.TotalBytes is { } total && rate > 0 && elapsed.TotalSeconds > 1.0) {
      var remainingBytes = Math.Max(0, total - p.BytesReceived);
      var remaining = TimeSpan.FromSeconds(remainingBytes / rate);
      eta = FormatDuration(remaining);
    }

    return $"{pct}  ·  elapsed {FormatDuration(elapsed)}  ·  ETA {eta}  ·  {rateText}";
  }

  private static string FormatMb(long bytes) {
    if (bytes < 1024) return $"{bytes} B";
    var kb = bytes / 1024.0;
    if (kb < 1024) return $"{kb:0} KB";
    return $"{kb / 1024:0.0} MB";
  }

  /// <summary>Compact mm:ss / h:mm:ss; keeps the row short on multi-minute downloads.</summary>
  private static string FormatDuration(TimeSpan t) {
    if (t.TotalHours >= 1)
      return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    return $"{t.Minutes:00}:{t.Seconds:00}";
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
