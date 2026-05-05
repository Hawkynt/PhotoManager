using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using PhotoManager.Core;
using PhotoManager.Core.Detection;
using PhotoManager.Core.Develop;
using PhotoManager.Core.Faces;
using PhotoManager.Core.Models;
using PhotoManager.Core.Segmentation;
using PhotoManager.UI.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
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
  // The "working" copies — modified in place by Inpaint, Despeckle,
  // Apply-colorize, etc. The pipeline preview runs over these so each
  // commit-action becomes the new starting point for the next stage.
  private Image<Rgba32>? _previewSource;        // downscaled — drives the preview pipeline
  private Image<Rgba32>? _fullResolutionSource;  // full-res — used by Save As

  // The "original" — what the user loaded, never modified. Always shown
  // on the LEFT preview pane so the user can see the un-touched starting
  // point alongside the work-in-progress on the right, even after a
  // dozen Inpaint / Despeckle / Apply iterations.
  private Image<Rgba32>? _originalPreviewSource;
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

  // Undo / redo of mask edits. Each entry is a clone of the mask state
  // BEFORE a stroke / replace. null entries mean "mask was empty before".
  // Capped at 20 to bound memory; the oldest entry is dropped when we hit
  // the cap. New mask-mutating actions clear the redo side.
  private readonly LinkedList<Image<Rgba32>?> _maskUndo = new();
  private readonly LinkedList<Image<Rgba32>?> _maskRedo = new();
  private const int MaskHistoryCap = 20;

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
      Populate(this.FindControl<ComboBox>("FaceModelCombo"),     ModelRegistry.FaceRestorers);
      Populate(this.FindControl<ComboBox>("DenoiseModelCombo"),  ModelRegistry.Denoisers);
      Populate(this.FindControl<ComboBox>("ArtifactModelCombo"), ModelRegistry.ArtifactRemovers);
      Populate(this.FindControl<ComboBox>("InpaintModelCombo"),  ModelRegistry.Inpainters);
      Populate(this.FindControl<ComboBox>("ColourModelCombo"),   ModelRegistry.Colorizers);
      Populate(this.FindControl<ComboBox>("UpscaleModelCombo"),  ModelRegistry.Upscalers);
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

  private async void OnArtifactModelChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressEvents) return;
    await this.PromptIfMissing(sender, ModelRegistry.ArtifactRemovers, this.ReadSettings().ArtifactRemoveStrength, "AI de-artifact");
    this.SchedulePreviewUpdate();
  }

  private async void OnInpaintModelChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressEvents) return;
    // The Inpaint button always operates on a non-empty mask, but the
    // strength concept doesn't apply — there's no slider gating it. Pass
    // a non-zero "strength" so PromptIfMissing actually fires the install
    // prompt the first time the user touches the picker.
    await this.PromptIfMissing(sender, ModelRegistry.Inpainters, 1.0, "AI inpaint");
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
      // Keep an immutable copy of the as-loaded preview for the LEFT
      // pane — Inpaint / Despeckle / Apply-* will mutate _previewSource
      // but never this. Cloning keeps the two buffers independent.
      this._originalPreviewSource = image.Clone();
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
        this.RefreshFaceBoxesOverlay();
      });
    });

    this.SchedulePreviewUpdate();
  }

  /// <summary>
  /// Paints the LEFT pane from <see cref="_originalPreviewSource"/> —
  /// always the un-touched as-loaded image, regardless of how many
  /// Inpaint / Despeckle / Apply-* commits have updated the working
  /// source. So the user sees the starting point for visual comparison.
  /// </summary>
  private void UpdateSourceBitmap() {
    if (this._originalPreviewSource is null) return;
    using var ms = new MemoryStream();
    this._originalPreviewSource.SaveAsJpeg(ms);
    ms.Position = 0;
    var bmp = new Bitmap(ms);
    if (this.FindControl<Avalonia.Controls.Image>("SourcePreview") is { } img)
      img.Source = bmp;
  }

  // ---------- Settings → preview ----------

  private RestorationSettings ReadSettings() {
    var face = (this.FindControl<Slider>("FaceSlider")?.Value ?? 0) / 100.0;
    var denoise = (this.FindControl<Slider>("DenoiseSlider")?.Value ?? 0) / 100.0;
    var artifact = (this.FindControl<Slider>("ArtifactSlider")?.Value ?? 0) / 100.0;
    var colour = (this.FindControl<Slider>("ColourSlider")?.Value ?? 0) / 100.0;
    // NumericUpDown stores the multiplier directly (1.00–20.00) so no /100 conversion.
    var chromaBoost = (double?)this.FindControl<NumericUpDown>("ChromaBoostUpDown")?.Value ?? 1.60;
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
      ArtifactRemoveStrength: artifact,
      RecolourStrength: colour,
      AutoTone: autoTone,
      UpscaleFactor: upscaleFactor,
      // RestorationSettings doesn't carry a face-model field (GFPGAN only,
      // for now); the others mirror what the develop window stores.
      DenoiseModel: PickedFileName(this.FindControl<ComboBox>("DenoiseModelCombo"), ModelRegistry.Denoisers),
      ArtifactRemoveModel: PickedFileName(this.FindControl<ComboBox>("ArtifactModelCombo"), ModelRegistry.ArtifactRemovers),
      ColorizeModel: PickedFileName(this.FindControl<ComboBox>("ColourModelCombo"), ModelRegistry.Colorizers),
      UpscaleModel: PickedFileName(this.FindControl<ComboBox>("UpscaleModelCombo"), ModelRegistry.Upscalers),
      ChromaBoost: chromaBoost
    );
  }

  private void RefreshValueLabels() {
    if (this.FindControl<TextBlock>("FaceValue") is { } fv && this.FindControl<Slider>("FaceSlider") is { } fs)
      fv.Text = ((int)fs.Value).ToString(CultureInfo.InvariantCulture);
    if (this.FindControl<TextBlock>("DenoiseValue") is { } dv && this.FindControl<Slider>("DenoiseSlider") is { } ds)
      dv.Text = ((int)ds.Value).ToString(CultureInfo.InvariantCulture);
    if (this.FindControl<TextBlock>("ArtifactValue") is { } av && this.FindControl<Slider>("ArtifactSlider") is { } a_s)
      av.Text = ((int)a_s.Value).ToString(CultureInfo.InvariantCulture);
    if (this.FindControl<TextBlock>("ColourValue") is { } cv && this.FindControl<Slider>("ColourSlider") is { } cs)
      cv.Text = ((int)cs.Value).ToString(CultureInfo.InvariantCulture);
    // The Saturation × NumericUpDown and the Upscale × ComboBox already
    // display their own value in their box; the previously-mirrored side
    // labels (ChromaBoostValue / UpscaleValue) were removed from the
    // AXAML to cut redundant chrome.
  }

  private void OnSettingsChangedSlider(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressEvents) return;
    this.SchedulePreviewUpdate();
  }

  /// <summary>NumericUpDown's ValueChanged signature differs from Slider's
  /// (decimal? in/out vs double), so it gets its own handler. Same effect:
  /// re-render the preview when the user dials chroma up/down.</summary>
  private void OnChromaBoostChanged(object? sender, NumericUpDownValueChangedEventArgs e) {
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
    if (settings.AutoTone)                  stages.Add("auto-tone");
    if (settings.DenoiseStrength > 0)       stages.Add("denoise");
    if (settings.ArtifactRemoveStrength > 0) stages.Add("de-artifact");
    if (settings.RecolourStrength > 0)      stages.Add("recolour");
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
      if (this.FindControl<Slider>("FaceSlider") is { } fs)     fs.Value = preset.FaceRestoreStrength    * 100;
      if (this.FindControl<Slider>("DenoiseSlider") is { } ds)  ds.Value = preset.DenoiseStrength        * 100;
      if (this.FindControl<Slider>("ArtifactSlider") is { } as_) as_.Value = preset.ArtifactRemoveStrength * 100;
      if (this.FindControl<Slider>("ColourSlider") is { } cs)   cs.Value = preset.RecolourStrength       * 100;
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
    this._originalPreviewSource?.Dispose();
    this._fullResolutionSource?.Dispose();
    this._maskImage?.Dispose();
    this.ClearMaskHistory();
    this.Close();
  }

  // ---------- Synchronised zoom + pan ----------

  private void OnPreviewWheel(object? sender, PointerWheelEventArgs e) {
    if (sender is not Visual visual)
      return;
    // Read the cursor in the image's parent space (no RenderTransform applied
    // there). e.GetPosition(visual) on a transformed visual would inverse
    // our own zoom/pan and we'd end up double-correcting.
    var pos = e.GetPosition(VisualParentOrSelf(visual));
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
      this._panStart = e.GetPosition(VisualParentOrSelf(image));
      e.Pointer.Capture(image);
      e.Handled = true;
      return;
    }

    // Left-button → paint to mask. Only acts on the RESTORED (right)
    // preview — the LEFT pane shows the immutable as-loaded original for
    // comparison and must stay paint-free. The mask is in _previewSource
    // pixel coordinates, which match regardless of which pane is clicked.
    if (props.IsLeftButtonPressed && image.Name == "RestoredPreview") {
      if (this._previewSource is null)
        return;
      this.EnsureMask();
      this.PushMaskUndo();
      this._isPainting = true;
      e.Pointer.Capture(image);
      this.PaintAtScreenPos(image, e);
      e.Handled = true;
    }
  }

  private void OnPreviewPointerMoved(object? sender, PointerEventArgs e) {
    if (sender is not Avalonia.Controls.Image image)
      return;
    var parentPos = e.GetPosition(VisualParentOrSelf(image));
    if (this._isPanning) {
      this._panX += parentPos.X - this._panStart.X;
      this._panY += parentPos.Y - this._panStart.Y;
      this._panStart = parentPos;
      this.ApplyTransform();
      e.Handled = true;
      return;
    }
    if (this._isPainting && image.Name == "RestoredPreview") {
      this.PaintAtScreenPos(image, e);
      e.Handled = true;
    }
    if (image.Name == "RestoredPreview")
      this.UpdateBrushCursor(parentPos);
  }

  private static Visual VisualParentOrSelf(Visual visual)
    => visual.GetVisualParent() as Visual ?? visual;

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
  /// Convert a pointer event on the Image control into the corresponding
  /// pixel coordinate inside the source bitmap, then stamp a brush-sized
  /// red disc into the mask.
  ///
  /// We use <c>TransformToVisual</c> against the window so the matrix
  /// composition handles every transform on the path — image's
  /// RenderTransform, layout offsets, ancestor transforms — without us
  /// having to know which of those Avalonia inverts on its own. The
  /// resulting cursor is in the image's pre-RenderTransform local space,
  /// where Stretch=Uniform letterboxing applies directly.
  /// </summary>
  private void PaintAtScreenPos(Avalonia.Controls.Image image, PointerEventArgs e) {
    if (this._previewSource is null || this._maskImage is null)
      return;

    var localCursor = CursorInImageLocal(image, e);
    if (localCursor is not { } local)
      return;

    var ctrlW = image.Bounds.Width;
    var ctrlH = image.Bounds.Height;
    if (ctrlW <= 0 || ctrlH <= 0)
      return;
    var imgW = this._previewSource.Width;
    var imgH = this._previewSource.Height;
    var baseScale = Math.Min(ctrlW / imgW, ctrlH / imgH);
    var letterboxX = (ctrlW - imgW * baseScale) / 2.0;
    var letterboxY = (ctrlH - imgH * baseScale) / 2.0;

    var px = (local.X - letterboxX) / baseScale;
    var py = (local.Y - letterboxY) / baseScale;

    var radius = Math.Max(1, this._brushSize);
    var cx = (int)Math.Round(px);
    var cy = (int)Math.Round(py);
    StampDisc(this._maskImage, cx, cy, radius);
    this.RefreshMaskOverlayBitmap();
  }

  /// <summary>
  /// Return the cursor position in <paramref name="image"/>'s local
  /// coordinate space BEFORE its RenderTransform — the space where
  /// Stretch=Uniform layout has placed the bitmap with letterbox padding.
  /// Robust against any combination of layout offset / ancestor
  /// transforms / RenderTransform on the path.
  /// </summary>
  private static Avalonia.Point? CursorInImageLocal(Visual image, PointerEventArgs e) {
    var top = TopLevel.GetTopLevel(image);
    if (top is null) return null;
    var cursorTop = e.GetPosition(top);
    var imageToTop = image.TransformToVisual(top);
    if (imageToTop is null) return null;
    if (!imageToTop.Value.TryInvert(out var topToImage)) return null;
    return topToImage.Transform(cursorTop);
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

  /// <summary>
  /// Push a snapshot of the current mask onto the undo stack and clear
  /// the redo stack. Call BEFORE every mask mutation (paint stroke,
  /// auto-detect, clear). null = "mask was empty".
  /// </summary>
  private void PushMaskUndo() {
    var snapshot = this._maskImage?.Clone();
    this._maskUndo.AddLast(snapshot);
    while (this._maskUndo.Count > MaskHistoryCap) {
      this._maskUndo.First!.Value?.Dispose();
      this._maskUndo.RemoveFirst();
    }
    foreach (var img in this._maskRedo)
      img?.Dispose();
    this._maskRedo.Clear();
  }

  /// <summary>Drop the entire undo+redo history — call when the working
  /// source changes (Inpaint, Despeckle, Apply) since the old mask is
  /// no longer meaningful against the new source dimensions / contents.</summary>
  private void ClearMaskHistory() {
    foreach (var img in this._maskUndo) img?.Dispose();
    this._maskUndo.Clear();
    foreach (var img in this._maskRedo) img?.Dispose();
    this._maskRedo.Clear();
  }

  private void OnUndoMaskClick(object? sender, RoutedEventArgs e) {
    if (this._maskUndo.Count == 0) {
      this.SetStatus("Nothing to undo.");
      return;
    }
    this._maskRedo.AddLast(this._maskImage);
    var prev = this._maskUndo.Last!.Value;
    this._maskUndo.RemoveLast();
    this._maskImage = prev;
    this.RefreshMaskOverlayBitmap();
    this.SetStatus($"Undid mask change ({this._maskUndo.Count} more available).");
  }

  private void OnRedoMaskClick(object? sender, RoutedEventArgs e) {
    if (this._maskRedo.Count == 0) {
      this.SetStatus("Nothing to redo.");
      return;
    }
    this._maskUndo.AddLast(this._maskImage);
    var next = this._maskRedo.Last!.Value;
    this._maskRedo.RemoveLast();
    this._maskImage = next;
    this.RefreshMaskOverlayBitmap();
    this.SetStatus($"Redid mask change ({this._maskRedo.Count} more available).");
  }

  /// <summary>
  /// Move the on-screen brush-radius indicator to follow the cursor over
  /// the right pane and resize it to match the brush radius scaled by
  /// the current zoom × Stretch=Uniform baseScale (so it visually equals
  /// the painted footprint regardless of zoom level).
  /// </summary>
  private void UpdateBrushCursor(Avalonia.Point parentPos) {
    var ellipse = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("BrushCursor");
    if (ellipse is null || this._previewSource is null)
      return;
    if (this.FindControl<Avalonia.Controls.Image>("RestoredPreview") is not { } image)
      return;
    var ctrlW = image.Bounds.Width;
    var ctrlH = image.Bounds.Height;
    if (ctrlW <= 0 || ctrlH <= 0)
      return;
    var baseScale = Math.Min(ctrlW / this._previewSource.Width, ctrlH / this._previewSource.Height);
    var screenRadius = this._brushSize * baseScale * this._zoom;
    var diameter = Math.Max(2.0, 2 * screenRadius);
    ellipse.Width = diameter;
    ellipse.Height = diameter;
    ellipse.Margin = new Avalonia.Thickness(parentPos.X - screenRadius, parentPos.Y - screenRadius, 0, 0);
    ellipse.IsVisible = true;
  }

  private void OnPreviewPointerEntered(object? sender, PointerEventArgs e) {
    if (sender is Avalonia.Controls.Image image && image.Name == "RestoredPreview")
      this.UpdateBrushCursor(e.GetPosition(VisualParentOrSelf(image)));
  }

  private void OnPreviewPointerExited(object? sender, PointerEventArgs e) {
    if (this.FindControl<Avalonia.Controls.Shapes.Ellipse>("BrushCursor") is { } ellipse)
      ellipse.IsVisible = false;
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

  /// <summary>
  /// Render the detected face bounding boxes into a transparent bitmap at
  /// <see cref="_previewSource"/>'s pixel resolution, then push it into
  /// the FaceBoxOverlay Image. The overlay shares the right pane's
  /// Stretch=Uniform geometry + RenderTransform, so the rectangles pan
  /// and zoom in lock-step with the working image. Call after face
  /// detection finishes and after every source-mutating op (Inpaint,
  /// Despeckle, Apply-*) since dimensions may change with upscale.
  /// </summary>
  private void RefreshFaceBoxesOverlay() {
    var control = this.FindControl<Avalonia.Controls.Image>("FaceBoxOverlay");
    if (control is null)
      return;
    if (this._previewSource is null || this._faces.Count == 0) {
      control.Source = null;
      return;
    }

    var w = this._previewSource.Width;
    var h = this._previewSource.Height;
    using var overlay = new Image<Rgba32>(w, h);
    var pen = SixLabors.ImageSharp.Drawing.Processing.Pens.Solid(
      SixLabors.ImageSharp.Color.Lime, Math.Max(2f, w / 320f));
    overlay.Mutate(ctx => {
      foreach (var face in this._faces) {
        var fx = (int)Math.Round(face.X * w);
        var fy = (int)Math.Round(face.Y * h);
        var fw = (int)Math.Round(face.Width * w);
        var fh = (int)Math.Round(face.Height * h);
        if (fw < 2 || fh < 2)
          continue;
        var rect = new SixLabors.ImageSharp.Drawing.RectangularPolygon(fx, fy, fw, fh);
        ctx.Draw(pen, rect);
      }
    });
    using var ms = new MemoryStream();
    overlay.SaveAsPng(ms);
    ms.Position = 0;
    control.Source = new Bitmap(ms);
  }

  private void OnBrushSizeChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressEvents) return;
    if (sender is Slider slider) {
      this._brushSize = (int)slider.Value;
      if (this.FindControl<TextBlock>("BrushSizeValue") is { } label)
        label.Text = this._brushSize.ToString(CultureInfo.InvariantCulture);
    }
  }

  /// <summary>
  /// Auto-detect scratches and pre-fill the brush mask. Tries the
  /// BOPB neural-net detector first (if installed) — substantially
  /// better than the classical Frangi filter on faint damage across
  /// textured backgrounds. Falls back to Frangi when BOPB isn't
  /// installed so the button always does something useful.
  /// </summary>
  private async void OnAutoDetectScratchesClick(object? sender, RoutedEventArgs e) {
    if (this._previewSource is null) {
      this.SetStatus("Open a photo first.");
      return;
    }

    this.SetStatus("Detecting scratches…");
    var src = this._previewSource;
    Image<Rgba32>? detected = null;
    string detectorUsed;

    // Prefer BOPB (neural net) when available — better at faint damage
    // across textured backgrounds. Falls back to the pure-C# Frangi
    // ridge filter when the model isn't installed yet.
    if (ModelRegistry.BopbScratchDetector.IsInstalled()) {
      detected = await Task.Run(() => {
        using var bopb = new OnnxScratchDetectorBOPB();
        return bopb.IsAvailable ? bopb.Detect(src) : null;
      });
      detectorUsed = "BOPB neural net";
    } else {
      detectorUsed = "classical Frangi";
    }

    // Fallback (BOPB unavailable, or its session failed to open).
    detected ??= await Task.Run(() => ScratchDetector.Detect(src));

    this.PushMaskUndo();
    this._maskImage?.Dispose();
    this._maskImage = detected;
    this.RefreshMaskOverlayBitmap();

    long count = 0;
    detected.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          if (row[x].R >= 128) count++;
      }
    });
    var pct = 100.0 * count / (src.Width * (long)src.Height);
    var hint = ModelRegistry.BopbScratchDetector.IsInstalled()
      ? string.Empty
      : "  (Install the BOPB scratch detector via Models… for substantially better results.)";
    this.SetStatus(count == 0
      ? $"No obvious scratches detected ({detectorUsed}) — paint over damage manually with the brush.{hint}"
      : $"Auto-detected {pct:0.0}% of pixels as scratches via {detectorUsed}. Refine with the brush, then click Inpaint.{hint}");
  }

  /// <summary>
  /// Run the salt-and-pepper adaptive median filter over the source.
  /// Same modify-source semantics as Inpaint — the cleaned image
  /// replaces the source so subsequent restoration stages benefit
  /// from the cleaner input. Pure C#, no model needed.
  /// </summary>
  private async void OnDespeckleClick(object? sender, RoutedEventArgs e) {
    if (this._previewSource is null) {
      this.SetStatus("Open a photo first.");
      return;
    }
    this.SetStatus("Despeckling…");
    this.SetOverlayVisible(true);
    if (this.FindControl<TextBlock>("ProgressText") is { } pt)
      pt.Text = "Adaptive median filter — removing salt & pepper noise…";

    var src = this._previewSource;
    Image<Rgba32> result;
    try {
      result = await Task.Run(() => SaltAndPepperFilter.Filter(src));
    } catch (Exception ex) {
      this.SetStatus($"Despeckle failed: {ex.Message}");
      this.SetOverlayVisible(false);
      return;
    }

    this._previewSource.Dispose();
    this._previewSource = result;
    this.ClearMaskHistory();
    this.RefreshFaceBoxesOverlay();
    this.UpdateSourceBitmap();

    if (this._fullResolutionSource is not null) {
      // Despeckle the full-res too so Save As gets the cleaned image.
      // Run off-thread; status overlay shows so the user knows it's
      // still busy on the bigger image.
      if (this.FindControl<TextBlock>("ProgressText") is { } pt2)
        pt2.Text = "Despeckling at full resolution…";
      var full = this._fullResolutionSource;
      var fullCleaned = await Task.Run(() => SaltAndPepperFilter.Filter(full));
      this._fullResolutionSource.Dispose();
      this._fullResolutionSource = fullCleaned;
    }

    this.SetStatus("Despeckled. The cleaned image is now the source for the rest of the restoration.");
    this.SetOverlayVisible(false);
    this.SchedulePreviewUpdate();
  }

  /// <summary>
  /// Repeatedly run detect → inpaint until the detector finds essentially
  /// nothing OR no further progress is being made between iterations.
  /// Convergence is by mask-coverage threshold (default 0.3% of pixels),
  /// not by a fixed iteration count — heavily-damaged scans can need
  /// 8–15 iterations, lightly-damaged ones converge in 1–2.
  /// </summary>
  private async void OnAutoLoopScratchClick(object? sender, RoutedEventArgs e) {
    if (this._previewSource is null) {
      this.SetStatus("Open a photo first.");
      return;
    }
    var convergenceThresholdPct = (double?)this.FindControl<NumericUpDown>("AutoLoopThreshold")?.Value ?? 0.3;
    var hardCapIterations = (int)((double?)this.FindControl<NumericUpDown>("AutoLoopMaxIter")?.Value ?? 5);
    const double noProgressDeltaPct = 0.05;       // stop when iteration-over-iteration improvement is below this

    this.SetOverlayVisible(true);
    var totalPixels = (long)this._previewSource.Width * this._previewSource.Height;
    var prevPct = double.PositiveInfinity;
    var iter = 0;

    while (iter < hardCapIterations) {
      iter++;
      if (this.FindControl<TextBlock>("ProgressText") is { } pt)
        pt.Text = $"Auto-loop scratch removal — iteration {iter} (last mask {(double.IsPositiveInfinity(prevPct) ? "—" : $"{prevPct:0.00}%")} → target <{convergenceThresholdPct}%)…";

      // Detect.
      var src = this._previewSource;
      Image<Rgba32>? detectedMask = null;
      try {
        if (ModelRegistry.BopbScratchDetector.IsInstalled())
          detectedMask = await Task.Run(() => {
            using var bopb = new OnnxScratchDetectorBOPB();
            return bopb.IsAvailable ? bopb.Detect(src) : null;
          });
        detectedMask ??= await Task.Run(() => ScratchDetector.Detect(src));
      } catch (Exception ex) {
        this.SetStatus($"Auto-loop failed during detect: {ex.Message}");
        this.SetOverlayVisible(false);
        return;
      }

      long maskedPixels = 0;
      detectedMask.ProcessPixelRows(a => {
        for (var y = 0; y < a.Height; y++) {
          var row = a.GetRowSpan(y);
          for (var x = 0; x < row.Length; x++)
            if (row[x].R >= 128) maskedPixels++;
        }
      });
      var pct = 100.0 * maskedPixels / totalPixels;

      // Convergence: detector found essentially nothing → stop.
      if (pct < convergenceThresholdPct) {
        detectedMask.Dispose();
        this.SetStatus($"Auto-loop converged after {iter - 1} iteration(s) — residual mask {pct:0.00}% < {convergenceThresholdPct}% threshold.");
        break;
      }

      // No-progress safety stop: if iteration-over-iteration improvement
      // is tiny, the detector is just oscillating on residual noise. Stop
      // before we burn another ~10s of inpainting on no real damage.
      if (!double.IsPositiveInfinity(prevPct) && (prevPct - pct) < noProgressDeltaPct) {
        detectedMask.Dispose();
        this.SetStatus($"Auto-loop stopped after {iter - 1} iteration(s) — last two iterations differ by <{noProgressDeltaPct}% (no further progress). Residual mask {pct:0.00}%.");
        break;
      }
      prevPct = pct;

      // Update mask overlay so the user sees progress.
      this._maskImage?.Dispose();
      this._maskImage = detectedMask;
      this.RefreshMaskOverlayBitmap();

      // Inpaint.
      try {
        var working = this._previewSource;
        var mask = this._maskImage;
        var inpainted = await Task.Run(() => {
          using var inpainter = new OnnxInpainter();
          return inpainter.IsAvailable ? inpainter.Inpaint(working, mask) : null;
        });
        if (inpainted is null) {
          this.SetStatus("Auto-loop failed: LaMa inpainter not installed. Use Models… to download it.");
          this.SetOverlayVisible(false);
          return;
        }

        this._previewSource.Dispose();
        this._previewSource = inpainted;
        if (this._fullResolutionSource is not null) {
          var fullW = this._fullResolutionSource.Width;
          var fullH = this._fullResolutionSource.Height;
          var resizedFull = inpainted.Clone(c => c.Resize(fullW, fullH));
          this._fullResolutionSource.Dispose();
          this._fullResolutionSource = resizedFull;
        }

        this.ClearMaskInPlace();
        this.ClearMaskHistory();
        this.RefreshFaceBoxesOverlay();
      } catch (Exception ex) {
        this.SetStatus($"Auto-loop failed during inpaint: {ex.Message}");
        this.SetOverlayVisible(false);
        return;
      }

      if (iter == hardCapIterations)
        this.SetStatus($"Auto-loop hit safety cap of {hardCapIterations} iterations — residual mask {pct:0.00}%. Something is preventing convergence; consider painting the remainder manually.");
    }

    this.SetOverlayVisible(false);
    this.SchedulePreviewUpdate();
  }

  // ---------- "Apply" buttons — bake one AI stage into the working source ----------
  // Same modify-source semantics as Inpaint / Despeckle: run a single
  // restoration stage at the slider's current strength, swap the result
  // into _previewSource + _fullResolutionSource, then reset the slider so
  // re-running the preview pipeline doesn't double-apply the same stage.

  private async void OnApplyFaceClick(object? sender, RoutedEventArgs e) {
    var strength = (this.FindControl<Slider>("FaceSlider")?.Value ?? 0) / 100.0;
    if (strength <= 1e-6) {
      this.SetStatus("Set the face slider above 0 first.");
      return;
    }
    if (this._faces.Count == 0) {
      this.SetStatus("No faces detected — nothing to apply.");
      return;
    }
    await this.BakeStage(
      "face restore",
      s => s with { FaceRestoreStrength = strength },
      "FaceSlider"
    );
  }

  private async void OnApplyDenoiseClick(object? sender, RoutedEventArgs e) {
    var strength = (this.FindControl<Slider>("DenoiseSlider")?.Value ?? 0) / 100.0;
    if (strength <= 1e-6) {
      this.SetStatus("Set the denoise slider above 0 first.");
      return;
    }
    await this.BakeStage(
      "denoise",
      s => s with { DenoiseStrength = strength },
      "DenoiseSlider"
    );
  }

  private async void OnApplyArtifactClick(object? sender, RoutedEventArgs e) {
    var strength = (this.FindControl<Slider>("ArtifactSlider")?.Value ?? 0) / 100.0;
    if (strength <= 1e-6) {
      this.SetStatus("Set the de-artifact slider above 0 first.");
      return;
    }
    await this.BakeStage(
      "de-artifact",
      s => s with { ArtifactRemoveStrength = strength },
      "ArtifactSlider"
    );
  }

  private async void OnApplyColourClick(object? sender, RoutedEventArgs e) {
    var strength = (this.FindControl<Slider>("ColourSlider")?.Value ?? 0) / 100.0;
    if (strength <= 1e-6) {
      this.SetStatus("Set the recolour slider above 0 first.");
      return;
    }
    await this.BakeStage(
      "recolour",
      s => s with { RecolourStrength = strength },
      "ColourSlider"
    );
  }

  /// <summary>
  /// Run the restoration pipeline with everything OFF except the one
  /// stage <paramref name="stageBuilder"/> turns on, swap the result into
  /// the working source (preview + full-res), then reset the slider so
  /// the live preview pipeline doesn't re-apply the same stage on top.
  /// </summary>
  private async Task BakeStage(string stageName, Func<RestorationSettings, RestorationSettings> stageBuilder, string sliderName) {
    if (this._previewSource is null) {
      this.SetStatus("Open a photo first.");
      return;
    }

    // Cancel any in-flight live preview so it doesn't overwrite our result.
    var prev = Interlocked.Exchange(ref this._previewCts, null);
    prev?.Cancel();
    prev?.Dispose();

    this.SetOverlayVisible(true);
    if (this.FindControl<TextBlock>("ProgressText") is { } pt)
      pt.Text = $"Applying {stageName}…";
    this.SetStatus($"Applying {stageName}…");

    var faces = this._faces;
    var stageSettings = stageBuilder(new RestorationSettings(
      DenoiseModel: PickedFileName(this.FindControl<ComboBox>("DenoiseModelCombo"), ModelRegistry.Denoisers),
      ArtifactRemoveModel: PickedFileName(this.FindControl<ComboBox>("ArtifactModelCombo"), ModelRegistry.ArtifactRemovers),
      ColorizeModel: PickedFileName(this.FindControl<ComboBox>("ColourModelCombo"), ModelRegistry.Colorizers),
      UpscaleModel: PickedFileName(this.FindControl<ComboBox>("UpscaleModelCombo"), ModelRegistry.Upscalers)
    ));

    var previewSrc = this._previewSource.Clone();
    var fullSrc = this._fullResolutionSource?.Clone();

    Image<Rgba32>? previewResult = null;
    Image<Rgba32>? fullResult = null;
    try {
      previewResult = await Task.Run(() => RestorationPipeline.Apply(previewSrc, faces, stageSettings));
      if (fullSrc is not null)
        fullResult = await Task.Run(() => RestorationPipeline.Apply(fullSrc, faces, stageSettings));
    } catch (Exception ex) {
      previewResult?.Dispose();
      fullResult?.Dispose();
      this.SetStatus($"Apply {stageName} failed: {ex.Message}");
      this.SetOverlayVisible(false);
      return;
    } finally {
      previewSrc.Dispose();
      fullSrc?.Dispose();
    }

    this._previewSource.Dispose();
    this._previewSource = previewResult;
    if (fullResult is not null) {
      this._fullResolutionSource?.Dispose();
      this._fullResolutionSource = fullResult;
    }
    this.ClearMaskHistory();
    this.RefreshFaceBoxesOverlay();

    // Reset the slider so the live preview pipeline doesn't re-apply the
    // same stage on top of the now-baked result. Suppress events so the
    // assignment doesn't trigger an immediate preview rebuild — we'll
    // schedule one explicitly below.
    this._suppressEvents = true;
    try {
      if (this.FindControl<Slider>(sliderName) is { } slider)
        slider.Value = 0;
    } finally {
      this._suppressEvents = false;
    }

    this.SetOverlayVisible(false);
    this.SetStatus($"{stageName} applied. The result is now the source for the rest of the restoration.");
    this.SchedulePreviewUpdate();
  }

  private void OnClearMaskClick(object? sender, RoutedEventArgs e) {
    if (this._maskImage is null)
      return;
    this.PushMaskUndo();
    this.ClearMaskInPlace();
    this.SetStatus("Mask cleared.");
  }

  /// <summary>Wipe the mask without touching the undo stack — used after
  /// source-mutating ops (Inpaint / Despeckle / Apply) where the prior
  /// mask is meaningless against the new source.</summary>
  private void ClearMaskInPlace() {
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
    if (this.FindControl<Avalonia.Controls.Image>("FaceBoxOverlay") is { } faces)
      faces.RenderTransform = transform;
    if (this.FindControl<TextBlock>("ZoomLabel") is { } label)
      label.Text = $"{this._zoom * 100:0}%";
  }

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = message;
  }
}
