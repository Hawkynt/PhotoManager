using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PhotoManager.Core;
using PhotoManager.Core.Detection;
using PhotoManager.Core.Develop;
using PhotoManager.Core.Faces;
using PhotoManager.Core.Models;
using PhotoManager.Core.Segmentation;
using PhotoManager.UI.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.UI.Views;

/// <summary>
/// Single-image restoration workshop. Open with a file, twiddle four
/// sliders + a checkbox + an upscale combo (or pick a preset), see the
/// restored result side-by-side with the source, save as JPEG.
///
/// Preview pipeline mirrors EditImageWindow: a fast no-AI baseline isn't
/// shown — the restored side is always the AI pipeline, but we cancel
/// in-flight work and re-run on every settings change so the user gets
/// responsive feedback. Faces are detected once on load (asynchronously)
/// and reused across every preview render so the AI pass doesn't pay
/// the detection cost on every slider drag.
/// </summary>
public partial class RestoreWindow : Window {
  /// <summary>Cap the preview source's longest edge so live re-renders stay snappy. Save As uses the full-resolution source.</summary>
  private const int PreviewMaxEdgePixels = 1024;

  private readonly FileInfo? _sourceFile;
  private Image<Rgba32>? _previewSource;       // downscaled — drives preview pipeline
  private Image<Rgba32>? _fullResolutionSource; // full-res — used by Save As
  private IReadOnlyList<NormalizedBoundingBox> _faces = Array.Empty<NormalizedBoundingBox>();
  private CancellationTokenSource? _previewCts;
  private bool _suppressEvents;

  // Synchronised zoom + pan state — applied identically to source and
  // restored Image controls so the user can compare pixel-for-pixel
  // anywhere in the image. Wheel zooms anchored at the cursor; right-drag
  // pans; double-click resets.
  private double _zoom = 1.0;
  private double _panX;
  private double _panY;
  private bool _isPanning;
  private Avalonia.Point _panStart;

  // Brush-mask state for LaMa inpainting. Mask is the same dimensions as
  // _previewSource; each painted pixel sets R=255 (any non-zero R counts
  // as masked at inpaint time). The Avalonia overlay shows it as a
  // semi-transparent red layer.
  private Image<Rgba32>? _maskImage;
  private bool _isPainting;
  private int _brushSize = 20;

  public RestoreWindow() : this(null) { }

  public RestoreWindow(FileInfo? sourceFile) {
    this._suppressEvents = true;
    try {
      this.InitializeComponent();
    } finally {
      this._suppressEvents = false;
    }
    this._sourceFile = sourceFile;
    if (sourceFile is not null)
      this.Title = $"🪄 Restore — {sourceFile.Name}";

    this.PopulateModelCombos();
    this.Opened += async (_, _) => await this.LoadAsync();
  }

  /// <summary>
  /// Seed each model picker from the central <see cref="ModelRegistry"/>.
  /// The first entry is the default and stores null in
  /// <see cref="RestorationSettings"/> so XMP / saved presets remain
  /// portable across machines that may have re-ordered model lists.
  /// </summary>
  private void PopulateModelCombos() {
    this._suppressEvents = true;
    try {
      Populate(this.FindControl<ComboBox>("FaceModelCombo"),    ModelRegistry.FaceRestorers);
      Populate(this.FindControl<ComboBox>("DenoiseModelCombo"), ModelRegistry.Denoisers);
      Populate(this.FindControl<ComboBox>("ColourModelCombo"),  ModelRegistry.Colorizers);
      Populate(this.FindControl<ComboBox>("UpscaleModelCombo"), ModelRegistry.Upscalers);
    } finally {
      this._suppressEvents = false;
    }

    static void Populate(ComboBox? combo, IReadOnlyList<ModelInfo> models) {
      if (combo is null) return;
      combo.Items.Clear();
      foreach (var m in models)
        combo.Items.Add(new ComboBoxItem { Content = m.DisplayName, Tag = m.FileName });
      combo.SelectedIndex = 0;
    }
  }

  private static string? PickedFileName(ComboBox? combo, IReadOnlyList<ModelInfo> models) {
    if (combo is null) return null;
    var idx = combo.SelectedIndex;
    if (idx <= 0 || idx >= models.Count)
      return null;  // 0 = default → store null so the pipeline picks the registry default
    return models[idx].FileName;
  }

