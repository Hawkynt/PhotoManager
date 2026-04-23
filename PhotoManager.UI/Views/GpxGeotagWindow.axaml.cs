using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PhotoManager.Core.Gpx;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Services;
using PhotoManager.UI.Services;

namespace PhotoManager.UI.Views;

/// <summary>
/// Dialog for applying a GPX track's positions to a folder of photos by
/// timestamp matching, à la GeoSetter.
/// </summary>
public partial class GpxGeotagWindow : Window {
  private readonly SupportedFormatsService _formats = new();
  private readonly IMetadataWriter _writer = new CompositeMetadataWriter();

  public GpxGeotagWindow() {
    this.InitializeComponent();
  }

  public GpxGeotagWindow(string initialFolder) : this() {
    if (this.FindControl<TextBox>("FolderBox") is { } box)
      box.Text = initialFolder;
  }

  private async void OnBrowseGpxClick(object? sender, RoutedEventArgs e) {
    var topLevel = TopLevel.GetTopLevel(this);
    if (topLevel?.StorageProvider is not { CanOpen: true } storage)
      return;
    var file = await storage.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Select GPX file",
      AllowMultiple = false,
      FileTypeFilter = [new FilePickerFileType("GPX tracks") { Patterns = ["*.gpx"] }]
    });
    if (file.Count == 0)
      return;
    if (this.FindControl<TextBox>("GpxPathBox") is { } box)
      box.Text = file[0].TryGetLocalPath();
  }

  private async void OnBrowseFolderClick(object? sender, RoutedEventArgs e) {
    var picker = new AvaloniaFolderPicker();
    var initial = this.FindControl<TextBox>("FolderBox")?.Text;
    var chosen = await picker.PickFolderAsync("Select photos folder", initial);
    if (chosen != null && this.FindControl<TextBox>("FolderBox") is { } box)
      box.Text = chosen;
  }

  private async void OnGeotagClick(object? sender, RoutedEventArgs e) {
    var gpxPath = this.FindControl<TextBox>("GpxPathBox")?.Text;
    var folder = this.FindControl<TextBox>("FolderBox")?.Text;
    if (string.IsNullOrWhiteSpace(gpxPath) || !File.Exists(gpxPath)) {
      this.SetStatus("Pick a GPX file first.");
      return;
    }
    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) {
      this.SetStatus("Pick a photos folder first.");
      return;
    }

    var inv = CultureInfo.InvariantCulture;
    if (!int.TryParse(this.FindControl<TextBox>("OffsetHoursBox")?.Text, NumberStyles.Integer, inv, out var offsetHours))
      offsetHours = 0;
    if (!int.TryParse(this.FindControl<TextBox>("OffsetMinutesBox")?.Text, NumberStyles.Integer, inv, out var offsetMinutes))
      offsetMinutes = 0;
    if (!int.TryParse(this.FindControl<TextBox>("ToleranceBox")?.Text, NumberStyles.Integer, inv, out var tolerance))
      tolerance = 60;
    var overwrite = this.FindControl<CheckBox>("OverwriteBox")?.IsChecked ?? false;
    var recursive = this.FindControl<CheckBox>("RecursiveBox")?.IsChecked ?? true;
    var offset = new TimeSpan(offsetHours, offsetMinutes, 0);

    this.SetStatus("Parsing GPX...");
    GpxTrack track;
    try {
      track = await GpxParser.ParseFileAsync(new FileInfo(gpxPath));
    } catch (Exception ex) {
      this.SetStatus($"GPX parse failed: {ex.Message}");
      return;
    }

    if (track.PointCount == 0) {
      this.SetStatus("GPX file has no trackpoints with timestamps.");
      return;
    }

    this.SetStatus($"Loaded {track.PointCount} trackpoints. Collecting photos...");
    var extensions = (await this._formats.GetSupportedExtensionsWithoutWildcardsAsync())
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
    var files = new DirectoryInfo(folder).EnumerateFiles("*", option)
      .Where(f => extensions.Contains(f.Extension))
      .ToList();

    if (files.Count == 0) {
      this.SetStatus("No supported image files in folder.");
      return;
    }

    var matcher = new GpxTimelineMatcher { MaxToleranceSeconds = tolerance };
    var service = new GpxGeotaggingService(this._writer);
    var progress = new Progress<FileInfo>(f =>
      Dispatcher.UIThread.Post(() => this.SetStatus($"Geotagging {f.Name}..."))
    );

    IReadOnlyList<GpxGeotagResult> results;
    try {
      results = await service.ApplyToFilesAsync(files, track, offset, matcher, overwrite, progress);
    } catch (Exception ex) {
      this.SetStatus($"Geotagging failed: {ex.Message}");
      return;
    }

    var matched = results.Count(r => r.Success);
    var skipped = results.Count - matched;
    this.SetStatus($"Geotagged {matched} of {results.Count} photo(s); {skipped} skipped.");

    this.PopulateResults(results);
  }

  private void PopulateResults(IReadOnlyList<GpxGeotagResult> results) {
    if (this.FindControl<ItemsControl>("ResultsList") is not { } list)
      return;
    var inv = CultureInfo.InvariantCulture;
    var rows = results.Select(r => {
      var status = r.Success
        ? string.Create(inv, $"OK   {r.Matched!.Value.Latitude:0.#####}, {r.Matched.Value.Longitude:0.#####}")
        : $"SKIP {r.Reason}";
      return $"{r.File.Name,-40} {status}";
    }).ToList();
    list.ItemsSource = rows;
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = message;
  }
}
