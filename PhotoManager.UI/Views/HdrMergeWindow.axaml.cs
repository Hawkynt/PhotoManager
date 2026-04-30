using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PhotoManager.Core.Hdr;
using PhotoManager.UI.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.UI.Views;

/// <summary>
/// Bracket-merge dialog: pick three or more frames of the same scene,
/// pick a tone-mapper and parameters, get an LDR JPEG. Heavy work runs
/// off-thread and posts UI updates through the Dispatcher.
/// </summary>
public partial class HdrMergeWindow : Window {
  private readonly ObservableCollection<HdrBracketRow> _rows = new();
  private CancellationTokenSource? _activeCts;

  public HdrMergeWindow() {
    this.InitializeComponent();
    if (this.FindControl<DataGrid>("BracketGrid") is { } grid)
      grid.ItemsSource = this._rows;

    if (this.FindControl<ComboBox>("OperatorCombo") is { } combo) {
      combo.ItemsSource = Enum.GetValues<ToneMapOperator>();
      combo.SelectedIndex = 0;
    }

    this.WireSliderLabels();
  }

  public HdrMergeWindow(IReadOnlyList<FileInfo> seedFiles) : this() {
    if (seedFiles.Count == 0)
      return;
    foreach (var f in seedFiles)
      this.AppendRow(f);
    this.RecomputeEvOffsets();
  }

  private void WireSliderLabels() {
    if (this.FindControl<Slider>("WhitePointSlider") is { } wp
        && this.FindControl<TextBlock>("WhitePointValue") is { } wpVal) {
      wp.PropertyChanged += (_, e) => {
        if (e.Property == Slider.ValueProperty)
          wpVal.Text = ((double)e.NewValue!).ToString("0.0", CultureInfo.InvariantCulture);
      };
    }
    if (this.FindControl<Slider>("DragoBiasSlider") is { } db
        && this.FindControl<TextBlock>("DragoBiasValue") is { } dbVal) {
      db.PropertyChanged += (_, e) => {
        if (e.Property == Slider.ValueProperty)
          dbVal.Text = ((double)e.NewValue!).ToString("0.00", CultureInfo.InvariantCulture);
      };
    }
    if (this.FindControl<Slider>("SaturationSlider") is { } sat
        && this.FindControl<TextBlock>("SaturationValue") is { } satVal) {
      sat.PropertyChanged += (_, e) => {
        if (e.Property == Slider.ValueProperty)
          satVal.Text = ((double)e.NewValue!).ToString("0.00", CultureInfo.InvariantCulture);
      };
    }
  }

