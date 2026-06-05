using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hawkynt.PhotoManager.Core.Panorama;
using StitcherMode = OpenCvSharp.Stitcher.Mode;
using Hawkynt.PhotoManager.UI.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SharpImage = SixLabors.ImageSharp.Image;

namespace Hawkynt.PhotoManager.UI.Views;

public partial class PanoramaStitchWindow : Window {
  private static readonly string[] SupportedExtensions =
    [".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".webp"];

  private readonly ObservableCollection<PanoramaInputRow> _rows = new();
  private Image<Rgba32>? _lastResult;

  public PanoramaStitchWindow() {
    this.InitializeComponent();
    if (this.FindControl<DataGrid>("InputsGrid") is { } grid)
      grid.ItemsSource = this._rows;
    this.UpdateFocalLabel();
    this.UpdateOverlapLabel();
    this.UpdateConfidenceLabel();
    this.Closed += (_, _) => {
      this._lastResult?.Dispose();
      this._lastResult = null;
    };
  }

  public PanoramaStitchWindow(IEnumerable<FileInfo> seedFiles) : this() {
    Dispatcher.UIThread.Post(async () => await this.AddFilesAsync(seedFiles), DispatcherPriority.Background);
  }

  private async void OnAddFilesClick(object? sender, RoutedEventArgs e) {
    var topLevel = TopLevel.GetTopLevel(this);
    if (topLevel?.StorageProvider is not { CanOpen: true } storage)
      return;

    var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Add panorama frames",
      AllowMultiple = true,
      FileTypeFilter = [
        new FilePickerFileType("Images") {
          Patterns = ["*.jpg", "*.jpeg", "*.png", "*.tif", "*.tiff", "*.bmp", "*.webp"]
        }
      ]
    });
    if (picked.Count == 0)
      return;

