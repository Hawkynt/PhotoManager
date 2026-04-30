using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PhotoManager.Core.Geocoding;
using PhotoManager.Core.Metadata;

namespace PhotoManager.UI.Views;

/// <summary>
/// Dialog version of the geofence-apply pass: walks the caller-supplied
/// file list against the user's saved bookmarks and reports per-file
/// outcomes. Mirrors the dry-run + cancellable + progress pattern used by
/// the reverse-geocode batch flow.
/// </summary>
public partial class GeofenceBatchWindow : Window {
  private readonly IReadOnlyList<FileInfo> _files;
  private readonly IReadOnlyList<MapBookmark> _bookmarks;
  private readonly GeofenceBatchService _service = new(new MetadataReader(), new CompositeMetadataWriter());
  private readonly ObservableCollection<string> _resultLines = new();
  private CancellationTokenSource? _cts;

  public GeofenceBatchWindow() : this(Array.Empty<FileInfo>(), Array.Empty<MapBookmark>()) { }

  public GeofenceBatchWindow(IReadOnlyList<FileInfo> files, IReadOnlyList<MapBookmark> bookmarks) {
    this._files = files;
    this._bookmarks = bookmarks;
    this.InitializeComponent();

    if (this.FindControl<TextBlock>("FileCountText") is { } fc)
      fc.Text = $"{files.Count} file(s) selected";
    if (this.FindControl<TextBlock>("BookmarkCountText") is { } bc)
      bc.Text = $"{bookmarks.Count} bookmark(s) loaded";
    if (this.FindControl<ItemsControl>("ResultsList") is { } list)
      list.ItemsSource = this._resultLines;

    if (bookmarks.Count == 0)
      this.SetStatus("No bookmarks saved — define some on the world map first.");
    else if (files.Count == 0)
      this.SetStatus("No files selected.");
    else
      this.SetStatus("Ready. Click Resolve to start.");
  }

  private async void OnResolveClick(object? sender, RoutedEventArgs e) {
    if (this._files.Count == 0 || this._bookmarks.Count == 0)
      return;

    var dryRun = this.FindControl<CheckBox>("DryRunBox")?.IsChecked ?? true;
    var onlyFillEmpty = this.FindControl<CheckBox>("OnlyFillEmptyBox")?.IsChecked ?? true;

    this._cts = new CancellationTokenSource();
    if (this.FindControl<Button>("ResolveButton") is { } resolveBtn)
      resolveBtn.IsEnabled = false;

    this._resultLines.Clear();
    this.SetStatus(dryRun ? "Resolving (dry run)..." : "Resolving and writing...");

    var progress = new Progress<GeofenceBatchService.Progress>(p => {
      Dispatcher.UIThread.Post(() => {
        if (this.FindControl<ProgressBar>("Progress") is { } bar)
          bar.Value = p.Total == 0 ? 0 : 100.0 * p.Processed / p.Total;
        if (p.Latest is { } latest) {
          var status = latest.Error is { Length: > 0 } err ? $"error: {err}"
            : latest.Matches.Count == 0 ? "no match"
            : $"hit: {string.Join(", ", latest.Matches.Select(m => m.Name))}{(latest.WouldWrite ? "" : " (no fields to add)")}";
          this._resultLines.Add($"[{p.Processed}/{p.Total}] {latest.File.Name} — {status}");
        }
      });
    });

    try {
      var outcomes = await this._service.RunAsync(
        this._files,
        this._bookmarks,
        new GeofenceBatchService.Options(DryRun: dryRun, OnlyFillEmpty: onlyFillEmpty),
        progress,
        this._cts.Token
      );

      var hits = outcomes.Count(o => o.Matches.Count > 0);
      var writes = outcomes.Count(o => o.WouldWrite);
      this.SetStatus(dryRun
        ? $"Done (dry run). {hits} matched, {writes} would write."
        : $"Done. {hits} matched, {writes} written.");
    } catch (OperationCanceledException) {
      this.SetStatus("Cancelled.");
    } catch (Exception ex) {
      this.SetStatus($"Failed: {ex.Message}");
    } finally {
      if (this.FindControl<Button>("ResolveButton") is { } resolveBtnDone)
        resolveBtnDone.IsEnabled = true;
      this._cts?.Dispose();
      this._cts = null;
    }
  }

  private void OnCancelClick(object? sender, RoutedEventArgs e) {
    if (this._cts is { IsCancellationRequested: false }) {
      this._cts.Cancel();
      this.SetStatus("Cancelling...");
      return;
    }
    this.Close();
  }

  private void SetStatus(string text) {
    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = text;
  }
}
