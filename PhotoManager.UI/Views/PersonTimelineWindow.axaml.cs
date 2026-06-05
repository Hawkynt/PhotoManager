using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Hawkynt.PhotoManager.Core.Faces;
using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.Core.Services;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Displays a per-year photo-count timeline for each detected person (face
/// cluster) in the library. The left sidebar lists every named person found
/// during the face scan; selecting one renders a vertical bar chart on the
/// right showing how many photos that person appeared in each year.
///
/// Clicking a year bar raises <see cref="YearClicked"/> so callers (e.g.
/// the main window) can filter the grid to that subset if desired.
/// </summary>
public partial class PersonTimelineWindow : Window {
  private readonly LibraryFaceScanner _scanner = new(new MetadataReader(), new SupportedFormatsService());

  /// <summary>
  /// Keyed by person display name; value is the list of (date, file) pairs
  /// for that person. Populated once during the scan; the sidebar and chart
  /// both read from this dictionary.
  /// </summary>
  private readonly Dictionary<string, List<(DateTime date, FileInfo file)>> _personPhotos = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Currently displayed person name, set by <see cref="RenderTimeline"/>.</summary>
  private string _currentPersonName = string.Empty;

  /// <summary>
  /// Fires when the user clicks a year bar, carrying the person name and year.
  /// </summary>
  public event Action<string, int>? YearClicked;

  public PersonTimelineWindow() : this(Array.Empty<FileInfo>()) { }

  public PersonTimelineWindow(IReadOnlyList<FileInfo> files) {
    this.InitializeComponent();

    this.Opened += async (_, _) => {
      if (files.Count == 0) {
        this.SetStatus("No files loaded — scan your library in the main window first.");
        return;
      }
      await this.ScanAndPopulateAsync(files);
    };
  }

  private async Task ScanAndPopulateAsync(IReadOnlyList<FileInfo> files) {
    this.SetStatus("Scanning face regions...");

    IReadOnlyList<ScannedFace> faces;
    try {
      faces = await this._scanner.ScanFilesAsync(files, onlyEmbedded: false);
    } catch (Exception ex) {
      this.SetStatus($"Scan failed: {ex.Message}");
      return;
    }

    if (faces.Count == 0) {
      this.SetStatus("No face regions found in the scanned files.");
      return;
    }

    // Group faces by person name. Unnamed faces are skipped — the timeline
    // only makes sense for identified people. Each face contributes its
    // file's last-write time as the photo date (the metadata reader is not
    // invoked again; filesystem date is a reasonable approximation here since
    // the main window has already applied date-detection during the scan).
    this._personPhotos.Clear();

    foreach (var face in faces) {
      if (string.IsNullOrWhiteSpace(face.Name))
        continue;

      if (!this._personPhotos.TryGetValue(face.Name!, out var list)) {
        list = new List<(DateTime, FileInfo)>();
        this._personPhotos[face.Name!] = list;
      }

      // Use the file's last-write time as a quick proxy for capture date.
      // Reading full metadata for every file would be accurate but slow;
      // LastWriteTime is already available and close enough for a histogram.
      var date = face.File.LastWriteTime;
      list.Add((date, face.File));
    }

    if (this._personPhotos.Count == 0) {
      this.SetStatus("No named persons found — name faces in the Face gallery first.");
      return;
    }

    var sortedNames = this._personPhotos.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    var listBox = this.FindControl<ListBox>("PersonListBox");
    if (listBox != null) {
      listBox.ItemsSource = sortedNames;
      listBox.SelectedIndex = 0;
    }

    this.SetStatus($"{this._personPhotos.Count} person(s) with {faces.Count(f => !string.IsNullOrWhiteSpace(f.Name))} tagged face(s).");
  }

  private void OnPersonSelectionChanged(object? sender, SelectionChangedEventArgs e) {
    if (sender is not ListBox { SelectedItem: string personName })
      return;

    if (!this._personPhotos.TryGetValue(personName, out var photos))
      return;

    var data = PersonTimeline.Build(personName, photos);
    this.RenderTimeline(data);
  }

  private void RenderTimeline(PersonTimelineData? data) {
    var header = this.FindControl<TextBlock>("PersonHeader");
    var summary = this.FindControl<TextBlock>("PersonSummary");
    var chart = this.FindControl<StackPanel>("ChartPanel");

    if (header == null || summary == null || chart == null)
      return;

    chart.Children.Clear();

    if (data == null) {
      header.Text = "(no data)";
      summary.Text = "";
      this._currentPersonName = string.Empty;
      return;
    }

    this._currentPersonName = data.PersonName;
    header.Text = $"\U0001F464 {data.PersonName}";
    summary.Text = $"First seen: {data.FirstSeen:yyyy-MM-dd}  |  Last seen: {data.LastSeen:yyyy-MM-dd}  |  {data.YearBuckets.Sum(b => b.PhotoCount)} photo(s) across {data.YearBuckets.Count} year(s)";

    if (data.YearBuckets.Count == 0)
      return;

    var maxCount = data.YearBuckets.Max(b => b.PhotoCount);
    var maxBarHeight = 200.0;

    foreach (var bucket in data.YearBuckets) {
      var barHeight = maxCount > 0
        ? Math.Max(4, (double)bucket.PhotoCount / maxCount * maxBarHeight)
        : 4;

      var bar = new Border {
        Width = 36,
        Height = barHeight,
        Background = new SolidColorBrush(Color.Parse("#2471A3")),
        CornerRadius = new CornerRadius(3, 3, 0, 0),
        VerticalAlignment = VerticalAlignment.Bottom,
        Cursor = new Cursor(StandardCursorType.Hand),
        Tag = bucket
      };
      bar.PointerPressed += this.OnBarClicked;

      // Tooltip with count detail
      ToolTip.SetTip(bar, $"{bucket.Year}: {bucket.PhotoCount} photo(s)");

      var countLabel = new TextBlock {
        Text = bucket.PhotoCount.ToString(),
        FontSize = 11,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 2)
      };

      var yearLabel = new TextBlock {
        Text = bucket.Year.ToString(),
        FontSize = 11,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 4, 0, 0)
      };

      var column = new StackPanel {
        Orientation = Orientation.Vertical,
        VerticalAlignment = VerticalAlignment.Bottom,
        Spacing = 0
      };
      column.Children.Add(countLabel);
      column.Children.Add(bar);
      column.Children.Add(yearLabel);

      chart.Children.Add(column);
    }
  }

  private void OnBarClicked(object? sender, PointerPressedEventArgs e) {
    if (sender is not Border { Tag: YearBucket bucket })
      return;

    if (!string.IsNullOrWhiteSpace(this._currentPersonName))
      this.YearClicked?.Invoke(this._currentPersonName, bucket.Year);

    this.SetStatus($"Clicked: {this._currentPersonName} — {bucket.Year} ({bucket.PhotoCount} photo(s))");
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = message;
  }
}
