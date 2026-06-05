using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hawkynt.PhotoManager.Core.Detection;
using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.Core.Ml;
using Hawkynt.PhotoManager.Core.Models;
using Hawkynt.PhotoManager.Core.Services;
using Hawkynt.PhotoManager.UI.Models;
using Hawkynt.PhotoManager.UI.Services;
using SixLabors.ImageSharp.PixelFormats;
using ImageSharp = SixLabors.ImageSharp.Image;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Library-scan dialog for auto-keyword tagging. Walks the chosen folder, runs
/// each image through the SigLIP vision encoder, ranks the configured
/// vocabulary by cosine similarity to the image embedding, and lets the user
/// review the top-K matches before writing them as <c>dc:subject</c> via the
/// composite metadata writer.
/// </summary>
public partial class AutoKeywordScanWindow : Window {
  private readonly SupportedFormatsService _formats = new();
  private readonly IMetadataReader _reader = new MetadataReader();
  private readonly IMetadataWriter _writer = new CompositeMetadataWriter();
  private readonly ObservableCollection<AutoKeywordRow> _rows = new();

  private IReadOnlyList<string> _vocabulary = AutoKeywordVocabulary.Default;
  private IReadOnlyList<float[]>? _vocabEmbeddings;
  private CancellationTokenSource? _scanCts;

  public AutoKeywordScanWindow() {
    this.InitializeComponent();
    if (this.FindControl<DataGrid>("ResultsGrid") is { } grid)
      grid.ItemsSource = this._rows;
    this.UpdateVocabSummary();
  }

  public AutoKeywordScanWindow(string initialFolder) : this() {
    if (this.FindControl<TextBox>("FolderBox") is { } box)
      box.Text = initialFolder;
  }

  private async void OnBrowseFolderClick(object? sender, RoutedEventArgs e) {
    var picker = new AvaloniaFolderPicker();
    var initial = this.FindControl<TextBox>("FolderBox")?.Text;
    var chosen = await picker.PickFolderAsync("Select photos folder", initial);
    if (chosen != null && this.FindControl<TextBox>("FolderBox") is { } box)
      box.Text = chosen;
  }

