using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PhotoManager.Core.Detection;
using PhotoManager.Core.Library;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Previews;
using PhotoManager.UI.Models;
using PhotoManager.UI.Services;

namespace PhotoManager.UI.Views;

/// <summary>
/// Lightweight memories browser. "On this day" surfaces photos taken on the
/// same calendar day in past years; "On this trip" surfaces photos within
/// 5 km / ±3 days of the currently-selected anchor photo's GPS + capture
/// time. Double-clicking a tile opens it in the OS default viewer.
///
/// All filtering is delegated to <see cref="MemoriesFilter"/> so the UI
/// stays a thin shell over pure-logic predicates.
/// </summary>
public partial class MemoriesWindow : Window {
  private readonly ObservableCollection<MemoryRowViewModel> _onThisDay = new();
  private readonly ObservableCollection<MemoryRowViewModel> _onThisTrip = new();
  private CancellationTokenSource? _thumbCts;

  public MemoriesWindow() : this(Array.Empty<(FileInfo, FullMetadata)>(), null) { }

  public MemoriesWindow(IReadOnlyList<(FileInfo File, FullMetadata Metadata)> photos, (FileInfo File, FullMetadata Metadata)? anchor) {
    this.InitializeComponent();

    if (this.FindControl<ItemsControl>("OnThisDayList") is { } day)
      day.ItemsSource = this._onThisDay;
    if (this.FindControl<ItemsControl>("OnThisTripList") is { } trip)
      trip.ItemsSource = this._onThisTrip;

    this.PopulateOnThisDay(photos);
    this.PopulateOnThisTrip(photos, anchor);

    this.Opened += (_, _) => {
      this._thumbCts = new CancellationTokenSource();
      _ = this.LoadThumbnailsAsync(this._thumbCts.Token);
    };
    this.Closed += (_, _) => this._thumbCts?.Cancel();
  }

  private void PopulateOnThisDay(IReadOnlyList<(FileInfo File, FullMetadata Metadata)> photos) {
    var today = DateTime.Today;
    var matches = MemoriesFilter.OnThisDay(photos, today)
      .OrderByDescending(p => p.Metadata.DateCreated ?? DateTime.MinValue)
      .ToList();
    foreach (var (file, md) in matches)
      this._onThisDay.Add(new MemoryRowViewModel(file, md));

    if (this.FindControl<TextBlock>("OnThisDayHeader") is { } header) {
      header.Text = matches.Count == 0
        ? $"No photos found from {today:MMM d} in past years."
        : $"{matches.Count} photo(s) taken on {today:MMM d} in past years.";
    }
  }

  private void PopulateOnThisTrip(IReadOnlyList<(FileInfo File, FullMetadata Metadata)> photos, (FileInfo File, FullMetadata Metadata)? anchor) {
    var header = this.FindControl<TextBlock>("OnThisTripHeader");
    if (anchor is not { } a) {
      if (header is not null)
        header.Text = "Select a photo with GPS in the main grid first to see nearby photos.";
      return;
    }

    if (a.Metadata.Gps is not { } gps || a.Metadata.DateCreated is not { } captured) {
      if (header is not null)
        header.Text = $"{a.File.Name} has no GPS or capture date — can't compute trip neighbours.";
      return;
    }

    var matches = MemoriesFilter.OnThisTrip(photos, gps, captured, radiusKm: 5, window: TimeSpan.FromDays(3))
      .OrderBy(p => p.Metadata.DateCreated ?? DateTime.MinValue)
      .ToList();
    foreach (var (file, md) in matches)
      this._onThisTrip.Add(new MemoryRowViewModel(file, md));

    if (header is not null) {
      header.Text = matches.Count == 0
        ? $"No nearby photos within 5 km / ±3 days of {a.File.Name}."
        : $"{matches.Count} photo(s) within 5 km of {a.File.Name} ({captured:yyyy-MM-dd}).";
    }
  }

  private async Task LoadThumbnailsAsync(CancellationToken token) {
    var fullFrame = new NormalizedBoundingBox(0, 0, 1, 1);
    foreach (var vm in this._onThisDay.Concat(this._onThisTrip).ToList()) {
      if (token.IsCancellationRequested)
        return;
      try {
        var bytes = await RegionThumbnailExtractor.CropAsync(vm.File, fullFrame, token);
        if (bytes is null)
          continue;
        await Dispatcher.UIThread.InvokeAsync(() => {
          using var ms = new MemoryStream(bytes, writable: false);
          vm.Thumbnail = new Bitmap(ms);
        });
      } catch (OperationCanceledException) {
        return;
      } catch {
        // Skip failed thumbnails — tile renders blank placeholder.
      }
    }
  }

  private void OnTileDoubleTapped(object? sender, TappedEventArgs e) {
    if (sender is not Border { DataContext: MemoryRowViewModel vm })
      return;
    try {
      ShellLauncher.OpenInDefaultViewer(vm.File.FullName);
    } catch {
      // Best-effort; failure is silent.
    }
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();
}