    var files = picked
      .Select(p => p.TryGetLocalPath())
      .Where(p => !string.IsNullOrWhiteSpace(p))
      .Select(p => new FileInfo(p!))
      .ToList();
    await this.AddFilesAsync(files);
  }

  private async void OnFromVideoClick(object? sender, RoutedEventArgs e) {
    // The extractor's FramesReady event fires with the list of generated
    // frame files; we feed them straight into our own input list and let
    // the user keep tweaking the stitch parameters before pressing Preview.
    var extractor = new VideoExtractWindow(autoCloseOnSuccess: true);
    extractor.FramesReady += async frames => {
      await this.AddFilesAsync(frames.ToList());
    };
    await extractor.ShowDialog(this);
  }

  private async Task AddFilesAsync(IEnumerable<FileInfo> files) {
    foreach (var file in files) {
      if (!file.Exists)
        continue;
      if (!SupportedExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
        continue;
      try {
        var info = await Task.Run(() => SharpImage.Identify(file.FullName));
        if (info is null)
          continue;
        this._rows.Add(new PanoramaInputRow(file, info.Width, info.Height));
      } catch {
        // Skip files we can't identify.
      }
    }
    this.SetStatus($"{this._rows.Count} frame(s) loaded.");
  }

  private void OnRemoveClick(object? sender, RoutedEventArgs e) {
    if (this.FindControl<DataGrid>("InputsGrid") is not { } grid)
      return;
    var selected = grid.SelectedItems.OfType<PanoramaInputRow>().ToList();
    foreach (var row in selected)
      this._rows.Remove(row);
  }

  private void OnMoveUpClick(object? sender, RoutedEventArgs e) => this.MoveSelected(-1);
  private void OnMoveDownClick(object? sender, RoutedEventArgs e) => this.MoveSelected(+1);

  private void MoveSelected(int delta) {
    if (this.FindControl<DataGrid>("InputsGrid") is not { } grid)
      return;
    if (grid.SelectedItem is not PanoramaInputRow selected)
      return;
    var idx = this._rows.IndexOf(selected);
    var target = idx + delta;
    if (target < 0 || target >= this._rows.Count)
      return;
    this._rows.Move(idx, target);
    grid.SelectedIndex = target;
  }

  private void OnModeChanged(object? sender, RoutedEventArgs e) {
    var tripod = this.FindControl<RadioButton>("TripodModeRadio")?.IsChecked == true;
    var handheld = this.FindControl<RadioButton>("HandheldModeRadio")?.IsChecked == true;
    var spherical = this.FindControl<RadioButton>("SphericalModeRadio")?.IsChecked == true;
    if (this.FindControl<StackPanel>("TripodControls") is { } tc)
      tc.IsVisible = tripod;
    if (this.FindControl<StackPanel>("HandheldControls") is { } hc)
      hc.IsVisible = handheld;
    if (this.FindControl<StackPanel>("SphericalControls") is { } sc)
      sc.IsVisible = spherical;
  }

  private void OnFocalChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    => this.UpdateFocalLabel();

  private void OnOverlapChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    => this.UpdateOverlapLabel();

  private void OnConfidenceChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    => this.UpdateConfidenceLabel();

  private void UpdateFocalLabel() {
    if (this.FindControl<Slider>("FocalSlider") is not { } slider)
      return;
    if (this.FindControl<TextBlock>("FocalLabel") is { } label)
      label.Text = ((int)slider.Value).ToString(CultureInfo.InvariantCulture);
  }

  private void UpdateOverlapLabel() {
    if (this.FindControl<Slider>("OverlapSlider") is not { } slider)
      return;
    if (this.FindControl<TextBlock>("OverlapLabel") is { } label)
      label.Text = (slider.Value * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
  }

  private void UpdateConfidenceLabel() {
    if (this.FindControl<Slider>("ConfidenceSlider") is not { } slider)
      return;
    if (this.FindControl<TextBlock>("ConfidenceLabel") is { } label)
      label.Text = slider.Value.ToString("0.00", CultureInfo.InvariantCulture);
  }

  private async void OnPreviewClick(object? sender, RoutedEventArgs e) {
    if (this._rows.Count == 0) {
      this.SetStatus("Add at least one frame first.");
      return;
    }
    await this.RenderAsync(previewOnly: true);
  }

  private async void OnSaveAsClick(object? sender, RoutedEventArgs e) {
    if (this._rows.Count == 0) {
      this.SetStatus("Add at least one frame first.");
      return;
    }

    var topLevel = TopLevel.GetTopLevel(this);
    if (topLevel?.StorageProvider is not { CanSave: true } storage) {
      this.SetStatus("Save picker unavailable.");
      return;
    }

    var first = this._rows[0].File;
    var suggested = Path.GetFileNameWithoutExtension(first.Name) + "_panorama.jpg";
    var result = await storage.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save stitched panorama",
      SuggestedFileName = suggested,
      DefaultExtension = "jpg",
      FileTypeChoices = [new FilePickerFileType("JPEG") { Patterns = ["*.jpg"] }]
    });
    if (result is null)
      return;

    var destination = result.TryGetLocalPath();
    if (string.IsNullOrWhiteSpace(destination)) {
      this.SetStatus("Couldn't resolve save path.");
      return;
    }

    await this.RenderAsync(previewOnly: false, savePath: destination);
  }

  private async Task RenderAsync(bool previewOnly, string? savePath = null) {
    var snapshot = this._rows.Select(r => r.File).ToList();
    var tripod = this.FindControl<RadioButton>("TripodModeRadio")?.IsChecked == true;
    var spherical = this.FindControl<RadioButton>("SphericalModeRadio")?.IsChecked == true;
    var focal = this.FindControl<Slider>("FocalSlider")?.Value ?? 1024;
    var overlap = this.FindControl<Slider>("OverlapSlider")?.Value ?? 0.3;
    var confidence = this.FindControl<Slider>("ConfidenceSlider")?.Value ?? 1.0;
    var openCvMode = this.FindControl<ComboBox>("OpenCvModeCombo")?.SelectedIndex == 1
      ? StitcherMode.Scans : StitcherMode.Panorama;
    var autoCrop = this.FindControl<CheckBox>("AutoCropCheck")?.IsChecked == true;
    var sphericalWidth = this.PickSphericalOutputWidth();
    var sphericalBlend = this.FindControl<CheckBox>("SphericalBlendCheck")?.IsChecked == true;
    var smartPatch = this.FindControl<CheckBox>("SmartPatchCheck")?.IsChecked == true;

    this.SetStatus("Loading frames...");
    this.SetProgress(2);

    Image<Rgba32>? composed = null;
    try {
      composed = await Task.Run(() => {
        var sources = new List<Image<Rgba32>>(snapshot.Count);
        var masks = new List<Image<L8>>();
        try {
          for (var i = 0; i < snapshot.Count; i++) {
            // Flatten alpha onto white so transparent GIF / PNG sources
            // don't paint black through the stitch.
            sources.Add(Hawkynt.PhotoManager.Core.Imaging.AlphaFlattener.FlattenOntoWhite(
              SharpImage.Load<Rgba32>(snapshot[i].FullName)));
            this.PostProgress(2 + (int)(20.0 * (i + 1) / snapshot.Count),
              $"Loading {snapshot[i].Name}");
          }

          if (smartPatch) {
            for (var i = 0; i < sources.Count; i++) {
              this.PostProgress(22 + (int)(8.0 * (i + 1) / sources.Count),
                $"Analysing sharpness {i + 1}/{sources.Count}…");
              masks.Add(SharpnessAnalyser.BuildPatchMask(sources[i]));
            }
          }

          if (tripod) {
            this.PostProgress(32, "Cylindrical projection...");
            var warped = sources.Select(s => CylindricalProjector.Project(s, focal)).ToList();
            // Cylindrical projection changes pixel dimensions, so masks
            // computed on the originals don't align. Re-project masks
            // through a luma-only path: rebuild a mask per warped frame.
            var warpedMasks = smartPatch
              ? warped.Select(w => SharpnessAnalyser.BuildPatchMask(w)).ToList()
              : null;
            try {
              this.PostProgress(60, "Stitching...");
              return TripodPanoramaStitcher.Stitch(warped, overlap, warpedMasks);
            } finally {
              foreach (var w in warped)
                w.Dispose();
              if (warpedMasks is not null) {
                foreach (var m in warpedMasks)
                  m.Dispose();
              }
            }
          }

          if (spherical) {
            this.PostProgress(40, "Spherical stitch...");
            var sphericalOptions = new SphericalStitchOptions {
              SeamConfidence = confidence,
              OutputWidth = sphericalWidth,
              BlendOverlaps = sphericalBlend,
              Masks = smartPatch ? masks : null
            };
            try {
              return SphericalPanoramaStitcher.Stitch(sources, sphericalOptions);
            } catch (DllNotFoundException ex) {
              throw new InvalidOperationException(
                "OpenCV native library not loaded — install the matching OpenCvSharp4.runtime.* package.", ex);
            }
          }

          this.PostProgress(40, "OpenCV stitch...");
          var options = new OpenCvStitchOptions {
            Mode = openCvMode,
            Confidence = confidence,
            AutoCropToContent = autoCrop,
            Masks = smartPatch ? masks : null
          };
          try {
            return OpenCvPanoramaStitcher.Stitch(sources, options);
          } catch (DllNotFoundException ex) {
            throw new InvalidOperationException(
              "OpenCV native library not loaded — install the matching OpenCvSharp4.runtime.* package.", ex);
          }
        } finally {
          foreach (var s in sources)
            s.Dispose();
          foreach (var m in masks)
            m.Dispose();
        }
      });
    } catch (Exception ex) {
      this.SetProgress(0);
      this.SetStatus($"Stitch failed: {ex.Message}");
      return;
    }

    if (composed is null) {
      this.SetProgress(0);
      var hint = spherical && !string.IsNullOrEmpty(SphericalPanoramaStitcher.LastStatus)
        ? $"Stitcher couldn't merge the supplied frames: {SphericalPanoramaStitcher.LastStatus}"
        : "Stitcher couldn't merge the supplied frames (insufficient overlap or features).";
      this.SetStatus(hint);
      return;
    }

    try {
      if (previewOnly) {
        this.PostProgress(85, "Building preview...");
        await this.UpdatePreviewAsync(composed);
        this.SetStatus($"Preview ready: {composed.Width}×{composed.Height} px.");
        this.SetProgress(100);
        this.RetainResult(composed);
        composed = null;
        return;
      }

      this.PostProgress(85, "Saving JPEG...");
      var dest = savePath!;
      await composed.SaveAsJpegAsync(dest);
      await this.UpdatePreviewAsync(composed);
      this.SetStatus($"Saved {Path.GetFileName(dest)} ({composed.Width}×{composed.Height} px).");
      this.SetProgress(100);
      this.RetainResult(composed);
      composed = null;
    } finally {
      composed?.Dispose();
    }
  }

  private void RetainResult(Image<Rgba32> result) {
    this._lastResult?.Dispose();
    this._lastResult = result;
    if (this.FindControl<Button>("OpenInViewerButton") is { } btn)
      btn.IsEnabled = true;
  }

  private int PickSphericalOutputWidth() {
    var combo = this.FindControl<ComboBox>("SphericalWidthCombo");
    var idx = combo?.SelectedIndex ?? 2;
    return idx switch {
      0 => 1024,
      1 => 2048,
      3 => 8192,
      4 => 16384,
      5 => 32768,
      _ => 4096
    };
  }

  private void OnOpenInViewerClick(object? sender, RoutedEventArgs e) {
    if (this._lastResult is null) {
      this.SetStatus("No stitched result to view yet — preview or save first.");
      return;
    }
    // Hand ownership of the pixel buffer to the viewer so we don't clone
    // a multi-megapixel image; clone() is cheap enough that we keep our
    // own copy alive in case the user wants to re-open.
    var clone = this._lastResult.Clone();
    var viewer = new PanoramaViewerWindow(clone);
    viewer.Show(this);
  }

  private async Task UpdatePreviewAsync(Image<Rgba32> source) {
    const int max = 900;
    var scale = Math.Min(1.0, (double)max / Math.Max(source.Width, source.Height));
    var w = Math.Max(1, (int)(source.Width * scale));
    var h = Math.Max(1, (int)(source.Height * scale));

    using var ms = new MemoryStream();
    using (var thumb = source.Clone(ctx => ctx.Resize(w, h))) {
      await thumb.SaveAsPngAsync(ms);
    }
    ms.Position = 0;
    var bmp = new Bitmap(ms);
    await Dispatcher.UIThread.InvokeAsync(() => {
      if (this.FindControl<Avalonia.Controls.Image>("PreviewImage") is { } img)
        img.Source = bmp;
    });
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = message;
  }

  private void SetProgress(double value) {
    if (this.FindControl<ProgressBar>("ProgressBar") is { } p)
      p.Value = value;
  }

  private void PostProgress(double value, string message) {
    Dispatcher.UIThread.Post(() => {
      this.SetProgress(value);
      this.SetStatus(message);
    });
  }
}
