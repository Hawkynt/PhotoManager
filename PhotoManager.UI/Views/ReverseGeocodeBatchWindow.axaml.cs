using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PhotoManager.Core.Geocoding;
using PhotoManager.Core.Metadata;

namespace PhotoManager.UI.Views;

/// <summary>
/// Dialog wrapping <see cref="ReverseGeocodeBatchService"/>: shows the list
/// of selected files, runs the geocode loop on a background task, posts
/// progress to the UI thread, and lets the user cancel mid-run.
/// </summary>
public partial class ReverseGeocodeBatchWindow : Window {
  private readonly IReadOnlyList<FileInfo> _files;
  private readonly Func<FileInfo, Task>? _afterApply;
  private readonly IReverseGeocoder _geocoder;
  private readonly IMetadataReader _reader;
  private readonly IMetadataWriter _writer;
  private readonly bool _ownsGeocoder;

  private CancellationTokenSource? _runCts;

  public ReverseGeocodeBatchWindow() : this(Array.Empty<FileInfo>(), null) { }

  public ReverseGeocodeBatchWindow(
    IReadOnlyList<FileInfo> files,
    Func<FileInfo, Task>? afterApply,
    IReverseGeocoder? geocoder = null,
    IMetadataReader? reader = null,
    IMetadataWriter? writer = null
  ) {
    this.InitializeComponent();
    this._files = files ?? Array.Empty<FileInfo>();
    this._afterApply = afterApply;
    this._ownsGeocoder = geocoder is null;
    this._geocoder = geocoder ?? new NominatimReverseGeocoder();
    this._reader = reader ?? new MetadataReader();
    this._writer = writer ?? new CompositeMetadataWriter();

    if (this.FindControl<TextBlock>("FileCountText") is { } fc)
      fc.Text = $"{this._files.Count} file(s) selected.";

    this.Closed += this.OnWindowClosed;
  }

  private async void OnRunClick(object? sender, RoutedEventArgs e) {
    if (this._files.Count == 0) {
      this.SetStatus("No files selected.");
      return;
    }
    if (this._runCts is not null)
      return;

    var onlyEmpty = this.FindControl<CheckBox>("OnlyEmptyBox")?.IsChecked ?? true;
    var options = new ReverseGeocodeBatchOptions { OnlyFillEmptyFields = onlyEmpty };

    this.SetRunningUi(running: true);
    this.SetStatus("Resolving addresses...");
    this.PopulateResults(Array.Empty<ReverseGeocodeBatchEntry>());

    var service = new ReverseGeocodeBatchService(this._geocoder, this._reader, this._writer);
    this._runCts = new CancellationTokenSource();

    IReadOnlyList<ReverseGeocodeBatchEntry> results;
    try {
      var progress = new Progress<FileInfo>(file =>
        Dispatcher.UIThread.Post(() => this.SetProgress($"Resolving {file.Name}..."))
      );

      results = await Task.Run(() => service.RunAsync(this._files, options, progress, this._runCts.Token));
    } catch (OperationCanceledException) {
      this.SetStatus("Cancelled.");
      this.SetRunningUi(running: false);
      return;
    } catch (Exception ex) {
      this.SetStatus($"Failed: {ex.Message}");
      this.SetRunningUi(running: false);
      return;
    } finally {
      this._runCts?.Dispose();
      this._runCts = null;
    }

    this.PopulateResults(results);

    var resolved = results.Count(r => r.Outcome == ReverseGeocodeBatchOutcome.Resolved);
    var skipped = results.Count - resolved;
    this.SetStatus($"Resolved {resolved} of {results.Count}; {skipped} skipped/failed.");

    if (this._afterApply is not null) {
      foreach (var r in results) {
        if (r.Outcome != ReverseGeocodeBatchOutcome.Resolved)
          continue;
        try {
          await this._afterApply(r.File);
        } catch {
          // Best-effort UI refresh — the write already succeeded.
        }
      }
    }

    this.SetRunningUi(running: false);
  }

  private void OnCancelRunClick(object? sender, RoutedEventArgs e) {
    this._runCts?.Cancel();
    this.SetStatus("Cancelling...");
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private void OnWindowClosed(object? sender, EventArgs e) {
    this._runCts?.Cancel();
    if (this._ownsGeocoder && this._geocoder is IDisposable disposable)
      disposable.Dispose();
  }

  private void SetRunningUi(bool running) {
    if (this.FindControl<Button>("RunButton") is { } run) run.IsEnabled = !running;
    if (this.FindControl<Button>("CancelButton") is { } cancel) cancel.IsEnabled = running;
    if (this.FindControl<CheckBox>("OnlyEmptyBox") is { } only) only.IsEnabled = !running;
  }

  private void PopulateResults(IReadOnlyList<ReverseGeocodeBatchEntry> results) {
    if (this.FindControl<ItemsControl>("ResultsList") is not { } list)
      return;

    var rows = results.Select(r => {
      var status = r.Outcome switch {
        ReverseGeocodeBatchOutcome.Resolved      => DescribeResolved(r.Result),
        ReverseGeocodeBatchOutcome.AlreadyTagged => "SKIP already tagged",
        ReverseGeocodeBatchOutcome.NoGps         => "SKIP no GPS",
        ReverseGeocodeBatchOutcome.NoMatch       => "SKIP no match",
        ReverseGeocodeBatchOutcome.Error         => $"FAIL {r.ErrorMessage}",
        _                                        => r.Outcome.ToString()
      };
      return $"{r.File.Name,-40} {status}";
    }).ToList();

    list.ItemsSource = rows;
  }

  private static string DescribeResolved(GeocodingResult? result) {
    if (result is null)
      return "OK";

    var parts = new[] { result.Location, result.City, result.State, result.Country }
      .Where(p => !string.IsNullOrEmpty(p));
    return $"OK   {string.Join(", ", parts)}";
  }

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = message;
  }

  private void SetProgress(string message) {
    if (this.FindControl<TextBlock>("ProgressText") is { } s)
      s.Text = message;
  }
}
