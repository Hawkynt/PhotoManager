using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PhotoManager.Core.Detection;
using PhotoManager.Core.Library;
using PhotoManager.Core.Previews;
using PhotoManager.UI.Models;
using PhotoManager.UI.Services;

namespace PhotoManager.UI.Views;

/// <summary>
/// Library search window. Index a folder once (walks recursively, reads every
/// supported image's metadata into an in-memory cache keyed by
/// <c>LastWriteTimeUtc</c>); then search across keywords, person regions, and
/// place fields with instant re-query.
/// </summary>
public partial class SearchWindow : Window {
  private readonly MetadataCache _cache = new();
  private readonly LibraryIndex _index;
  // ObservableCollection so ItemsControl's container generator updates incrementally
  // — re-assigning ItemsSource on every search races with WrapPanel layout and can
  // throw IndexOutOfRange inside Avalonia's CompiledBinding visitor.
  private readonly ObservableCollection<SearchHitViewModel> _results = new();
  private CancellationTokenSource? _scanCts;
  private CancellationTokenSource? _thumbCts;

  public SearchWindow() {
    this.InitializeComponent();
    this._index = new LibraryIndex(this._cache);
    if (this.FindControl<ItemsControl>("ResultsList") is { } list)
      list.ItemsSource = this._results;
  }

  public SearchWindow(string initialFolder) : this() {
    if (this.FindControl<TextBox>("FolderBox") is { } box)
      box.Text = initialFolder;
  }

  private async void OnBrowseClick(object? sender, RoutedEventArgs e) {
    var picker = new AvaloniaFolderPicker();
    var initial = this.FindControl<TextBox>("FolderBox")?.Text;
    var chosen = await picker.PickFolderAsync("Select library folder", initial);
    if (chosen != null && this.FindControl<TextBox>("FolderBox") is { } box)
      box.Text = chosen;
  }

  private async void OnIndexClick(object? sender, RoutedEventArgs e) {
    await this.EnsureIndexedAsync(force: true);
  }

  /// <summary>
  /// Walks the folder once and populates <see cref="MetadataCache"/>. Returns
  /// true when the cache is non-empty afterwards. Skips the work when the
  /// cache is already populated unless <paramref name="force"/> is set.
  /// </summary>
  private async Task<bool> EnsureIndexedAsync(bool force) {
    if (!force && this._cache.Count > 0)
      return true;

    var folder = this.FindControl<TextBox>("FolderBox")?.Text;
    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) {
      this.SetStatus("Pick a folder first.");
      return false;
    }

    this._scanCts?.Cancel();
    this._scanCts = new CancellationTokenSource();
    var token = this._scanCts.Token;

    this.SetStatus("Indexing...");

    var recursive = this.FindControl<CheckBox>("RecursiveBox")?.IsChecked ?? true;
    var progress = new Progress<FileInfo>(f =>
      Dispatcher.UIThread.Post(() => this.SetStatus($"Reading {f.Name}..."))
    );

    int count;
    try {
      count = await this._index.ScanAsync(new DirectoryInfo(folder), recursive, progress, token);
    } catch (OperationCanceledException) {
      this.SetStatus("Indexing cancelled.");
      return false;
    } catch (Exception ex) {
      this.SetStatus($"Indexing failed: {ex.Message}");
      return false;
    }

    this.SetStatus($"Indexed {count} file(s). Ready to search.");
    this.PopulateAutoComplete();
    return count > 0;
  }

  private void PopulateAutoComplete() {
    if (this.FindControl<AutoCompleteBox>("PersonBox") is { } personBox)
      personBox.ItemsSource = this._index.DistinctPeople();
    if (this.FindControl<AutoCompleteBox>("LocationBox") is { } locationBox)
      locationBox.ItemsSource = this._index.DistinctLocations();
    if (this.FindControl<AutoCompleteBox>("KeywordBox") is { } keywordBox)
      keywordBox.ItemsSource = this._index.DistinctKeywords();
  }

  private async void OnSearchClick(object? sender, RoutedEventArgs e) {
    // First-time search: build the in-memory index transparently so the user
    // doesn't have to remember to click "Index" first. Re-runs are cheap because
    // the cache short-circuits unchanged files.
    if (this._cache.Count == 0 && !await this.EnsureIndexedAsync(force: false))
      return;

    try {
      this.RunSearch();
    } catch (Exception ex) {
      // Surface the full chain so the next error-report names the root cause
      // (Avalonia binding errors are often wrapped twice).
      var inner = ex;
      while (inner.InnerException != null) inner = inner.InnerException;
      this.SetStatus($"Search failed: {inner.GetType().Name}: {inner.Message}");
    }
  }

  private void RunSearch() {
    var query = new LibrarySearchQuery(
      AnyText: this.FindControl<TextBox>("AnyTextBox")?.Text,
      Keyword: this.FindControl<AutoCompleteBox>("KeywordBox")?.Text,
      Person: this.FindControl<AutoCompleteBox>("PersonBox")?.Text,
      Location: this.FindControl<AutoCompleteBox>("LocationBox")?.Text
    );

    var hits = this._index.Search(query);
    this._results.Clear();
    foreach (var hit in hits)
      this._results.Add(new SearchHitViewModel(hit));

    this.SetStatus(query.IsEmpty
      ? $"Showing all {hits.Count} indexed file(s)."
      : $"Found {hits.Count} match(es).");

    this._thumbCts?.Cancel();
    this._thumbCts = new CancellationTokenSource();
    _ = this.PopulateThumbnailsAsync(this._thumbCts.Token);
  }

  private async Task PopulateThumbnailsAsync(CancellationToken token) {
    foreach (var vm in this._results.ToList()) {
      if (token.IsCancellationRequested)
        return;
      try {
        // Full-image thumb via the normalized bounding box that spans the
        // whole frame (x=0, y=0, width=1, height=1). RegionThumbnailExtractor
        // already resizes to its max edge so giant RAW previews don't balloon memory.
        var fullFrame = new NormalizedBoundingBox(0, 0, 1, 1);
        var bytes = await RegionThumbnailExtractor.CropAsync(vm.Hit.File, fullFrame, token);
        if (bytes == null)
          continue;
        using var ms = new MemoryStream(bytes, writable: false);
        var bitmap = new Bitmap(ms);
        // Setting the property raises PropertyChanged which the Image binding
        // observes — Avalonia rejects cross-thread updates, so marshal back.
        await Dispatcher.UIThread.InvokeAsync(() => vm.Thumbnail = bitmap);
      } catch (OperationCanceledException) {
        return;
      } catch {
        // Skip — an unreadable file just shows as a blank tile.
      }
    }
  }

  private void OnResultDoubleTapped(object? sender, TappedEventArgs e) {
    if (sender is not Border { DataContext: SearchHitViewModel vm })
      return;
    try {
      ShellLauncher.OpenInDefaultViewer(vm.Hit.File.FullName);
    } catch {
      // Opening in the shell is a convenience — no need to surface errors.
    }
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = message;
  }
}