  // ---------- Per-model change handlers ----------
  // Each handler prompts to download the picked model when a corresponding
  // strength is non-zero, then re-renders the preview. Keeping these in
  // separate methods avoids tangling the install prompt with the generic
  // settings-changed callback.

  private async void OnFaceModelChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressEvents) return;
    await this.PromptIfMissing(sender, ModelRegistry.FaceRestorers, this.ReadSettings().FaceRestoreStrength, "AI face restore");
    this.SchedulePreviewUpdate();
  }

  private async void OnDenoiseModelChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressEvents) return;
    await this.PromptIfMissing(sender, ModelRegistry.Denoisers, this.ReadSettings().DenoiseStrength, "AI denoise");
    this.SchedulePreviewUpdate();
  }

  private async void OnColourModelChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressEvents) return;
    await this.PromptIfMissing(sender, ModelRegistry.Colorizers, this.ReadSettings().RecolourStrength, "AI colorize");
    this.SchedulePreviewUpdate();
  }

  private async void OnUpscaleModelChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressEvents) return;
    var settings = this.ReadSettings();
    await this.PromptIfMissing(sender, ModelRegistry.Upscalers, settings.UpscaleFactor > 1 ? 1.0 : 0.0, "AI upscale");
    this.SchedulePreviewUpdate();
  }

  private async Task PromptIfMissing(object? sender, IReadOnlyList<ModelInfo> models, double strength, string featureName) {
    if (strength <= 1e-6) return;
    if (sender is not ComboBox combo) return;
    var idx = combo.SelectedIndex;
    if (idx < 0 || idx >= models.Count) return;
    var model = models[idx];
    if (!model.IsInstalled())
      await ModelPrompt.EnsureInstalledAsync(this, model, $"{featureName} ({model.DisplayName})");
  }

  // ---------- Load ----------

  private async Task LoadAsync() {
    if (this._sourceFile is not { Exists: true } file) {
      this.SetStatus("No file selected.");
      return;
    }
    this.SetStatus("Loading…");
    try {
      var image = await RawImageLoader.LoadAsync(file);
      this._fullResolutionSource = image.Clone();
      var longest = Math.Max(image.Width, image.Height);
      if (longest > PreviewMaxEdgePixels) {
        var scale = (double)PreviewMaxEdgePixels / longest;
        image.Mutate(c => c.Resize((int)(image.Width * scale), (int)(image.Height * scale)));
      }
      this._previewSource = image;
    } catch (Exception ex) {
      this.SetStatus($"Load failed: {ex.Message}");
      return;
    }

    this.SetStatus($"{this._sourceFile.Name} ({this._previewSource!.Width}×{this._previewSource.Height} preview)");
    this.UpdateSourceBitmap();

    // Detect faces in the background once. Until detection finishes the
    // face slider is honoured but produces no effect (empty face list).
    _ = Task.Run(async () => {
      try {
        using var detector = new OnnxFaceDetector();
        var detected = await detector.DetectAsync(this._sourceFile);
        this._faces = detected.Where(f => f.Region is not null).Select(f => f.Region.Box).ToList();
      } catch {
        this._faces = Array.Empty<NormalizedBoundingBox>();
      }
      Avalonia.Threading.Dispatcher.UIThread.Post(() => {
        if (this.FindControl<TextBlock>("FaceCountLabel") is { } label)
          label.Text = this._faces.Count switch {
            0 => "No faces detected — the Restore-faces slider has no effect on this image.",
            1 => "1 face detected — the Restore-faces slider will apply GFPGAN to it.",
            var n => $"{n} faces detected — the Restore-faces slider will apply GFPGAN to each."
          };
      });
    });

    this.SchedulePreviewUpdate();
  }

  private void UpdateSourceBitmap() {
    if (this._previewSource is null) return;
    using var ms = new MemoryStream();
    this._previewSource.SaveAsJpeg(ms);
    ms.Position = 0;
    var bmp = new Bitmap(ms);
    if (this.FindControl<Avalonia.Controls.Image>("SourcePreview") is { } img)
      img.Source = bmp;
  }

  // ---------- Settings → preview ----------

  private RestorationSettings ReadSettings() {
    var face = (this.FindControl<Slider>("FaceSlider")?.Value ?? 0) / 100.0;
    var denoise = (this.FindControl<Slider>("DenoiseSlider")?.Value ?? 0) / 100.0;
    var colour = (this.FindControl<Slider>("ColourSlider")?.Value ?? 0) / 100.0;
    var autoTone = this.FindControl<CheckBox>("AutoToneBox")?.IsChecked == true;
    var upscaleIdx = this.FindControl<ComboBox>("UpscaleCombo")?.SelectedIndex ?? 0;
    var upscaleFactor = upscaleIdx switch {
      1 => 2,
      2 => 4,
      3 => 16,
      4 => 64,
      _ => 1
    };
    return new RestorationSettings(
      FaceRestoreStrength: face,
      DenoiseStrength: denoise,
      RecolourStrength: colour,
      AutoTone: autoTone,
      UpscaleFactor: upscaleFactor,
      // RestorationSettings doesn't carry a face-model field (GFPGAN only,
      // for now); the others mirror what the develop window stores.
      DenoiseModel: PickedFileName(this.FindControl<ComboBox>("DenoiseModelCombo"), ModelRegistry.Denoisers),
      ColorizeModel: PickedFileName(this.FindControl<ComboBox>("ColourModelCombo"), ModelRegistry.Colorizers),
      UpscaleModel: PickedFileName(this.FindControl<ComboBox>("UpscaleModelCombo"), ModelRegistry.Upscalers)
    );
  }

  private void RefreshValueLabels() {
    if (this.FindControl<TextBlock>("FaceValue") is { } fv && this.FindControl<Slider>("FaceSlider") is { } fs)
      fv.Text = ((int)fs.Value).ToString(CultureInfo.InvariantCulture);
    if (this.FindControl<TextBlock>("DenoiseValue") is { } dv && this.FindControl<Slider>("DenoiseSlider") is { } ds)
      dv.Text = ((int)ds.Value).ToString(CultureInfo.InvariantCulture);
    if (this.FindControl<TextBlock>("ColourValue") is { } cv && this.FindControl<Slider>("ColourSlider") is { } cs)
      cv.Text = ((int)cs.Value).ToString(CultureInfo.InvariantCulture);
    if (this.FindControl<TextBlock>("UpscaleValue") is { } uv && this.FindControl<ComboBox>("UpscaleCombo") is { } uc)
      uv.Text = uc.SelectedIndex switch { 1 => "2×", 2 => "4×", 3 => "16×", 4 => "64×", _ => "1×" };
  }

  private void OnSettingsChangedSlider(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressEvents) return;
    this.SchedulePreviewUpdate();
  }

  private void OnSettingsChangedClick(object? sender, RoutedEventArgs e) {
    if (this._suppressEvents) return;
    this.SchedulePreviewUpdate();
  }

  private void OnUpscaleChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressEvents) return;
    this.SchedulePreviewUpdate();
  }

  /// <summary>
  /// Cancel any in-flight preview render and kick off a fresh one. The AI
  /// pass runs off-thread; the result is dropped if a newer call has
  /// replaced our token.
  /// </summary>
  private void SchedulePreviewUpdate() {
    this.RefreshValueLabels();
    if (this._previewSource is null)
      return;
    var prev = Interlocked.Exchange(ref this._previewCts, null);
    prev?.Cancel();
    prev?.Dispose();

    var settings = this.ReadSettings();
    if (settings.IsIdentity) {
      this.PaintRestored(this._previewSource);
      this.SetOverlayVisible(false);
      return;
    }

    var stages = new List<string>();
    if (settings.AutoTone)                stages.Add("auto-tone");
    if (settings.DenoiseStrength > 0)     stages.Add("denoise");
    if (settings.RecolourStrength > 0)    stages.Add("recolour");
    if (settings.FaceRestoreStrength > 0) stages.Add("face restore");
    if (settings.UpscaleFactor > 1)       stages.Add($"{settings.UpscaleFactor}× upscale");
    if (this.FindControl<TextBlock>("ProgressText") is { } pt)
      pt.Text = $"Restoring · {string.Join(" + ", stages)}…";
    this.SetOverlayVisible(true);

    var cts = new CancellationTokenSource();
    this._previewCts = cts;
    var sourceClone = this._previewSource.Clone();
    var captured = settings;
    var capturedFaces = this._faces;

    _ = Task.Run(() => {
      try {
        using (sourceClone)
          return RestorationPipeline.Apply(sourceClone, capturedFaces, captured, cts.Token);
      } catch (OperationCanceledException) { return null; }
        catch { return null; }
    }, cts.Token).ContinueWith(t => {
      var result = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
      Avalonia.Threading.Dispatcher.UIThread.Post(() => {
        if (cts.IsCancellationRequested || !ReferenceEquals(this._previewCts, cts)) {
          result?.Dispose();
          return;
        }
        if (result is null) {
          this.SetOverlayVisible(false);
          this.SetStatus("Restoration failed — check that the required ONNX models are installed.");
          return;
        }
        this.PaintRestored(result);
        result.Dispose();
        this.SetOverlayVisible(false);
        this.SetStatus("Preview updated.");
      });
    });
  }

  private void PaintRestored(Image<Rgba32> image) {
    using var ms = new MemoryStream();
    image.SaveAsJpeg(ms);
    ms.Position = 0;
    var bmp = new Bitmap(ms);
    if (this.FindControl<Avalonia.Controls.Image>("RestoredPreview") is { } img)
      img.Source = bmp;
  }

  private void SetOverlayVisible(bool visible) {
    if (this.FindControl<Border>("ProgressOverlay") is { } overlay)
      overlay.IsVisible = visible;
  }

  // ---------- Presets ----------

  private void OnPresetOldBwClick(object? sender, RoutedEventArgs e)
    => this.ApplyPreset(RestorationSettings.OldBlackAndWhite);

  private void OnPresetDamagedColourClick(object? sender, RoutedEventArgs e)
    => this.ApplyPreset(RestorationSettings.DamagedColour);

  private void OnPresetFadedSlideClick(object? sender, RoutedEventArgs e)
    => this.ApplyPreset(RestorationSettings.FadedSlide);

  private void OnPresetSubtleClick(object? sender, RoutedEventArgs e)
    => this.ApplyPreset(RestorationSettings.SubtleCleanup);

  private void OnResetClick(object? sender, RoutedEventArgs e)
    => this.ApplyPreset(new RestorationSettings());

  /// <summary>Push the preset onto every slider/checkbox/combo and re-render the preview.</summary>
  private void ApplyPreset(RestorationSettings preset) {
    this._suppressEvents = true;
    try {
      if (this.FindControl<Slider>("FaceSlider") is { } fs)    fs.Value = preset.FaceRestoreStrength * 100;
      if (this.FindControl<Slider>("DenoiseSlider") is { } ds) ds.Value = preset.DenoiseStrength    * 100;
      if (this.FindControl<Slider>("ColourSlider") is { } cs)  cs.Value = preset.RecolourStrength   * 100;
      if (this.FindControl<CheckBox>("AutoToneBox") is { } at) at.IsChecked = preset.AutoTone;
      if (this.FindControl<ComboBox>("UpscaleCombo") is { } uc)
        uc.SelectedIndex = preset.UpscaleFactor switch {
          >= 64 => 4,
          >= 16 => 3,
          >= 4 => 2,
          2 => 1,
          _ => 0
        };
    } finally {
      this._suppressEvents = false;
    }
    this.SchedulePreviewUpdate();
  }

  // ---------- Save / close ----------

  private async void OnSaveAsClick(object? sender, RoutedEventArgs e) {
    if (this._fullResolutionSource is null || this._sourceFile is null) {
      this.SetStatus("Nothing to save yet.");
      return;
    }

    var topLevel = TopLevel.GetTopLevel(this);
    if (topLevel?.StorageProvider is not { CanSave: true } storage) {
      this.SetStatus("Picker unavailable.");
      return;
    }

    var stem = Path.GetFileNameWithoutExtension(this._sourceFile.Name);
    var picked = await storage.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save restored photo as JPEG",
      SuggestedFileName = $"{stem} - restored.jpg",
      FileTypeChoices = [new FilePickerFileType("JPEG") { Patterns = ["*.jpg"] }]
    });
    if (picked is null) return;
    var destPath = picked.TryGetLocalPath();
    if (string.IsNullOrWhiteSpace(destPath)) {
      this.SetStatus("Picked path is unreadable.");
      return;
    }

    this.SetStatus("Rendering full-resolution restoration…");
    this.SetOverlayVisible(true);
    if (this.FindControl<TextBlock>("ProgressText") is { } pt)
      pt.Text = "Save As — running full-resolution pipeline…";
    var settings = this.ReadSettings();
    var faces = this._faces;
    var fullSrc = this._fullResolutionSource.Clone();
    try {
      var result = await Task.Run(() => RestorationPipeline.Apply(fullSrc, faces, settings));
      try {
        await result.SaveAsJpegAsync(destPath);
        this.SetStatus($"Saved {Path.GetFileName(destPath)}.");
      } finally {
        result.Dispose();
      }
    } catch (Exception ex) {
      this.SetStatus($"Save failed: {ex.Message}");
    } finally {
      fullSrc.Dispose();
      this.SetOverlayVisible(false);
    }
  }

  private async void OnOpenModelsClick(object? sender, RoutedEventArgs e) {
    var window = new ModelDownloadWindow();
    await window.ShowDialog(this);
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) {
    var prev = Interlocked.Exchange(ref this._previewCts, null);
    prev?.Cancel();
    prev?.Dispose();
    this._previewSource?.Dispose();
    this._fullResolutionSource?.Dispose();
    this._maskImage?.Dispose();
    this.Close();
  }

  // ---------- Synchronised zoom + pan ----------

  private void OnPreviewWheel(object? sender, PointerWheelEventArgs e) {
    if (sender is not Visual visual)
      return;
    var pos = e.GetPosition(visual);
    var oldZoom = this._zoom;
    var step = e.Delta.Y > 0 ? 1.2 : 1.0 / 1.2;
    var newZoom = Math.Clamp(oldZoom * step, 0.1, 32.0);
    if (Math.Abs(newZoom - oldZoom) < 1e-6)
      return;

    // Keep the world point under the cursor stationary on screen.
    this._panX = pos.X - (pos.X - this._panX) * (newZoom / oldZoom);
    this._panY = pos.Y - (pos.Y - this._panY) * (newZoom / oldZoom);
    this._zoom = newZoom;
    this.ApplyTransform();
    e.Handled = true;
  }

  private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e) {
    if (sender is not Avalonia.Controls.Image image)
      return;
    var props = e.GetCurrentPoint(image).Properties;

    // Right-button → pan (existing behaviour, both panes synced).
    if (props.IsRightButtonPressed) {
      this._isPanning = true;
      this._panStart = e.GetPosition(image);
      e.Pointer.Capture(image);
      e.Handled = true;
      return;
    }

    // Left-button → paint to mask. Only acts on the SOURCE preview —
    // the user can still left-click on the restored side without
    // accidentally drawing.
    if (props.IsLeftButtonPressed && image.Name == "SourcePreview") {
      if (this._previewSource is null)
        return;
      this.EnsureMask();
      this._isPainting = true;
      e.Pointer.Capture(image);
      this.PaintAtScreenPos(image, e.GetPosition(image));
      e.Handled = true;
    }
  }

  private void OnPreviewPointerMoved(object? sender, PointerEventArgs e) {
    if (sender is not Avalonia.Controls.Image image)
      return;
    if (this._isPanning) {
      var pos = e.GetPosition(image);
      this._panX += pos.X - this._panStart.X;
      this._panY += pos.Y - this._panStart.Y;
      this._panStart = pos;
      this.ApplyTransform();
      e.Handled = true;
      return;
    }
    if (this._isPainting && image.Name == "SourcePreview") {
      this.PaintAtScreenPos(image, e.GetPosition(image));
      e.Handled = true;
    }
  }

  private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e) {
    if (this._isPanning) {
      this._isPanning = false;
      e.Pointer.Capture(null);
      e.Handled = true;
      return;
    }
    if (this._isPainting) {
      this._isPainting = false;
      e.Pointer.Capture(null);
      this.RefreshMaskOverlayBitmap();
      e.Handled = true;
    }
  }

  // ---------- Brush mask painting ----------

  private void EnsureMask() {
    if (this._previewSource is null)
      return;
    if (this._maskImage is null
        || this._maskImage.Width != this._previewSource.Width
        || this._maskImage.Height != this._previewSource.Height) {
      this._maskImage?.Dispose();
      this._maskImage = new Image<Rgba32>(this._previewSource.Width, this._previewSource.Height);
    }
  }

  /// <summary>
  /// Convert <paramref name="screenPos"/> on the source Image control into
  /// the corresponding pixel coordinate inside the source bitmap, then
  /// stamp a brush-sized red disc into the mask. Inverts the same
  /// Stretch=Uniform geometry + RenderTransform we use for display.
  /// </summary>
  private void PaintAtScreenPos(Avalonia.Controls.Image image, Avalonia.Point screenPos) {
    if (this._previewSource is null || this._maskImage is null)
      return;

    // Stretch=Uniform: bitmap is centred, scaled to fit, with letterbox
    // padding on the smaller axis. Compute the base mapping…
    var ctrlW = image.Bounds.Width;
    var ctrlH = image.Bounds.Height;
    if (ctrlW <= 0 || ctrlH <= 0)
      return;
    var imgW = this._previewSource.Width;
    var imgH = this._previewSource.Height;
    var baseScale = Math.Min(ctrlW / imgW, ctrlH / imgH);
    var letterboxX = (ctrlW - imgW * baseScale) / 2.0;
    var letterboxY = (ctrlH - imgH * baseScale) / 2.0;

    // … then peel off the RenderTransform (zoom + pan).
    var px = (screenPos.X - this._panX - letterboxX) / (baseScale * this._zoom);
    var py = (screenPos.Y - this._panY - letterboxY) / (baseScale * this._zoom);

    var radius = Math.Max(1, this._brushSize);
    var cx = (int)Math.Round(px);
    var cy = (int)Math.Round(py);
    StampDisc(this._maskImage, cx, cy, radius);
    this.RefreshMaskOverlayBitmap();
  }

  /// <summary>
  /// Set every pixel within <paramref name="radius"/> of (cx, cy) to a
  /// fully-opaque red — the mask uses R as the "inpaint here" channel
  /// and Alpha as the display opacity for the semi-transparent overlay.
  /// </summary>
  private static void StampDisc(Image<Rgba32> mask, int cx, int cy, int radius) {
    var minY = Math.Max(0, cy - radius);
    var maxY = Math.Min(mask.Height - 1, cy + radius);
    var minX = Math.Max(0, cx - radius);
    var maxX = Math.Min(mask.Width - 1, cx + radius);
    var r2 = radius * radius;

    mask.ProcessPixelRows(accessor => {
      for (var y = minY; y <= maxY; y++) {
        var dy = y - cy;
        var row = accessor.GetRowSpan(y);
        for (var x = minX; x <= maxX; x++) {
          var dx = x - cx;
          if (dx * dx + dy * dy <= r2)
            row[x] = new Rgba32((byte)255, (byte)0, (byte)0, (byte)200);
        }
      }
    });
  }

  private void RefreshMaskOverlayBitmap() {
    if (this._maskImage is null)
      return;
    using var ms = new MemoryStream();
    this._maskImage.SaveAsPng(ms);
    ms.Position = 0;
    var bmp = new Bitmap(ms);
    if (this.FindControl<Avalonia.Controls.Image>("MaskOverlay") is { } overlay)
      overlay.Source = bmp;
  }

  private void OnBrushSizeChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressEvents) return;
    if (sender is Slider slider) {
      this._brushSize = (int)slider.Value;
      if (this.FindControl<TextBlock>("BrushSizeValue") is { } label)
        label.Text = this._brushSize.ToString(CultureInfo.InvariantCulture);
    }
  }

  private void OnClearMaskClick(object? sender, RoutedEventArgs e) {
    if (this._maskImage is null)
      return;
    this._maskImage.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = default;
      }
    });
    this.RefreshMaskOverlayBitmap();
    this.SetStatus("Mask cleared.");
  }

  /// <summary>
  /// Run LaMa over the source's masked region, replace the source with
  /// the inpainted result, clear the mask, re-render the restored side.
  /// We replace BOTH <see cref="_previewSource"/> and
  /// <see cref="_fullResolutionSource"/> so a subsequent Save As renders
  /// from the inpainted full-res copy. (For very large images we'd
  /// ideally re-run LaMa at full resolution; for now we upscale the
  /// inpainted preview back to source dimensions — Save As at full
  /// resolution is a future improvement.)
  /// </summary>
  private async void OnInpaintClick(object? sender, RoutedEventArgs e) {
    if (this._previewSource is null) {
      this.SetStatus("Open a photo first.");
      return;
    }
    if (this._maskImage is null || !this.MaskHasContent()) {
      this.SetStatus("Paint over the damaged region first (left-click drag on the source).");
      return;
    }
    if (!await ModelPrompt.EnsureInstalledAsync(this, ModelRegistry.LamaInpaint, "Inpainting"))
      return;

    this.SetStatus("Inpainting…");
    this.SetOverlayVisible(true);
    if (this.FindControl<TextBlock>("ProgressText") is { } pt)
      pt.Text = "Inpainting masked region…";

    var src = this._previewSource;
    var mask = this._maskImage;

    Image<Rgba32>? result = null;
    try {
      result = await Task.Run(() => {
        using var inpainter = new OnnxInpainter();
        return inpainter.IsAvailable ? inpainter.Inpaint(src, mask) : null;
      });
    } catch (Exception ex) {
      this.SetStatus($"Inpainting failed: {ex.Message}");
      this.SetOverlayVisible(false);
      return;
    }
    if (result is null) {
      this.SetStatus("Inpainting failed (model returned no result).");
      this.SetOverlayVisible(false);
      return;
    }

    // Swap the source out for the inpainted version. The full-res copy
    // gets the same treatment — the inpainted preview is bicubic-resized
    // up to the source's native resolution. Coarse but visually consistent.
    this._previewSource.Dispose();
    this._previewSource = result;
    this.UpdateSourceBitmap();

    if (this._fullResolutionSource is not null) {
      var fullW = this._fullResolutionSource.Width;
      var fullH = this._fullResolutionSource.Height;
      var resizedFull = result.Clone(c => c.Resize(fullW, fullH));
      this._fullResolutionSource.Dispose();
      this._fullResolutionSource = resizedFull;
    }

    this.OnClearMaskClick(this, new RoutedEventArgs());
    this.SetStatus("Inpainted. The repaired image is now the source for the rest of the restoration.");
    this.SetOverlayVisible(false);
    this.SchedulePreviewUpdate();
  }

  private bool MaskHasContent() {
    if (this._maskImage is null) return false;
    var found = false;
    this._maskImage.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height && !found; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          if (row[x].R >= 128) { found = true; break; }
      }
    });
    return found;
  }

  private void OnPreviewDoubleTap(object? sender, TappedEventArgs e) {
    this._zoom = 1.0;
    this._panX = 0;
    this._panY = 0;
    this.ApplyTransform();
    e.Handled = true;
  }

  /// <summary>
  /// Apply the same scale + translate transform to both source and
  /// restored Image controls so they pan/zoom in lock-step. NearestNeighbor
  /// rendering is preserved during the transform — no extra interpolation
  /// is added on top of what RenderOptions configures.
  /// </summary>
  private void ApplyTransform() {
    var transform = new TransformGroup();
    transform.Children.Add(new ScaleTransform(this._zoom, this._zoom));
    transform.Children.Add(new TranslateTransform(this._panX, this._panY));

    if (this.FindControl<Avalonia.Controls.Image>("SourcePreview") is { } src)
      src.RenderTransform = transform;
    if (this.FindControl<Avalonia.Controls.Image>("RestoredPreview") is { } rst)
      rst.RenderTransform = transform;
    if (this.FindControl<Avalonia.Controls.Image>("MaskOverlay") is { } mask)
      mask.RenderTransform = transform;
    if (this.FindControl<TextBlock>("ZoomLabel") is { } label)
      label.Text = $"{this._zoom * 100:0}%";
  }

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = message;
  }
}