  private void OnMinSimChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e) {
    if (this.FindControl<TextBlock>("MinSimLabel") is { } label)
      label.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture);
  }

  private async void OnReloadVocabClick(object? sender, RoutedEventArgs e) {
    var topLevel = TopLevel.GetTopLevel(this);
    if (topLevel?.StorageProvider is not { CanOpen: true } storage)
      return;
    var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Select vocabulary file (one word per line)",
      AllowMultiple = false,
      FileTypeFilter = [new FilePickerFileType("Text") { Patterns = ["*.txt"] }]
    });
    if (picked.Count == 0)
      return;
    var path = picked[0].TryGetLocalPath();
    if (string.IsNullOrEmpty(path))
      return;
    try {
      var loaded = AutoKeywordVocabulary.LoadFromFile(new FileInfo(path));
      if (loaded.Count == 0) {
        this.SetStatus("Vocabulary file was empty or unreadable.");
        return;
      }
      this._vocabulary = loaded;
      this._vocabEmbeddings = null;  // force re-embedding on next scan
      this.UpdateVocabSummary();
      this.SetStatus($"Loaded {loaded.Count} vocabulary words from {Path.GetFileName(path)}.");
    } catch (Exception ex) {
      this.SetStatus($"Couldn't load vocabulary: {ex.Message}");
    }
  }

  private void OnRestoreDefaultVocabClick(object? sender, RoutedEventArgs e) {
    this._vocabulary = AutoKeywordVocabulary.Default;
    this._vocabEmbeddings = null;
    this.UpdateVocabSummary();
    this.SetStatus($"Restored default vocabulary ({this._vocabulary.Count} words).");
  }

  private async void OnScanClick(object? sender, RoutedEventArgs e) {
    var folder = this.FindControl<TextBox>("FolderBox")?.Text;
    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) {
      this.SetStatus("Pick a photos folder first.");
      return;
    }

    var topK = (int)(this.FindControl<NumericUpDown>("TopKBox")?.Value ?? 5m);
    var minSim = (float)(this.FindControl<Slider>("MinSimSlider")?.Value ?? 0.22);
    var recursive = this.FindControl<CheckBox>("RecursiveBox")?.IsChecked ?? true;

    // Prompt up-front if either SigLIP tower is missing — silent no-op
    // makes the user think the scan is broken.
    if (!await ModelPrompt.EnsureInstalledAsync(this, ModelRegistry.SiglipVision, "Auto-keyword (vision encoder)"))
      return;
    if (this._vocabEmbeddings == null && !await ModelPrompt.EnsureInstalledAsync(this, ModelRegistry.SiglipText, "Auto-keyword (text encoder)"))
      return;

    using var imageEncoder = new OnnxClipImageEncoder();
    using var textEncoder = new OnnxClipTextEncoder();

    if (!imageEncoder.IsAvailable) {
      this.SetStatus("SigLIP vision model is still missing after install — check the file landed in the models folder.");
      return;
    }

    if (this._vocabEmbeddings == null) {
      if (!textEncoder.IsAvailable) {
        this.SetStatus("SigLIP text model is still missing after install — check the file landed in the models folder.");
        return;
      }
      this.SetStatus($"Embedding {this._vocabulary.Count} vocabulary words...");
      this._vocabEmbeddings = await Task.Run(() => textEncoder.EmbedAllCached(this._vocabulary));
      if (this._vocabEmbeddings == null) {
        this.SetStatus("Vocabulary embedding failed.");
        return;
      }
    }

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

    this._rows.Clear();
    this._scanCts?.Cancel();
    this._scanCts = new CancellationTokenSource();
    var token = this._scanCts.Token;

    var scanned = 0;
    foreach (var file in files) {
      if (token.IsCancellationRequested)
        break;

      var localFile = file;
      this.SetStatus($"Scanning {localFile.Name} ({scanned + 1}/{files.Count})...");
      var row = await Task.Run(() => this.ScanOneFile(localFile, imageEncoder, topK, minSim), token);
      this._rows.Add(row);
      scanned++;
    }

    this.SetStatus($"Scanned {scanned} of {files.Count} file(s). Click 'Apply tags' to write keywords.");
  }

  private AutoKeywordRow ScanOneFile(FileInfo file, OnnxClipImageEncoder encoder, int topK, float minSim) {
    try {
      using var img = ImageSharp.Load<Rgba32>(file.FullName);
      var embedding = encoder.Embed(img);
      if (embedding == null)
        return Row(file, "encode failed", Array.Empty<string>(), string.Empty);

      var matches = AutoKeywordTagger.Tag(embedding, this._vocabEmbeddings!, this._vocabulary, topK, minSim);
      if (matches.Count == 0)
        return Row(file, "ok", Array.Empty<string>(), "(no matches)");

      var inv = CultureInfo.InvariantCulture;
      var display = string.Join(", ", matches.Select(m => $"{m.Word} ({m.Score.ToString("0.00", inv)})"));
      var raw = matches.Select(m => m.Word).ToArray();
      return Row(file, "ok", raw, display);
    } catch (Exception ex) {
      return Row(file, "error", Array.Empty<string>(), ex.Message);
    }
  }

  private static AutoKeywordRow Row(FileInfo file, string status, IReadOnlyList<string> raw, string display)
    => new() {
      File = file,
      FileName = file.Name,
      Keywords = display,
      RawKeywords = raw,
      Status = status
    };

  private async void OnApplyTagsClick(object? sender, RoutedEventArgs e) {
    var rows = this._rows.Where(r => r.RawKeywords.Count > 0).ToList();
    if (rows.Count == 0) {
      this.SetStatus("Nothing to apply — scan first.");
      return;
    }

    var written = 0;
    var failed = 0;
    foreach (var row in rows) {
      try {
        var existing = await this._reader.ReadAsync(row.File);
        var merged = DetectionService.MergeKeywords(existing.Keywords, row.RawKeywords);
        await this._writer.ApplyAsync(row.File, new MetadataEdit {
          Keywords = Optional<IReadOnlyList<string>>.Set(merged)
        });
        written++;
        await Dispatcher.UIThread.InvokeAsync(() =>
          this.SetStatus($"Tagged {written}/{rows.Count}..."));
      } catch (Exception ex) {
        failed++;
        await Dispatcher.UIThread.InvokeAsync(() =>
          this.SetStatus($"Failed to tag {row.File.Name}: {ex.Message}"));
      }
    }

    this.SetStatus($"Done — wrote keywords to {written} file(s), {failed} failed.");
  }

  private void UpdateVocabSummary() {
    if (this.FindControl<TextBlock>("VocabSummary") is { } s)
      s.Text = $"{this._vocabulary.Count} words loaded.";
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) {
    this._scanCts?.Cancel();
    this.Close();
  }

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = message;
  }
}
