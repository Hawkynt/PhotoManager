using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Hawkynt.PhotoManager.Core.Detection;
using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.Core.Previews;
using Hawkynt.PhotoManager.UI.Models;
using Hawkynt.PhotoManager.UI.Services;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Picasa-style month grid: each cell is a day, populated with the count
/// and first-photo thumbnail of every photo whose capture date falls on
/// it. Clicking a cell drills into the day's photos in the right pane;
/// double-clicking a photo hands it to the OS default viewer.
///
/// The source is the main window's already-scanned <c>_currentFileItems</c>
/// — no DB, the calendar is rebuilt per open. Capture dates are read from
/// each item's metadata (DateCreated) when present, otherwise we fall back
/// to the file's LastWriteTime so the calendar still displays.
/// </summary>
public partial class CalendarWindow : Window {
  private readonly IMetadataReader _metadataReader = new MetadataReader();
  private readonly IReadOnlyList<FileItemModel> _items;
  private readonly Dictionary<DateTime, List<FileItemModel>> _byDay = new();
  private readonly ObservableCollection<CalendarDayViewModel> _days = new();
  private readonly ObservableCollection<FileItemModel> _selectedDayPhotos = new();
  private DateTime _viewMonth;
  private CalendarDayViewModel? _selectedDay;
  private CancellationTokenSource? _thumbnailCts;

  public CalendarWindow() : this(Array.Empty<FileItemModel>()) { }

  public CalendarWindow(IReadOnlyList<FileItemModel> items) {
    this.InitializeComponent();
    this._items = items;

    if (this.FindControl<ItemsControl>("DayGrid") is { } grid)
      grid.ItemsSource = this._days;
    if (this.FindControl<ListBox>("DayPhotoList") is { } list)
      list.ItemsSource = this._selectedDayPhotos;

    this.Opened += async (_, _) => await this.InitializeCalendarAsync();
  }

  private async Task InitializeCalendarAsync() {
    this.SetStatus("Loading capture dates...");
    await this.GroupItemsByDayAsync();

    var initial = this._byDay.Count > 0
      ? this._byDay.Keys.OrderBy(d => d).Last()
      : DateTime.Today;
    this._viewMonth = new DateTime(initial.Year, initial.Month, 1);
    this.RebuildDayGrid();

    var totalPhotos = this._byDay.Values.Sum(v => v.Count);
    var distinctDays = this._byDay.Count;
    this.SetStatus($"{totalPhotos} photo(s) across {distinctDays} day(s).");
  }

  private async Task GroupItemsByDayAsync() {
    this._byDay.Clear();
    foreach (var item in this._items) {
      if (item.FileInfo is not { Exists: true } file)
        continue;
      var captureDate = await this.GetCaptureDateAsync(file);
      var key = captureDate.Date;
      if (!this._byDay.TryGetValue(key, out var bucket)) {
        bucket = new List<FileItemModel>();
        this._byDay[key] = bucket;
      }
      bucket.Add(item);
    }

    foreach (var bucket in this._byDay.Values)
      bucket.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
  }

  private async Task<DateTime> GetCaptureDateAsync(FileInfo file) {
    try {
      var md = await this._metadataReader.ReadAsync(file);
      if (md.DateCreated is { } dc)
        return dc;
    } catch {
      // Fall through to filesystem mtime.
    }
    return file.LastWriteTime;
  }

  private void RebuildDayGrid() {
    this._days.Clear();
    this._selectedDayPhotos.Clear();
    this._selectedDay = null;
    if (this.FindControl<TextBlock>("DayDetailHeading") is { } heading)
      heading.Text = "(pick a day)";

    if (this.FindControl<TextBlock>("MonthHeading") is { } monthHeading)
      monthHeading.Text = this._viewMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    var firstOfMonth = this._viewMonth;
    var daysInMonth = DateTime.DaysInMonth(firstOfMonth.Year, firstOfMonth.Month);

    // Monday-first week: shift DayOfWeek so Monday=0 .. Sunday=6.
    var firstWeekdayIndex = ((int)firstOfMonth.DayOfWeek + 6) % 7;
    var gridStart = firstOfMonth.AddDays(-firstWeekdayIndex);

    for (var i = 0; i < 42; i++) {
      var day = gridStart.AddDays(i);
      var inMonth = day.Month == firstOfMonth.Month && day.Year == firstOfMonth.Year;
      var photos = this._byDay.TryGetValue(day, out var bucket) ? (IReadOnlyList<FileItemModel>)bucket : Array.Empty<FileItemModel>();
      this._days.Add(new CalendarDayViewModel(day, photos, inMonth));
    }

    this._thumbnailCts?.Cancel();
    this._thumbnailCts = new CancellationTokenSource();
    _ = this.PopulateDayThumbnailsAsync(this._thumbnailCts.Token);
  }

  private async Task PopulateDayThumbnailsAsync(CancellationToken token) {
    foreach (var day in this._days.ToList()) {
      if (token.IsCancellationRequested)
        return;
      if (!day.HasPhotos || day.Thumbnail != null)
        continue;
      var first = day.Photos[0];
      if (first.FileInfo is not { Exists: true } file)
        continue;

      byte[]? bytes;
      try {
        var box = new NormalizedBoundingBox(0f, 0f, 1f, 1f);
        bytes = await RegionThumbnailExtractor.CropAsync(file, box, token);
      } catch (OperationCanceledException) {
        return;
      } catch {
        bytes = null;
      }
      if (bytes == null)
        continue;

      try {
        var bitmap = new Bitmap(new MemoryStream(bytes, writable: false));
        Dispatcher.UIThread.Post(() => day.Thumbnail = bitmap);
      } catch {
        // Drop this thumbnail silently.
      }
    }
  }

  private void OnPrevMonthClick(object? sender, RoutedEventArgs e) {
    this._viewMonth = this._viewMonth.AddMonths(-1);
    this.RebuildDayGrid();
  }

  private void OnNextMonthClick(object? sender, RoutedEventArgs e) {
    this._viewMonth = this._viewMonth.AddMonths(1);
    this.RebuildDayGrid();
  }

  private void OnTodayClick(object? sender, RoutedEventArgs e) {
    this._viewMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    this.RebuildDayGrid();
  }

  private void OnDayCellTapped(object? sender, TappedEventArgs e) {
    if (sender is not Control { Tag: CalendarDayViewModel day })
      return;
    this._selectedDay = day;
    this._selectedDayPhotos.Clear();
    foreach (var p in day.Photos)
      this._selectedDayPhotos.Add(p);

    if (this.FindControl<TextBlock>("DayDetailHeading") is { } heading)
      heading.Text = day.HasPhotos
        ? $"{day.Date:dddd, d MMMM yyyy} — {day.CountText}"
        : $"{day.Date:dddd, d MMMM yyyy} — no photos";
  }

  private void OnPhotoDoubleTapped(object? sender, TappedEventArgs e) {
    if (sender is not ListBox list)
      return;
    if (list.SelectedItem is not FileItemModel { FileInfo: { Exists: true } file })
      return;
    try {
      ShellLauncher.OpenInDefaultViewer(file.FullName);
    } catch (Exception ex) {
      this.SetStatus($"Open failed: {ex.Message}");
    }
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = message;
  }
}
