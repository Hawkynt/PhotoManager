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
  // Immutable source copies — set once at Load time and never mutated.
  // Every preview / Save As re-runs the pipeline end-to-end against
  // these inputs, so the rendered output is always a deterministic
  // function of (source, settings, brush-mask). No order-dependent
  // bake state — the user's "harsh recolor after Apply X" case can't
  // happen because Apply X no longer exists.
  private Image<Rgba32>? _previewSource;        // downscaled — drives the preview pipeline
  private Image<Rgba32>? _fullResolutionSource;  // full-res — used by Save As

  // Per-stage output cache — populated by the pipeline, reused across
  // SchedulePreviewUpdate calls. When the user moves a slider, only
  // the changed stage and its downstream stages re-execute; earlier
  // stage outputs are blitted in from this cache. BindToSource clears
  // the cache when the source image identity changes (file load).
  // Disposed on window close.
  private readonly RestorationPipelineCache _previewCache = new();
  // Save As needs its own cache because it operates on the full-res
  // source (different image identity than the preview's downscaled
  // version). Kept around so successive Save As calls with the same
  // settings hit the cache.
  private readonly RestorationPipelineCache _saveCache = new();
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

  // Undo / redo of mask edits. Each ENTRY describes one rectangular
  // tile of the mask: Bounds + the rect's old-pixel and new-pixel
  // snapshots (row-major within Bounds). One mutation pushes an
  // ARRAY of entries — a brush stroke uses one entry per disc stamp;
  // a sparse merge / clear emits one entry per run of consecutive
  // changed pixels in a row, so a row with five scattered scratches
  // costs five tiny 1×1 entries instead of one 1×4096 row.
  //
  // Memory is bounded by the actual changed-pixel footprint plus a
  // small rect-padding overhead (a 41×41 stamp stores 1681 pixels;
  // the disc inside is 1257 — ~25% padding). Capped at 20 mutations;
  // the oldest is dropped when full. Apply forward = write NewPixels
  // at Bounds; apply reverse = write OldPixels. Stamps inside one
  // mutation are reverse-iterated on undo so overlapping discs
  // unwind back to the true pre-stroke state.
  private readonly record struct MaskDeltaEntry(Rectangle Bounds, Rgba32[] OldPixels, Rgba32[] NewPixels);
  private readonly LinkedList<MaskDeltaEntry[]> _maskUndo = new();
  private readonly LinkedList<MaskDeltaEntry[]> _maskRedo = new();
  private const int MaskHistoryCap = 20;

  // Per-stroke accumulator: each PaintAtScreenPos call appends one
  // MaskDeltaEntry covering its disc's bounding rect (with old/new
  // pixel snapshots taken in-place inside StampDisc). PointerReleased
  // flushes the list as a single multi-entry delta so undo unwinds
  // the whole drag, not just the last stamp.
  private List<MaskDeltaEntry>? _strokeStamps;

  // Per-stroke mode: PAINT (sets disc pixels to red) or ERASE (clears
  // disc pixels). Determined at PointerPressed by sampling the mask
  // under the cursor — if it's already painted there, the user wants
  // to erase; otherwise paint. This makes a single brush behave like
  // both pen and eraser without a separate mode toggle.
  private bool _strokeIsErase;

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
  /// Paints the LEFT pane from <see cref="_previewSource"/> — the
  /// un-touched as-loaded image. The pipeline never mutates this
  /// buffer; the LEFT pane is therefore the user's reference point and
  /// changes only when a new file is loaded.
  /// </summary>
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
      ChromaBoost: chromaBoost,
      AutoScratchRemoval: this.FindControl<CheckBox>("AutoScratchInPipelineBox")?.IsChecked == true,
      AutoScratchMaxIterations: (int)((double?)this.FindControl<NumericUpDown>("AutoLoopMaxIter")?.Value ?? 5),
      AutoScratchThresholdPct: (double?)this.FindControl<NumericUpDown>("AutoLoopThreshold")?.Value ?? 0.3,
      DespeckleStrength: this.FindControl<CheckBox>("DespeckleBox")?.IsChecked == true ? 1.0 : 0.0
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
    var maskHasContent = this.MaskHasContent();
    if (settings.IsIdentity && !maskHasContent && settings.DespeckleStrength <= 1e-6) {
      this.PaintRestored(this._previewSource);
      this.SetOverlayVisible(false);
      return;
    }

    var stages = new List<string>();
    if (settings.AutoTone)                  stages.Add("auto-tone");
    if (settings.DenoiseStrength > 0)       stages.Add("denoise");
    if (settings.ArtifactRemoveStrength > 0) stages.Add("de-artifact");
    if (maskHasContent)                      stages.Add("brush inpaint");
    if (settings.AutoScratchRemoval)        stages.Add("auto-scratch");
    if (settings.DespeckleStrength > 0)     stages.Add("despeckle");
    if (settings.RecolourStrength > 0)      stages.Add("recolour");
    if (settings.FaceRestoreStrength > 0) stages.Add("face restore");
    if (settings.UpscaleFactor > 1)       stages.Add($"{settings.UpscaleFactor}× upscale");
    if (this.FindControl<TextBlock>("ProgressText") is { } pt)
      pt.Text = $"Restoring · {string.Join(" + ", stages)}…";
    this.SetOverlayVisible(true);

    var cts = new CancellationTokenSource();
    this._previewCts = cts;
    var sourceClone = this._previewSource.Clone();
    var maskClone = maskHasContent ? this._maskImage!.Clone() : null;
    var captured = settings;
    var capturedFaces = this._faces;

    var stageProgress = new Progress<PhotoManager.Core.Develop.StageProgress>(
      p => this.OnStageProgress(p));
    Exception? capturedError = null;
    // Bind the cache to the live _previewSource (NOT the clone — we
    // want reference identity that's stable across renders, so the
    // cache only invalidates when a new file is loaded). The pipeline
    // reads cached entries by cumulative settings hash; bind only
    // protects against source-image change.
    this._previewCache.BindToSource(this._previewSource);
    var pipelineCache = this._previewCache;
    _ = Task.Run(() => {
      try {
        using (sourceClone)
        using (maskClone)
          return RestorationPipeline.Apply(sourceClone, capturedFaces, captured, cts.Token, stageProgress, maskClone, pipelineCache);
      } catch (OperationCanceledException) { return null; }
        catch (Exception ex) { capturedError = ex; return null; }
    }, cts.Token).ContinueWith(t => {
      var result = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
      Avalonia.Threading.Dispatcher.UIThread.Post(() => {
        if (cts.IsCancellationRequested || !ReferenceEquals(this._previewCts, cts)) {
          result?.Dispose();
          return;
        }
        if (result is null) {
          this.SetOverlayVisible(false);
          // Surface the real exception (DDColor inference failure,
          // OpenVINO device contention, missing model file, etc.)
          // instead of the previous generic "models not installed".
          this.SetStatus(capturedError is { } err
            ? $"Restoration failed: {err.Message}"
            : "Restoration failed — check that the required ONNX models are installed.");
          return;
        }
        this.PaintRestored(result);
        // Diagnostic: when the pipeline thinks it ran a colorize stage
        // but the result still looks gray, the cause is somewhere
        // between the model's predicted chroma and the displayed
        // bitmap. Sampling the actual pipeline output's grayness here
        // — together with DDColor's last-inference chroma magnitude —
        // narrows down which leg is dropping the color.
        var diag = string.Empty;
        if (settings.AutoScratchRemoval) {
          diag += $"  [auto-scratch: {PhotoManager.Core.Develop.AutoScratchPipeline.LastDiagnostic}]";
        }
        if (settings.RecolourStrength > 0) {
          var grayPct = ComputeGrayPercent(result);
          var meanAb = PhotoManager.Core.Segmentation.OnnxColorizerDDColor.LastInferenceMeanAbsAb;
          var inputStats = PhotoManager.Core.Segmentation.OnnxColorizerDDColor.LastInputStats;
          diag += $"  [recolor: chroma={meanAb:F2}, gray={grayPct}%, src={inputStats}]";
        }
        result.Dispose();
        this.SetOverlayVisible(false);
        this.SetStatus($"Preview updated.{diag}");
      });
    });
  }

  /// <summary>Sample a 10×10 grid of pixels from <paramref name="img"/> and
  /// return the percentage with R==G==B. Used to detect cases where the
  /// pipeline's recolor stage ran but the visible output is grayscale —
  /// either the model returned no chroma, or the compose blended it
  /// out, or something downstream overwrote it.</summary>
  private static int ComputeGrayPercent(Image<Rgba32> img) {
    var grayCount = 0;
    var total = 0;
    img.ProcessPixelRows(accessor => {
      var yStep = Math.Max(1, accessor.Height / 10);
      for (var y = 0; y < accessor.Height; y += yStep) {
        var row = accessor.GetRowSpan(y);
        var xStep = Math.Max(1, row.Length / 10);
        for (var x = 0; x < row.Length; x += xStep) {
          total++;
          if (row[x].R == row[x].G && row[x].G == row[x].B)
            grayCount++;
        }
      }
    });
    return total == 0 ? 0 : grayCount * 100 / total;
  }

  private void PaintRestored(Image<Rgba32> image) {
    using var ms = new MemoryStream();
    image.SaveAsJpeg(ms);
    ms.Position = 0;
    var bmp = new Bitmap(ms);
    if (this.FindControl<Avalonia.Controls.Image>("RestoredPreview") is { } img)
      img.Source = bmp;
  }

  // Live elapsed-time + ETA tracking for the progress overlay. The key is
  // taken from whatever ProgressText says when the overlay is first shown
  // (callers always set the label first, then toggle the overlay visible),
  // and the duration of each completed run is cached in _stageHistory so
  // the next time the same operation runs we can show "~M:SS remaining".
  // History persists for the lifetime of the window — wiped when the user
  // closes it. Cross-window persistence isn't needed: a 30-second job
  // runs ~30s on cold start, and after the first run the user gets ETA.
  private static readonly Dictionary<string, TimeSpan> _stageHistory = new();
  private readonly System.Diagnostics.Stopwatch _overlayStopwatch = new();
  private Avalonia.Threading.DispatcherTimer? _overlayTimer;
  private string? _overlayCurrentKey;

  private void SetOverlayVisible(bool visible) {
    if (this.FindControl<Border>("ProgressOverlay") is { } overlay)
      overlay.IsVisible = visible;
    if (visible)
      this.StartOverlayTimer();
    else
      this.StopOverlayTimer();
  }

  private void StartOverlayTimer() {
    this._overlayCurrentKey = (this.FindControl<TextBlock>("ProgressText")?.Text) ?? "operation";
    this._overlayStopwatch.Restart();
    this.UpdateOverlayTiming();
    if (this._overlayTimer is null) {
      this._overlayTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
      this._overlayTimer.Tick += (_, _) => this.UpdateOverlayTiming();
    }
    this._overlayTimer.Start();
  }

  private void StopOverlayTimer() {
    this._overlayTimer?.Stop();
    if (this._overlayStopwatch.IsRunning) {
      this._overlayStopwatch.Stop();
      // Store the actual run duration so the *next* invocation of the
      // same operation can show a meaningful ETA. We overwrite rather
      // than averaging — the most recent run is the best predictor of
      // the next one (data sizes / settings tend to be stable session-
      // to-session).
      if (this._overlayCurrentKey is { } key)
        _stageHistory[key] = this._overlayStopwatch.Elapsed;
    }
    if (this.FindControl<TextBlock>("ProgressTimingText") is { } t)
      t.Text = string.Empty;
    this._overlayCurrentKey = null;
  }

  private void UpdateOverlayTiming() {
    if (this.FindControl<TextBlock>("ProgressTimingText") is not { } t)
      return;
    var elapsed = this._overlayStopwatch.Elapsed;
    var elapsedStr = FormatMmSs(elapsed) + " elapsed";
    if (this._overlayCurrentKey is { } key && _stageHistory.TryGetValue(key, out var prev)) {
      var remaining = prev - elapsed;
      if (remaining > TimeSpan.Zero)
        t.Text = $"{elapsedStr} · ~{FormatMmSs(remaining)} remaining";
      else
        t.Text = $"{elapsedStr} · longer than last run ({FormatMmSs(prev)})";
    } else {
      t.Text = elapsedStr;
    }
  }

  private static string FormatMmSs(TimeSpan ts)
    => $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";

  // Per-stage timing memory: when a stage starts, we remember the
  // elapsed offset and (for tile-based stages) the within-stage start
  // time, so subsequent tile updates compute ETA from the stage's own
  // progress rather than the whole-pipeline elapsed.
  private string? _currentStageName;
  private TimeSpan _currentStageStartedAt;

  /// <summary>
  /// Unified stage-progress callback for the restoration pipeline.
  /// Handles tile-based stages (denoise, upscale) and pixel-rate
  /// estimated single-shot stages (artifact, recolour, faces, etc.).
  /// Stays on the UI thread because the IProgress is wrapped with
  /// Avalonia's SynchronizationContext at creation.
  /// </summary>
  private void OnStageProgress(PhotoManager.Core.Develop.StageProgress p) {
    var t = this.FindControl<TextBlock>("ProgressTimingText");
    if (t is null) return;
    var pt = this.FindControl<TextBlock>("ProgressText");
    var elapsed = this._overlayStopwatch.Elapsed;

    // Track stage transitions so within-stage ETA isn't polluted by the
    // time previous stages took.
    if (this._currentStageName != p.StageName) {
      this._currentStageName = p.StageName;
      this._currentStageStartedAt = elapsed;
      // Show the human label of the current stage in ProgressText so the
      // user always knows what's running right now.
      if (pt is not null)
        pt.Text = $"Restoring · {FriendlyStageName(p.StageName)}…";
    }

    var stageElapsed = elapsed - this._currentStageStartedAt;
    var elapsedStr = FormatMmSs(elapsed);

    string detail;
    double thisStageRemainingSeconds;
    if (p.TotalUnits > 1 && p.DoneUnits > 0) {
      // Tile-based stage with concrete progress — most accurate ETA.
      var fraction = p.DoneUnits / (double)p.TotalUnits;
      var estStageTotal = stageElapsed.TotalSeconds / fraction;
      thisStageRemainingSeconds = Math.Max(0, estStageTotal - stageElapsed.TotalSeconds);
      detail = $"patch {p.DoneUnits}/{p.TotalUnits}, {fraction * 100:0}% of {FriendlyStageName(p.StageName)}";
    } else if (p.EstimatedTotalSeconds > 0) {
      // Single-shot stage — pixel-rate estimate.
      thisStageRemainingSeconds = Math.Max(0, p.EstimatedTotalSeconds - stageElapsed.TotalSeconds);
      detail = $"{FriendlyStageName(p.StageName)} (estimated)";
    } else {
      // Stage completed (DoneUnits == TotalUnits, no estimate). Don't
      // overwrite — let the timer keep showing elapsed.
      return;
    }
    // Pipeline-spanning total: time left in current stage + already-known
    // estimate for stages that haven't started yet. Without this the user
    // sees the ETA reset to a small number every time a stage transitions,
    // which is misleading.
    var totalRemaining = TimeSpan.FromSeconds(thisStageRemainingSeconds + p.EstimatedRemainingPipelineSeconds);
    t.Text = $"{elapsedStr} elapsed · ~{FormatMmSs(totalRemaining)} remaining · {detail}";
  }

  private static string FriendlyStageName(string raw) => raw switch {
    "auto-tone" => "auto-tone",
    "denoise"   => "denoising",
    "artifact"  => "removing JPEG artifacts",
    "recolour"  => "colorising",
    "faces"     => "restoring faces",
    "inpaint"   => "inpainting",
    var s when s.StartsWith("upscale") => "upscaling " + s[7..],  // "upscale (1/2)" → "upscaling (1/2)"
    _ => raw
  };

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
    // Brush mask was painted on the preview-resolution source; resize
    // it to the full-resolution source so manual repairs land in the
    // right place at native size. Nearest-neighbour resize keeps the
    // mask binary — bicubic would smear into half-pixel transparency
    // values that confuse the LaMa "is this masked" R≥128 test.
    var fullMask = this.MaskHasContent()
      ? this._maskImage!.Clone(c => c.Resize(
          fullSrc.Width, fullSrc.Height,
          SixLabors.ImageSharp.Processing.KnownResamplers.NearestNeighbor))
      : null;
    var saveProgress = new Progress<PhotoManager.Core.Develop.StageProgress>(
      p => this.OnStageProgress(p));
    this._saveCache.BindToSource(this._fullResolutionSource);
    var saveCache = this._saveCache;
    try {
      var result = await Task.Run(() => RestorationPipeline.Apply(fullSrc, faces, settings, default, saveProgress, fullMask, saveCache));
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
      fullMask?.Dispose();
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
    this._previewCache.Dispose();
    this._saveCache.Dispose();
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

    // Left-button → paint OR erase the mask. Mode is decided here at
    // press time and held for the whole drag. If the mask under the
    // cursor is already painted (R≥128), the user means to erase
    // (drag clears that region); otherwise the user means to paint
    // (drag adds to the mask). One brush, two behaviors, no separate
    // toggle — like an eraser overlay you flip into when you drag
    // over an already-marked area.
    if (props.IsLeftButtonPressed && image.Name == "RestoredPreview") {
      if (this._previewSource is null)
        return;
      this.EnsureMask();
      this._strokeIsErase = this.MaskHasPaintAtCursor(image, e);
      // Start a per-stroke buffer of rect entries; each StampDisc call
      // appends one MaskDeltaEntry covering the disc's bounding rect.
      // PointerReleased flushes the list as a single delta so undo
      // unwinds the entire drag, not just the last stamp. Starting a
      // new mutation invalidates redo.
      this._strokeStamps = new List<MaskDeltaEntry>();
      this._maskRedo.Clear();
      this._isPainting = true;
      e.Pointer.Capture(image);
      this.PaintAtScreenPos(image, e);
      e.Handled = true;
    }
  }

  /// <summary>Sample the mask at the cursor's source-pixel coordinate
  /// to decide whether this drag should paint or erase. Returns true
  /// if the cursor is over an already-painted pixel (R≥128) — the
  /// drag will erase. False if the cursor is over an empty pixel —
  /// the drag will paint. Out-of-bounds or no-mask = false (paint).
  /// </summary>
  private bool MaskHasPaintAtCursor(Avalonia.Controls.Image image, PointerEventArgs e) {
    if (this._maskImage is null || this._previewSource is null)
      return false;
    var localCursor = CursorInImageLocal(image, e);
    if (localCursor is not { } local)
      return false;
    var ctrlW = image.Bounds.Width;
    var ctrlH = image.Bounds.Height;
    if (ctrlW <= 0 || ctrlH <= 0)
      return false;
    var imgW = this._previewSource.Width;
    var imgH = this._previewSource.Height;
    var baseScale = Math.Min(ctrlW / imgW, ctrlH / imgH);
    var letterboxX = (ctrlW - imgW * baseScale) / 2.0;
    var letterboxY = (ctrlH - imgH * baseScale) / 2.0;
    var px = (int)Math.Round((local.X - letterboxX) / baseScale);
    var py = (int)Math.Round((local.Y - letterboxY) / baseScale);
    if (px < 0 || px >= imgW || py < 0 || py >= imgH)
      return false;
    return this._maskImage[px, py].R >= 128;
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
      // Flush the per-stroke list of rect stamps onto the undo stack
      // as ONE multi-entry delta. Each entry holds the disc's bounding
      // rect with old/new pixel snapshots — overlapping stamps unwind
      // correctly because ApplyDelta walks reverse on undo.
      if (this._strokeStamps is { Count: > 0 } stamps)
        this.PushMaskDelta(stamps.ToArray());
      this._strokeStamps = null;
      this.RefreshMaskOverlayBitmap();
      // Mask is declarative pipeline state — schedule a re-render so
      // the right pane reflects the just-painted strokes.
      this.SchedulePreviewUpdate();
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
    var stamp = StampDisc(this._maskImage, cx, cy, radius, this._strokeIsErase);
    this._strokeStamps?.Add(stamp);
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
  /// OR <paramref name="addition"/> into <paramref name="dst"/> in
  /// place: any pixel with R≥128 in the source becomes a "masked here"
  /// pixel in the destination. Used by Auto-detect to add detected
  /// scratches to the user's brush strokes without wiping them.
  ///
  /// Returns the diff as ROW-RUN rect entries: per row, every maximal
  /// run of consecutive pixels that flipped from not-masked to masked
  /// becomes one 1×N rect. Sparse detection output (5 scattered
  /// scratches across a row of 4096) costs five tiny 1×1 entries
  /// rather than a single 1×4096 row that would be 99% padding.
  /// </summary>
  private static MaskDeltaEntry[] MergeMask(Image<Rgba32> dst, Image<Rgba32> addition) {
    if (dst.Width != addition.Width || dst.Height != addition.Height) {
      // Best-effort: skip the merge silently rather than throw mid-UI.
      // The detector always produces a mask at source dimensions so a
      // mismatch only happens if the user resized the working source
      // between paint and auto-detect — not a flow we currently support.
      return Array.Empty<MaskDeltaEntry>();
    }
    var addPixels = new Rgba32[addition.Width * addition.Height];
    addition.CopyPixelDataTo(addPixels);
    var w = dst.Width;
    var redPixel = MaskRedPixel;
    var entries = new List<MaskDeltaEntry>();
    dst.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var off = y * w;
        var x = 0;
        while (x < row.Length) {
          while (x < row.Length && !(addPixels[off + x].R >= 128 && row[x].R < 128))
            x++;
          if (x >= row.Length) break;
          var runStart = x;
          while (x < row.Length && addPixels[off + x].R >= 128 && row[x].R < 128)
            x++;
          var runLen = x - runStart;
          var olds = new Rgba32[runLen];
          var news = new Rgba32[runLen];
          for (var i = 0; i < runLen; i++) {
            olds[i] = row[runStart + i];
            news[i] = redPixel;
            row[runStart + i] = redPixel;
          }
          entries.Add(new MaskDeltaEntry(new Rectangle(runStart, y, runLen, 1), olds, news));
        }
      }
    });
    return entries.ToArray();
  }

  /// <summary>
  /// Stamp a disc into the mask. <paramref name="erase"/> selects the
  /// fill: false = "paint" (set disc pixels to opaque red), true =
  /// "erase" (clear disc pixels to transparent). Returns a
  /// <see cref="MaskDeltaEntry"/> covering the disc's bounding rect
  /// for undo. Same delta shape regardless of mode — only NewPixels
  /// inside the disc differs.
  /// </summary>
  private static MaskDeltaEntry StampDisc(Image<Rgba32> mask, int cx, int cy, int radius, bool erase) {
    var minY = Math.Max(0, cy - radius);
    var maxY = Math.Min(mask.Height - 1, cy + radius);
    var minX = Math.Max(0, cx - radius);
    var maxX = Math.Min(mask.Width - 1, cx + radius);
    var bw = maxX - minX + 1;
    var bh = maxY - minY + 1;
    var bounds = new Rectangle(minX, minY, bw, bh);
    var oldPixels = new Rgba32[bw * bh];
    var newPixels = new Rgba32[bw * bh];
    var r2 = radius * radius;
    var fillPixel = erase ? default(Rgba32) : MaskRedPixel;

    mask.ProcessPixelRows(accessor => {
      for (var ly = 0; ly < bh; ly++) {
        var ty = minY + ly;
        var row = accessor.GetRowSpan(ty);
        var dy = ty - cy;
        var rowOff = ly * bw;
        for (var lx = 0; lx < bw; lx++) {
          var tx = minX + lx;
          var idx = rowOff + lx;
          var old = row[tx];
          oldPixels[idx] = old;
          var dx = tx - cx;
          if (dx * dx + dy * dy <= r2) {
            row[tx] = fillPixel;
            newPixels[idx] = fillPixel;
          } else {
            newPixels[idx] = old;
          }
        }
      }
    });
    return new MaskDeltaEntry(bounds, oldPixels, newPixels);
  }

  /// <summary>The single Rgba32 value the mask uses for "inpaint here".
  /// Centralised so the diff plumbing and StampDisc / MergeMask all
  /// agree on one value.</summary>
  private static readonly Rgba32 MaskRedPixel = new(255, 0, 0, 200);

  /// <summary>
  /// Push a sparse-diff <paramref name="delta"/> onto the undo stack
  /// and clear the redo stack. Call AFTER applying a mask mutation —
  /// the delta records the (oldValue, newValue) pairs for each pixel
  /// the mutation changed. Skipping zero-change deltas keeps the stack
  /// from filling up with no-op clicks.
  /// </summary>
  private void PushMaskDelta(MaskDeltaEntry[] delta) {
    if (delta.Length == 0)
      return;
    this._maskUndo.AddLast(delta);
    while (this._maskUndo.Count > MaskHistoryCap)
      this._maskUndo.RemoveFirst();
    this._maskRedo.Clear();
  }

  /// <summary>Drop the entire undo+redo history — call when the mask
  /// dimensions change (e.g. opening a new file). Diff entries are
  /// indexed against the current mask resolution so they're meaningless
  /// across resizes.</summary>
  private void ClearMaskHistory() {
    this._maskUndo.Clear();
    this._maskRedo.Clear();
  }

  /// <summary>Apply <paramref name="delta"/> to <paramref name="mask"/>
  /// in either direction. forward=true blits NewPixels into each
  /// rectangle (re-applies); forward=false blits OldPixels (reverses).
  /// On reverse we walk entries from last to first so overlapping
  /// stamps within one stroke unwind to the true pre-stroke state.
  /// </summary>
  private static void ApplyDelta(Image<Rgba32> mask, MaskDeltaEntry[] delta, bool forward) {
    if (forward) {
      for (var i = 0; i < delta.Length; i++)
        BlitRect(mask, delta[i], forward: true);
    } else {
      for (var i = delta.Length - 1; i >= 0; i--)
        BlitRect(mask, delta[i], forward: false);
    }
  }

  /// <summary>Copy the entry's old- or new-pixel array onto the mask
  /// at <paramref name="entry"/>.Bounds. Out-of-bounds rows / columns
  /// are clipped silently — Bounds should already be image-clamped at
  /// capture time, so clipping is just defensive.</summary>
  private static void BlitRect(Image<Rgba32> mask, MaskDeltaEntry entry, bool forward) {
    var pixels = forward ? entry.NewPixels : entry.OldPixels;
    var bounds = entry.Bounds;
    var bw = bounds.Width;
    var bh = bounds.Height;
    if (bw <= 0 || bh <= 0)
      return;
    mask.ProcessPixelRows(accessor => {
      for (var ly = 0; ly < bh; ly++) {
        var ty = bounds.Y + ly;
        if (ty < 0 || ty >= accessor.Height) continue;
        var row = accessor.GetRowSpan(ty);
        var rowOff = ly * bw;
        var startX = Math.Max(0, bounds.X);
        var endX = Math.Min(row.Length, bounds.X + bw);
        for (var tx = startX; tx < endX; tx++)
          row[tx] = pixels[rowOff + (tx - bounds.X)];
      }
    });
  }

  private void OnUndoMaskClick(object? sender, RoutedEventArgs e) {
    if (this._maskUndo.Count == 0 || this._maskImage is null) {
      this.SetStatus("Nothing to undo.");
      return;
    }
    var delta = this._maskUndo.Last!.Value;
    this._maskUndo.RemoveLast();
    ApplyDelta(this._maskImage, delta, forward: false);
    this._maskRedo.AddLast(delta);
    this.RefreshMaskOverlayBitmap();
    this.SetStatus($"Undid mask change ({this._maskUndo.Count} more available).");
    this.SchedulePreviewUpdate();
  }

  private void OnRedoMaskClick(object? sender, RoutedEventArgs e) {
    if (this._maskRedo.Count == 0 || this._maskImage is null) {
      this.SetStatus("Nothing to redo.");
      return;
    }
    var delta = this._maskRedo.Last!.Value;
    this._maskRedo.RemoveLast();
    ApplyDelta(this._maskImage, delta, forward: true);
    this._maskUndo.AddLast(delta);
    this.RefreshMaskOverlayBitmap();
    this.SetStatus($"Redid mask change ({this._maskRedo.Count} more available).");
    this.SchedulePreviewUpdate();
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
    if (sender is not Avalonia.Controls.Image image || image.Name != "RestoredPreview")
      return;
    this.UpdateBrushCursor(e.GetPosition(VisualParentOrSelf(image)));
    // Mask overlay is only shown while the cursor is over the preview
    // pane — otherwise the red splotches obscure the colorize / restore
    // result the user is actually trying to evaluate.
    if (this.FindControl<Avalonia.Controls.Image>("MaskOverlay") is { } mask)
      mask.IsVisible = true;
  }

  private void OnPreviewPointerExited(object? sender, PointerEventArgs e) {
    if (this.FindControl<Avalonia.Controls.Shapes.Ellipse>("BrushCursor") is { } ellipse)
      ellipse.IsVisible = false;
    // Don't hide the mask if the user is actively painting — they need
    // to keep seeing their strokes even if the cursor briefly leaves
    // the preview's visual bounds mid-drag.
    if (this._isPainting)
      return;
    if (this.FindControl<Avalonia.Controls.Image>("MaskOverlay") is { } mask)
      mask.IsVisible = false;
  }

  private void RefreshMaskOverlayBitmap() {
    var overlay = this.FindControl<Avalonia.Controls.Image>("MaskOverlay");
    if (overlay is null)
      return;
    // Undo can land us back at "no mask ever existed" (the initial
    // snapshot is null on the undo stack). Clear the overlay so the
    // last-rendered bitmap doesn't linger on screen.
    if (this._maskImage is null) {
      overlay.Source = null;
      return;
    }
    using var ms = new MemoryStream();
    this._maskImage.SaveAsPng(ms);
    ms.Position = 0;
    overlay.Source = new Bitmap(ms);
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

    // MERGE the detected scratches INTO the existing brush mask rather
    // than replacing it. Otherwise the user's hand-drawn strokes
    // (painted to cover damage the detector missed) get clobbered the
    // moment they click Auto-detect, and the subsequent Inpaint runs
    // against detector-only pixels with the manual marks lost. The OR
    // semantics also lets the user click Auto-detect multiple times
    // (with different model parameters) and accumulate findings.
    this.EnsureMask();
    var mergeDelta = MergeMask(this._maskImage!, detected);
    detected.Dispose();
    this.PushMaskDelta(mergeDelta);
    this.RefreshMaskOverlayBitmap();

    long count = 0;
    this._maskImage!.ProcessPixelRows(a => {
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
    if (count > 0)
      this.SchedulePreviewUpdate();
  }

  /// <summary>Cancel any in-flight live-preview render. Used by the
  /// preview scheduler when a newer settings change arrives before the
  /// previous render finished — the in-flight task's cancellation token
  /// is tripped so it discards its work and exits inside ~1 tile.</summary>
  private void CancelPendingPreview() {
    var prev = Interlocked.Exchange(ref this._previewCts, null);
    prev?.Cancel();
    prev?.Dispose();
  }

  private void OnClearMaskClick(object? sender, RoutedEventArgs e) {
    if (this._maskImage is null)
      return;
    var clearDelta = ClearMaskInPlace(this._maskImage);
    this.PushMaskDelta(clearDelta);
    this.RefreshMaskOverlayBitmap();
    this.SetStatus("Mask cleared.");
    this.SchedulePreviewUpdate();
  }

  /// <summary>Zero every currently-masked pixel and return the diff
  /// so the operation can be pushed onto the undo stack. Same row-run
  /// shape as <see cref="MergeMask"/>: each row's maximal runs of
  /// previously-set pixels become 1×N rect entries. Empty masks
  /// produce an empty diff and the push becomes a no-op.</summary>
  private static MaskDeltaEntry[] ClearMaskInPlace(Image<Rgba32> mask) {
    var entries = new List<MaskDeltaEntry>();
    var emptyPixel = default(Rgba32);
    mask.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        var x = 0;
        while (x < row.Length) {
          while (x < row.Length && row[x].R < 128 && row[x].A == 0)
            x++;
          if (x >= row.Length) break;
          var runStart = x;
          while (x < row.Length && (row[x].R >= 128 || row[x].A != 0))
            x++;
          var runLen = x - runStart;
          var olds = new Rgba32[runLen];
          var news = new Rgba32[runLen];
          for (var i = 0; i < runLen; i++) {
            olds[i] = row[runStart + i];
            news[i] = emptyPixel;
            row[runStart + i] = emptyPixel;
          }
          entries.Add(new MaskDeltaEntry(new Rectangle(runStart, y, runLen, 1), olds, news));
        }
      }
    });
    return entries.ToArray();
  }

  /// <summary>
  /// "Inpaint masked" button — kept as a manual re-render trigger for
  /// the user, but it no longer mutates the source. The brush mask is
  /// part of declarative pipeline state: the live preview (and Save As)
  /// already runs LaMa over the painted regions on every render. So
  /// this button just reschedules the pipeline preview, which is
  /// useful when the user has painted strokes while a previous render
  /// was finishing and wants an explicit refresh.
  /// </summary>
  private async void OnInpaintClick(object? sender, RoutedEventArgs e) {
    if (this._previewSource is null) {
      this.SetStatus("Open a photo first.");
      return;
    }
    if (this._maskImage is null || !this.MaskHasContent()) {
      this.SetStatus("Paint over the damaged region first (left-click drag on the right pane).");
      return;
    }
    if (!await ModelPrompt.EnsureInstalledAsync(this, ModelRegistry.LamaInpaint, "Inpainting"))
      return;
    this.SchedulePreviewUpdate();
  }

  /// <summary>
  /// "Despeckle" button — turns the salt-and-pepper stage on as part of
  /// the declarative pipeline state instead of one-shot mutating the
  /// source. The pipeline always runs the cleaned filter against the
  /// un-modified loaded image so users can toggle it off and the
  /// speckles return.
  /// </summary>
  private void OnDespeckleClick(object? sender, RoutedEventArgs e) {
    if (this._previewSource is null) {
      this.SetStatus("Open a photo first.");
      return;
    }
    if (this.FindControl<CheckBox>("DespeckleBox") is { } cb)
      cb.IsChecked = true;
    this.SchedulePreviewUpdate();
  }

  /// <summary>
  /// "Auto-loop scratch removal" button — equivalent to ticking
  /// AutoScratchInPipelineBox. The detect → inpaint loop runs as a
  /// pipeline stage, so toggling it on causes the next render to
  /// include the loop's output. No mutation of _previewSource — the
  /// loop runs on the as-loaded source every time, so unchecking the
  /// pipeline box returns to the un-cleaned scan.
  /// </summary>
  private void OnAutoLoopScratchClick(object? sender, RoutedEventArgs e) {
    if (this._previewSource is null) {
      this.SetStatus("Open a photo first.");
      return;
    }
    if (this.FindControl<CheckBox>("AutoScratchInPipelineBox") is { } cb)
      cb.IsChecked = true;
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