  private async void OnAddFilesClick(object? sender, RoutedEventArgs e) {
    var top = TopLevel.GetTopLevel(this);
    if (top?.StorageProvider is not { CanOpen: true } sp)
      return;

    var picked = await sp.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Add bracket files",
      AllowMultiple = true,
      FileTypeFilter = [
        new FilePickerFileType("Images") {
          Patterns = ["*.jpg", "*.jpeg", "*.png", "*.tif", "*.tiff", "*.bmp", "*.webp"]
        }
      ]
    });
    if (picked.Count == 0)
      return;

    foreach (var item in picked) {
      var path = item.TryGetLocalPath();
      if (string.IsNullOrEmpty(path) || !File.Exists(path))
        continue;
      this.AppendRow(new FileInfo(path));
    }
    this.RecomputeEvOffsets();
  }

  private void OnRemoveClick(object? sender, RoutedEventArgs e) {
    if (this.FindControl<DataGrid>("BracketGrid") is not { } grid)
      return;
    var selected = grid.SelectedItems.Cast<HdrBracketRow>().ToList();
    foreach (var row in selected)
      this._rows.Remove(row);
    this.RecomputeEvOffsets();
  }

  private void OnMoveUpClick(object? sender, RoutedEventArgs e) => this.MoveSelected(-1);
  private void OnMoveDownClick(object? sender, RoutedEventArgs e) => this.MoveSelected(+1);

  private void MoveSelected(int delta) {
    if (this.FindControl<DataGrid>("BracketGrid") is not { SelectedItem: HdrBracketRow row } grid)
      return;
    var idx = this._rows.IndexOf(row);
    var target = idx + delta;
    if (target < 0 || target >= this._rows.Count)
      return;
    this._rows.Move(idx, target);
    grid.SelectedItem = row;
  }

  private void AppendRow(FileInfo file) {
    var seconds = HdrMerger.ReadExposureSeconds(file) ?? 0;
    var row = new HdrBracketRow {
      FileInfo = file,
      FileName = file.Name,
      ExposureSeconds = seconds,
      ExposureText = HdrBracketRow.FormatExposure(seconds)
    };
    // EV column re-renders whenever the user types a new exposure into any
    // row; cheap O(n) recomputation, runs on the row's own change event.
    row.PropertyChanged += (_, e) => {
      if (e.PropertyName == nameof(HdrBracketRow.ExposureSeconds))
        this.RecomputeEvOffsets();
    };
    this._rows.Add(row);
  }

  private void RecomputeEvOffsets() {
    if (this._rows.Count == 0)
      return;
    var sorted = this._rows.Where(r => r.ExposureSeconds > 0).OrderBy(r => r.ExposureSeconds).ToList();
    if (sorted.Count == 0) {
      foreach (var r in this._rows)
        r.EvOffsetText = string.Empty;
      return;
    }
    var median = sorted[sorted.Count / 2].ExposureSeconds;
    foreach (var r in this._rows)
      r.EvOffsetText = HdrBracketRow.FormatEvOffset(r.ExposureSeconds, median);
  }

  private async void OnPreviewClick(object? sender, RoutedEventArgs e) {
    await this.RunMergeAsync(previewMode: true);
  }

  private async void OnSaveAsClick(object? sender, RoutedEventArgs e) {
    if (this._rows.Count < 2) {
      this.SetStatus("Add at least two bracket frames first.");
      return;
    }
    var top = TopLevel.GetTopLevel(this);
    if (top?.StorageProvider is not { CanSave: true } sp)
      return;
    var save = await sp.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save HDR result",
      DefaultExtension = "jpg",
      SuggestedFileName = "hdr-merge.jpg",
      FileTypeChoices = [new FilePickerFileType("JPEG") { Patterns = ["*.jpg", "*.jpeg"] }]
    });
    if (save is null)
      return;
    var savePath = save.TryGetLocalPath();
    if (string.IsNullOrEmpty(savePath))
      return;
    await this.RunMergeAsync(previewMode: false, savePath);
  }

  private async Task RunMergeAsync(bool previewMode, string? savePath = null) {
    if (this._rows.Count < 2) {
      this.SetStatus("Add at least two bracket frames first.");
      return;
    }
    var unknownExposure = this._rows.FirstOrDefault(r => r.ExposureSeconds <= 0);
    if (unknownExposure != null) {
      this.SetStatus($"{unknownExposure.FileName} has no exposure time — type a value (in seconds) into its row first.");
      return;
    }

    this._activeCts?.Cancel();
    this._activeCts = new CancellationTokenSource();
    var ct = this._activeCts.Token;

    this.SetStatus(previewMode ? "Rendering preview…" : "Rendering full-resolution…");
    this.SetProgress(true);

    var entries = this._rows
      .Select(r => new HdrBracketEntry(r.FileInfo, r.ExposureSeconds))
      .ToList();

    var options = this.BuildOptions(previewMode);

    try {
      var result = await HdrMerger.MergeAsync(entries, options, ct);
      using var ldr = result.Image;

      if (previewMode) {
        var bitmap = await EncodeToBitmapAsync(ldr, ct);
        if (this.FindControl<Avalonia.Controls.Image>("PreviewImage") is { } img)
          img.Source = bitmap;
        this.SetStatus("Preview ready.");
      } else if (savePath != null) {
        await Task.Run(() => ldr.SaveAsJpeg(savePath), ct);
        this.SetStatus($"Saved {Path.GetFileName(savePath)}.");
      }
    } catch (OperationCanceledException) {
      this.SetStatus("Cancelled.");
    } catch (Exception ex) {
      this.SetStatus($"Merge failed: {ex.Message}");
    } finally {
      this.SetProgress(false);
    }
  }

  private HdrOptions BuildOptions(bool previewMode) {
    var op = (this.FindControl<ComboBox>("OperatorCombo")?.SelectedItem as ToneMapOperator?) ?? ToneMapOperator.Reinhard;
    var align = this.FindControl<CheckBox>("AlignBeforeMergeBox")?.IsChecked ?? true;
    var wp = this.FindControl<Slider>("WhitePointSlider")?.Value ?? 0;
    var bias = this.FindControl<Slider>("DragoBiasSlider")?.Value ?? 0.85;
    var sat = this.FindControl<Slider>("SaturationSlider")?.Value ?? 1.0;
    return new HdrOptions(
      AlignBeforeMerge: align,
      Operator: op,
      WhitePoint: wp,
      DragoBias: bias,
      Saturation: sat,
      PreviewLongEdge: previewMode ? 800 : null
    );
  }

  private static async Task<Bitmap> EncodeToBitmapAsync(Image<Rgba32> image, CancellationToken ct) {
    return await Task.Run(() => {
      using var ms = new MemoryStream();
      image.SaveAsJpeg(ms);
      ms.Position = 0;
      return new Bitmap(ms);
    }, ct);
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) {
    this._activeCts?.Cancel();
    this.Close();
  }

  private void SetStatus(string message) {
    Dispatcher.UIThread.Post(() => {
      if (this.FindControl<TextBlock>("StatusText") is { } t)
        t.Text = message;
    });
  }

  private void SetProgress(bool active) {
    Dispatcher.UIThread.Post(() => {
      if (this.FindControl<ProgressBar>("ProgressBar") is { } p)
        p.IsIndeterminate = active;
    });
  }
}
