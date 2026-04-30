using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PhotoManager.Core.Library;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Services;
using PhotoManager.UI.Models;
using PhotoManager.UI.Services;

namespace PhotoManager.UI.Views;

/// <summary>
/// Tools-menu window that runs <see cref="QualityFlagger"/> over a chosen
/// folder and surfaces the scores in a grid. The "Apply tags" button writes
/// <c>qa:blurry</c> / <c>qa:overexposed</c> / <c>qa:underexposed</c> keywords
/// via <see cref="CompositeMetadataWriter"/> so the flags survive in the
/// file's metadata for downstream filtering.
/// </summary>
public partial class QualityScanWindow : Window {
  private readonly SupportedFormatsService _formats = new();
  private readonly QualityFlagger _flagger = new();
  private readonly MetadataReader _reader = new();
  private readonly IMetadataWriter _writer = new CompositeMetadataWriter();
  private readonly ObservableCollection<QualityScanRow> _rows = [];

  public QualityScanWindow() {
    this.InitializeComponent();
    if (this.FindControl<DataGrid>("ResultsGrid") is { } grid)
      grid.ItemsSource = this._rows;
  }

  public QualityScanWindow(string initialFolder) : this() {
    if (this.FindControl<TextBox>("FolderBox") is { } box)
      box.Text = initialFolder;
  }

  private async void OnBrowseClick(object? sender, RoutedEventArgs e) {
    var picker = new AvaloniaFolderPicker();
    var initial = this.FindControl<TextBox>("FolderBox")?.Text;
    var chosen = await picker.PickFolderAsync("Select photos folder", initial);
    if (chosen != null && this.FindControl<TextBox>("FolderBox") is { } box)
      box.Text = chosen;
  }

  private async void OnScanClick(object? sender, RoutedEventArgs e) {
    var folder = this.FindControl<TextBox>("FolderBox")?.Text;
    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) {
      this.SetStatus("Pick a photos folder first.");
      return;
    }
    var recursive = this.FindControl<CheckBox>("RecursiveBox")?.IsChecked ?? true;

    this._rows.Clear();
    this.SetStatus("Collecting files...");

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

    this.SetStatus($"Analysing {files.Count} file(s)...");

    var processed = 0;
    var total = files.Count;
    var rows = new List<QualityScanRow>(total);

    await Task.Run(async () => {
      foreach (var file in files) {
        QualityResult result;
        try {
          result = await this._flagger.AnalyseAsync(file);
        } catch {
          // One bad file shouldn't abort the whole scan.
          result = default;
        }

        var row = new QualityScanRow(file, result);
        rows.Add(row);

        var done = Interlocked.Increment(ref processed);
        if (done % 25 == 0 || done == total)
          Dispatcher.UIThread.Post(() => this.SetStatus($"Analysing {done}/{total}..."));
      }
    });

    foreach (var row in rows)
      this._rows.Add(row);

    var flagged = rows.Count(r => r.IsAnyFlag);
    this.SetStatus($"Done — {flagged} of {rows.Count} flagged.");
  }

  private async void OnApplyTagsClick(object? sender, RoutedEventArgs e) {
    var rows = this._rows.Where(r => r.IsAnyFlag).ToList();
    if (rows.Count == 0) {
      this.SetStatus("Nothing flagged to tag.");
      return;
    }

    this.SetStatus($"Tagging {rows.Count} file(s)...");
    var written = 0;
    var skipped = 0;

    foreach (var row in rows) {
      try {
        var existing = await this._reader.ReadAsync(row.File);
        var keywords = new List<string>(existing.Keywords);
        AddIfMissing(keywords, "qa:blurry", row.Result.IsBlurry);
        AddIfMissing(keywords, "qa:overexposed", row.Result.IsOverexposed);
        AddIfMissing(keywords, "qa:underexposed", row.Result.IsUnderexposed);

        if (keywords.Count == existing.Keywords.Count) {
          skipped++;
          continue;
        }

        var edit = new MetadataEdit { Keywords = Optional<IReadOnlyList<string>>.Set(keywords) };
        await this._writer.ApplyAsync(row.File, edit);
        written++;
      } catch {
        skipped++;
      }
    }

    this.SetStatus($"Tagged {written} file(s); {skipped} skipped/unchanged.");
  }

  private static void AddIfMissing(List<string> keywords, string tag, bool flag) {
    if (!flag)
      return;
    if (keywords.Any(k => string.Equals(k, tag, StringComparison.OrdinalIgnoreCase)))
      return;
    keywords.Add(tag);
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private void SetStatus(string text) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = text;
  }
}
