using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Hawkynt.PhotoManager.Core.Detection;
using Hawkynt.PhotoManager.Core.Library;
using Hawkynt.PhotoManager.Core.Previews;
using Hawkynt.PhotoManager.UI.Models;
using Hawkynt.PhotoManager.UI.Services;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Duplicate-detection window. Hashes a folder of images with the
/// <see cref="DuplicateFinder"/> (perceptual-hash + Hamming distance), then
/// shows each cluster of similar files as a row of side-by-side thumbnail
/// tiles. Hashes are cached in memory by mtime so re-scans of unchanged
/// files are free; nothing persists to disk.
/// </summary>
public partial class DuplicatesWindow : Window {
  private readonly DuplicateFinder _finder = new();
  private readonly ObservableCollection<DuplicateGroupViewModel> _groups = new();
  private CancellationTokenSource? _scanCts;
  private CancellationTokenSource? _thumbCts;

  public DuplicatesWindow() {
    this.InitializeComponent();
    if (this.FindControl<ItemsControl>("GroupsList") is { } list)
      list.ItemsSource = this._groups;
  }

  public DuplicatesWindow(string initialFolder) : this() {
    if (this.FindControl<TextBox>("FolderBox") is { } box)
      box.Text = initialFolder;
  }

  private async void OnBrowseClick(object? sender, RoutedEventArgs e) {
    var picker = new AvaloniaFolderPicker();
    var initial = this.FindControl<TextBox>("FolderBox")?.Text;
    var chosen = await picker.PickFolderAsync("Select folder to scan", initial);
    if (chosen != null && this.FindControl<TextBox>("FolderBox") is { } box)
      box.Text = chosen;
  }

  private async void OnScanClick(object? sender, RoutedEventArgs e) {
    var folder = this.FindControl<TextBox>("FolderBox")?.Text;
    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) {
      this.SetStatus("Pick a folder first.");
      return;
    }

    this._scanCts?.Cancel();
    this._scanCts = new CancellationTokenSource();
    var token = this._scanCts.Token;

    this.SetStatus("Hashing...");

    var recursive = this.FindControl<CheckBox>("RecursiveBox")?.IsChecked ?? true;
    var progress = new Progress<FileInfo>(f =>
      Dispatcher.UIThread.Post(() => this.SetStatus($"Hashing {f.Name}..."))
    );

    int hashed;
    try {
      hashed = await this._finder.ScanAsync(new DirectoryInfo(folder), recursive, progress, token);
    } catch (OperationCanceledException) {
      this.SetStatus("Scan cancelled.");
      return;
    } catch (Exception ex) {
      this.SetStatus($"Scan failed: {ex.Message}");
      return;
    }

    this.SetStatus($"Hashed {hashed} file(s). Clustering...");
    this.RebuildGroups();
  }

  private void OnReclusterClick(object? sender, RoutedEventArgs e) => this.RebuildGroups();

  private void RebuildGroups() {
    var threshold = this.GetThreshold();

    IReadOnlyList<DuplicateGroup> result;
    try {
      result = this._finder.FindGroups(threshold);
    } catch (Exception ex) {
      this.SetStatus($"Clustering failed: {ex.Message}");
      return;
    }

    this._groups.Clear();
    var idx = 0;
    foreach (var group in result)
      this._groups.Add(new DuplicateGroupViewModel(group, idx++));

    var totalDup = result.Sum(g => g.Count);
    this.SetStatus(result.Count == 0
      ? $"No duplicate groups at threshold {threshold} bits."
      : $"{result.Count} group(s), {totalDup} file(s) within {threshold} bits.");

    this._thumbCts?.Cancel();
    this._thumbCts = new CancellationTokenSource();
    _ = this.PopulateThumbnailsAsync(this._thumbCts.Token);
  }

  private int GetThreshold() {
    if (this.FindControl<NumericUpDown>("ThresholdBox") is { Value: { } v })
      return Math.Max(0, (int)v);
    return DuplicateFinder.DefaultThreshold;
  }

  private async Task PopulateThumbnailsAsync(CancellationToken token) {
    var fullFrame = new NormalizedBoundingBox(0, 0, 1, 1);
    foreach (var group in this._groups.ToList()) {
      foreach (var member in group.Members.ToList()) {
        if (token.IsCancellationRequested)
          return;
        try {
          var bytes = await RegionThumbnailExtractor.CropAsync(member.HashedFile.File, fullFrame, token);
          if (bytes == null)
            continue;
          var bitmap = new Bitmap(new MemoryStream(bytes, writable: false));
          await Dispatcher.UIThread.InvokeAsync(() => member.Thumbnail = bitmap);
        } catch (OperationCanceledException) {
          return;
        } catch {
          // An unreadable file just shows as a blank tile — the rest of the cluster still works.
        }
      }
    }
  }

  private void OnMemberDoubleTapped(object? sender, TappedEventArgs e) {
    if (sender is not Border { DataContext: DuplicateMemberViewModel vm })
      return;
    try {
      ShellLauncher.OpenInDefaultViewer(vm.FullPath);
    } catch {
      // Convenience action — no need to surface shell errors.
    }
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = message;
  }
}
