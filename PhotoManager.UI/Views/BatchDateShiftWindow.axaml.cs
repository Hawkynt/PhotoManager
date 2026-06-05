using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Hawkynt.PhotoManager.Core.Library;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Apply a uniform <see cref="TimeSpan"/> offset to a selection of files'
/// EXIF capture timestamps. Plan is computed live as the user adjusts the
/// day/hour/minute/second spinners; Apply writes via
/// <see cref="PhotoDateShifter.ApplyAsync"/>.
/// </summary>
public partial class BatchDateShiftWindow : Window {
  private readonly IReadOnlyList<DateShiftRow> _rows;
  private readonly Dictionary<DateShiftRow, FileInfo> _filesByRow;

  public BatchDateShiftWindow() : this(Array.Empty<FileInfo>()) { }

  public BatchDateShiftWindow(IReadOnlyList<FileInfo> files) {
    this.InitializeComponent();

    var initialPlans = PhotoDateShifter.Plan(files, TimeSpan.Zero);
    var rows = new List<DateShiftRow>(initialPlans.Count);
    var lookup = new Dictionary<DateShiftRow, FileInfo>();
    foreach (var plan in initialPlans) {
      var row = new DateShiftRow {
        File = plan.File,
        FileName = plan.File.Name,
        Current = plan.Current?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "(no EXIF date)",
        Shifted = plan.Current?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "(no EXIF date)",
        Status = plan.Current is null ? "no date" : "ready"
      };
      rows.Add(row);
      lookup[row] = plan.File;
    }
    this._rows = rows;
    this._filesByRow = lookup;

    if (this.FindControl<DataGrid>("PreviewGrid") is { } grid)
      grid.ItemsSource = new ObservableCollection<DateShiftRow>(this._rows);
    this.RefreshOffsetSummary();
  }

  private TimeSpan GetCurrentOffset() {
    var d = (int)(this.FindControl<NumericUpDown>("DaysBox")?.Value    ?? 0m);
    var h = (int)(this.FindControl<NumericUpDown>("HoursBox")?.Value   ?? 0m);
    var m = (int)(this.FindControl<NumericUpDown>("MinutesBox")?.Value ?? 0m);
    var s = (int)(this.FindControl<NumericUpDown>("SecondsBox")?.Value ?? 0m);
    return new TimeSpan(d, h, m, s);
  }

  private void OnOffsetChanged(object? sender, NumericUpDownValueChangedEventArgs e) {
    this.RefreshOffsetSummary();
  }

  private void RefreshOffsetSummary() {
    var offset = this.GetCurrentOffset();
    if (this.FindControl<TextBlock>("OffsetSummary") is { } summary) {
      var sign = offset.Ticks < 0 ? "-" : "+";
      var abs = offset.Duration();
      summary.Text = $"Total: {sign}{(int)abs.TotalDays}d {abs.Hours:D2}:{abs.Minutes:D2}:{abs.Seconds:D2}";
    }
    foreach (var row in this._rows) {
      var current = string.IsNullOrEmpty(row.Current) || row.Current.StartsWith("(") ? null : (DateTime?)null;
      // Re-derive original from File via plan to avoid string round-trip parsing.
    }
    // Re-plan against File list with the new offset.
    var plans = PhotoDateShifter.Plan(this._rows.Select(r => r.File), offset);
    var i = 0;
    foreach (var plan in plans) {
      var row = this._rows[i++];
      row.Current = plan.Current?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "(no EXIF date)";
      row.Shifted = plan.Shifted?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "(no EXIF date)";
      row.Status = plan.Current is null ? "no date" : (plan.HasChange ? "ready" : "no change");
    }
    this.RebindGrid();
  }

  private void RebindGrid() {
    if (this.FindControl<DataGrid>("PreviewGrid") is { } grid) {
      grid.ItemsSource = null;
      grid.ItemsSource = new ObservableCollection<DateShiftRow>(this._rows);
    }
  }

  private async void OnApplyClick(object? sender, RoutedEventArgs e) {
    var offset = this.GetCurrentOffset();
    if (offset == TimeSpan.Zero) {
      if (this.FindControl<TextBlock>("StatusText") is { } empty)
        empty.Text = "Offset is zero — nothing to do.";
      return;
    }

    var ok = 0;
    var skipped = 0;
    var failed = 0;
    foreach (var row in this._rows) {
      if (row.Status == "no date" || row.Status == "no change") {
        skipped++;
        continue;
      }
      var success = await PhotoDateShifter.ApplyAsync(row.File, offset);
      if (success) { row.Status = "shifted"; ok++; } else { row.Status = "failed"; failed++; }
    }

    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = $"Shifted {ok}, skipped {skipped}, failed {failed}.";
    this.RebindGrid();
  }

  private void OnCancelClick(object? sender, RoutedEventArgs e) => this.Close();
}
