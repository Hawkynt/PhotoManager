using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hawkynt.PhotoManager.Core.Develop;
using Hawkynt.PhotoManager.Core.Enhance;
using Hawkynt.PhotoManager.Core.Models;
using Hawkynt.PhotoManager.Core.Segmentation;
using Hawkynt.PhotoManager.UI.Controls;
using Hawkynt.PhotoManager.UI.Models;
using Hawkynt.PhotoManager.UI.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Lightroom-lite developer. Live-preview sliders for tone (exposure /
/// contrast / highlights / shadows / whites / blacks), presence (clarity /
/// vibrance / saturation), white balance (temperature / tint), detail
/// (sharpening). Save-as renders a full-resolution JPEG via
/// <see cref="ImageDeveloper"/>. Templates persist favourite slider combos
/// to <c>%AppData%/PhotoManager/develop-templates/</c> and can be applied
/// to every file selected in the main grid in one go.
///
/// Preview uses a downscaled copy so slider drags stay responsive on
/// 50-megapixel RAWs; save renders off the full-resolution source image.
/// </summary>
public partial class EditImageWindow : Window {
  private const int PreviewMaxEdgePixels = 1024;

  private readonly FileInfo? _sourceFile;
  private readonly IReadOnlyList<FileInfo> _selectionForApplyAll;
  private readonly Action<OperationProgress?>? _setHostOperation;
  private readonly DevelopTemplateStore _templates = new();
  private Image<Rgba32>? _previewSource;  // downscaled copy for live preview
  private Image<Rgba32>? _developedPreview;  // last preview render — sampled by eyedroppers
  private Bitmap? _baselinePreview;  // baseline render with default settings, recomputed only on file load / "Reset all"
  private readonly CompareModeState _compareState = new();
  private OnnxSegmentationDetector? _subjectSegmenter;
  private DispatcherTimer? _updateTimer;
  private DevelopSettings _settings = new();
  private bool _suppressTemplateSelect;
  private bool _suppressSliderEvents;
  private bool _suppressFilmSimSelect;
  /// <summary>User's develop settings before a film-sim preset was applied, so
  /// selecting "None" can restore the original state.</summary>
  private DevelopSettings? _filmSimBaseSettings;
  private string? _activeEyedropper;
  /// <summary>0 = original (embedded XMP), N &gt; 0 = the basename.copyN.xmp sidecar this window edits.</summary>
  private int _copyIndex;
  /// <summary>Full-resolution source dimensions (before preview downscale). Used to gate AI-upscale factors against the 32K cap.</summary>
  private int _sourceFullWidth;
  private int _sourceFullHeight;

  /// <summary>List of slider name → DevelopSettings field accessor pairs so
  /// we can iterate them once for read/write/reset/dirty-track instead of
  /// 13 hand-typed FindControl calls per operation.</summary>
  private static readonly (string Slider, string Label, Func<DevelopSettings, double> Get,
                           Func<DevelopSettings, double, DevelopSettings> Set, string Format)[] SliderBindings = new[] {
    ("ExposureSlider",   "ExposureValue",    (Func<DevelopSettings, double>)(s => s.ExposureStops),     (Func<DevelopSettings, double, DevelopSettings>)((s, v) => s with { ExposureStops = v }),     "+0.0;-0.0;0.0"),
    ("ContrastSlider",   "ContrastValue",    s => s.ContrastPercent,    (s, v) => s with { ContrastPercent = v },    "+0;-0;0"),
    ("HighlightsSlider", "HighlightsValue",  s => s.HighlightsPercent,  (s, v) => s with { HighlightsPercent = v },  "+0;-0;0"),
    ("ShadowsSlider",    "ShadowsValue",     s => s.ShadowsPercent,     (s, v) => s with { ShadowsPercent = v },     "+0;-0;0"),
    ("WhitesSlider",     "WhitesValue",      s => s.WhitesPercent,      (s, v) => s with { WhitesPercent = v },      "+0;-0;0"),
    ("BlacksSlider",     "BlacksValue",      s => s.BlacksPercent,      (s, v) => s with { BlacksPercent = v },      "+0;-0;0"),
    ("ClaritySlider",    "ClarityValue",     s => s.ClarityPercent,     (s, v) => s with { ClarityPercent = v },     "+0;-0;0"),
    ("VibranceSlider",   "VibranceValue",    s => s.VibrancePercent,    (s, v) => s with { VibrancePercent = v },    "+0;-0;0"),
    ("SaturationSlider", "SaturationValue",  s => s.SaturationPercent,  (s, v) => s with { SaturationPercent = v },  "+0;-0;0"),
    ("TemperatureSlider","TemperatureValue", s => s.TemperatureShift,   (s, v) => s with { TemperatureShift = v },   "+0;-0;0"),
    ("TintSlider",       "TintValue",        s => s.TintShift,          (s, v) => s with { TintShift = v },          "+0;-0;0"),
    ("TextureSlider",    "TextureValue",     s => s.TexturePercent,     (s, v) => s with { TexturePercent = v },     "+0;-0;0"),
    ("SmoothnessSlider", "SmoothnessValue",  s => s.SmoothnessPercent,  (s, v) => s with { SmoothnessPercent = v },  "0;0;0"),
    ("SharpeningSlider", "SharpeningValue",  s => s.SharpeningAmount,   (s, v) => s with { SharpeningAmount = v },   "0;0;0"),
    ("SharpenRadiusSlider",  "SharpenRadiusValue",  s => s.SharpenRadius,  (s, v) => s with { SharpenRadius  = v },  "0.0"),
    ("SharpenDetailSlider",  "SharpenDetailValue",  s => s.SharpenDetail,  (s, v) => s with { SharpenDetail  = v },  "0;0;0"),
    ("SharpenMaskingSlider", "SharpenMaskingValue", s => s.SharpenMasking, (s, v) => s with { SharpenMasking = v },  "0;0;0"),
    ("DehazeSlider",     "DehazeValue",      s => s.DehazePercent,      (s, v) => s with { DehazePercent = v },      "+0;-0;0"),
    ("CropAngleSlider",  null,   s => s.CropAngleDegrees,   (s, v) => s with { CropAngleDegrees = v },   "+0.0;-0.0;0.0"),
    ("VignetteAmountSlider",     "VignetteAmountValue",     s => s.VignetteAmount,             (s, v) => s with { VignetteAmount = v },             "+0;-0;0"),
    ("VignetteMidpointSlider",   "VignetteMidpointValue",   s => s.VignetteMidpoint,           (s, v) => s with { VignetteMidpoint = v },           "0;0;0"),
    ("VignetteFeatherSlider",    "VignetteFeatherValue",    s => s.VignetteFeather,            (s, v) => s with { VignetteFeather = v },            "0;0;0"),
    ("VignetteRoundnessSlider",  "VignetteRoundnessValue",  s => s.VignetteRoundness,          (s, v) => s with { VignetteRoundness = v },          "+0;-0;0"),
    ("VignetteHighlightProtectSlider", "VignetteHighlightProtectValue", s => s.VignetteHighlightContrast, (s, v) => s with { VignetteHighlightContrast = v }, "0;0;0"),
    ("ColorEnhancementSlider",   "ColorEnhancementValue",   s => s.ColorEnhancement,           (s, v) => s with { ColorEnhancement = v },           "+0;-0;0"),
    ("GrainAmountSlider",        "GrainAmountValue",        s => s.GrainAmount,                (s, v) => s with { GrainAmount = v },                "0;0;0"),
    ("GrainSizeSlider",          "GrainSizeValue",          s => s.GrainSize,                  (s, v) => s with { GrainSize = v },                  "0;0;0"),
    ("GrainFreqSlider",          "GrainFreqValue",          s => s.GrainFrequency,             (s, v) => s with { GrainFrequency = v },             "0;0;0"),
    ("LumNrDetailSlider",        "LumNrDetailValue",        s => s.LuminanceNrDetail,          (s, v) => s with { LuminanceNrDetail = v },          "0;0;0"),
    ("LumNrContrastSlider",      "LumNrContrastValue",      s => s.LuminanceNrContrast,        (s, v) => s with { LuminanceNrContrast = v },        "0;0;0"),
    ("ColorNrSlider",            "ColorNrValue",            s => s.ColorNoiseReduction,        (s, v) => s with { ColorNoiseReduction = v },        "0;0;0"),
    ("ColorNrDetailSlider",      "ColorNrDetailValue",      s => s.ColorNrDetail,              (s, v) => s with { ColorNrDetail = v },              "0;0;0"),
    ("ColorNrSmoothSlider",      "ColorNrSmoothValue",      s => s.ColorNrSmoothness,          (s, v) => s with { ColorNrSmoothness = v },          "0;0;0"),
    ("SplitShHueSlider",         "SplitShHueValue",         s => s.SplitToningShadowHue,        (s, v) => s with { SplitToningShadowHue = v },        "0;0;0"),
    ("SplitShSatSlider",         "SplitShSatValue",         s => s.SplitToningShadowSaturation, (s, v) => s with { SplitToningShadowSaturation = v }, "0;0;0"),
    ("SplitHlHueSlider",         "SplitHlHueValue",         s => s.SplitToningHighlightHue,        (s, v) => s with { SplitToningHighlightHue = v },        "0;0;0"),
    ("SplitHlSatSlider",         "SplitHlSatValue",         s => s.SplitToningHighlightSaturation, (s, v) => s with { SplitToningHighlightSaturation = v }, "0;0;0"),
    ("SplitBalanceSlider",       "SplitBalanceValue",       s => s.SplitToningBalance,          (s, v) => s with { SplitToningBalance = v },          "+0;-0;0"),
    ("ParamShSlider",            "ParamShValue",            s => s.ParametricShadows,           (s, v) => s with { ParametricShadows = v },           "+0;-0;0"),
    ("ParamDarksSlider",         "ParamDarksValue",         s => s.ParametricDarks,             (s, v) => s with { ParametricDarks = v },             "+0;-0;0"),
    ("ParamLightsSlider",        "ParamLightsValue",        s => s.ParametricLights,            (s, v) => s with { ParametricLights = v },            "+0;-0;0"),
    ("ParamHlSlider",            "ParamHlValue",            s => s.ParametricHighlights,        (s, v) => s with { ParametricHighlights = v },        "+0;-0;0"),
    ("ParamShSplitSlider",       "ParamShSplitValue",       s => s.ParametricShadowSplit,       (s, v) => s with { ParametricShadowSplit = v },       "0;0;0"),
    ("ParamMidSplitSlider",      "ParamMidSplitValue",      s => s.ParametricMidtoneSplit,      (s, v) => s with { ParametricMidtoneSplit = v },      "0;0;0"),
    ("ParamHlSplitSlider",       "ParamHlSplitValue",       s => s.ParametricHighlightSplit,    (s, v) => s with { ParametricHighlightSplit = v },    "0;0;0"),
    ("LensDistortSlider",        "LensDistortValue",        s => s.LensManualDistortion,        (s, v) => s with { LensManualDistortion = v },        "+0;-0;0"),
    ("CaRSlider",                "CaRValue",                s => s.ChromaticAberrationR,        (s, v) => s with { ChromaticAberrationR = v },        "+0;-0;0"),
    ("CaBSlider",                "CaBValue",                s => s.ChromaticAberrationB,        (s, v) => s with { ChromaticAberrationB = v },        "+0;-0;0"),
    ("DefringePurpleSlider",     "DefringePurpleValue",     s => s.DefringePurpleAmount,        (s, v) => s with { DefringePurpleAmount = v },        "0;0;0"),
    ("DefringeGreenSlider",      "DefringeGreenValue",      s => s.DefringeGreenAmount,         (s, v) => s with { DefringeGreenAmount = v },         "0;0;0"),
    ("CalRedHueSlider",          "CalRedHueValue",          s => s.CalibrationRedHue,           (s, v) => s with { CalibrationRedHue = v },           "+0;-0;0"),
    ("CalRedSatSlider",          "CalRedSatValue",          s => s.CalibrationRedSaturation,    (s, v) => s with { CalibrationRedSaturation = v },    "+0;-0;0"),
    ("CalGreenHueSlider",        "CalGreenHueValue",        s => s.CalibrationGreenHue,         (s, v) => s with { CalibrationGreenHue = v },         "+0;-0;0"),
    ("CalGreenSatSlider",        "CalGreenSatValue",        s => s.CalibrationGreenSaturation,  (s, v) => s with { CalibrationGreenSaturation = v },  "+0;-0;0"),
    ("CalBlueHueSlider",         "CalBlueHueValue",         s => s.CalibrationBlueHue,          (s, v) => s with { CalibrationBlueHue = v },          "+0;-0;0"),
    ("CalBlueSatSlider",         "CalBlueSatValue",         s => s.CalibrationBlueSaturation,   (s, v) => s with { CalibrationBlueSaturation = v },   "+0;-0;0"),
    ("PerspVerticalSlider",      "PerspVerticalValue",      s => s.PerspectiveVertical,         (s, v) => s with { PerspectiveVertical = v },         "+0;-0;0"),
    ("PerspHorizontalSlider",    "PerspHorizontalValue",    s => s.PerspectiveHorizontal,       (s, v) => s with { PerspectiveHorizontal = v },       "+0;-0;0"),
    ("PerspRotateSlider",        "PerspRotateValue",        s => s.PerspectiveRotate,           (s, v) => s with { PerspectiveRotate = v },           "+0.0;-0.0;0.0"),
    ("PerspScaleSlider",         "PerspScaleValue",         s => s.PerspectiveScale,            (s, v) => s with { PerspectiveScale = v },            "0;0;0"),
    ("PerspAspectSlider",        "PerspAspectValue",        s => s.PerspectiveAspect,           (s, v) => s with { PerspectiveAspect = v },           "+0;-0;0"),
    ("PerspXSlider",             "PerspXValue",             s => s.PerspectiveX,                (s, v) => s with { PerspectiveX = v },                "+0;-0;0"),
    ("PerspYSlider",             "PerspYValue",             s => s.PerspectiveY,                (s, v) => s with { PerspectiveY = v },                "+0;-0;0"),
    ("RedGainSlider",    "RedGainValue",     s => s.RedGain,            (s, v) => s with { RedGain   = v },          "+0;-0;0"),
    ("GreenGainSlider",  "GreenGainValue",   s => s.GreenGain,          (s, v) => s with { GreenGain = v },          "+0;-0;0"),
    ("BlueGainSlider",   "BlueGainValue",    s => s.BlueGain,           (s, v) => s with { BlueGain  = v },          "+0;-0;0"),
    ("AiDenoiseSlider",  "AiDenoiseValue",   s => s.AiDenoiseStrength,  (s, v) => s with { AiDenoiseStrength = v },  "0.00"),
    ("AiColorizeSlider", "AiColorizeValue",  s => s.AiColorizeAmount,   (s, v) => s with { AiColorizeAmount = v },   "0.00"),
  };

  /// <summary>
  /// Which tone-curve channel the editor is currently displaying / mutating.
  /// Master / Red / Green / Blue. Master runs first in the develop pipeline;
  /// per-channel curves run after.
  /// </summary>
  private enum CurveChannel { Master, Red, Green, Blue }
  private CurveChannel _activeCurveChannel = CurveChannel.Master;

  /// <summary>Active HSL band edited by the Hue/Sat/Lum sliders.</summary>
  private int _activeHslBand;

  /// <summary>Active color-grading wheel: 0 Shadow / 1 Midtone / 2 Highlight / 3 Global.</summary>
  private int _activeGradeWheel;

  /// <summary>Active B&amp;W gray-mixer band edited by the Mix slider.</summary>
  private int _activeBwBand;

  /// <summary>Index of the currently selected local adjustment in the list, or -1.</summary>
  private int _activeLocalIndex = -1;

  /// <summary>Index of the active sub-mask within the active local adjustment, or -1 = primary.</summary>
  private int _activeSubMaskIndex = -1;

  public EditImageWindow() : this(null, Array.Empty<FileInfo>(), null, copyIndex: 0) { }

  public EditImageWindow(FileInfo sourceFile)
    : this(sourceFile, Array.Empty<FileInfo>(), null, copyIndex: 0) { }

  public EditImageWindow(FileInfo? sourceFile, IReadOnlyList<FileInfo> selectionForApplyAll)
    : this(sourceFile, selectionForApplyAll, null, copyIndex: 0) { }

  public EditImageWindow(FileInfo? sourceFile, IReadOnlyList<FileInfo> selectionForApplyAll, Action<OperationProgress?>? setHostOperation)
    : this(sourceFile, selectionForApplyAll, setHostOperation, copyIndex: 0) { }

  public EditImageWindow(FileInfo? sourceFile, IReadOnlyList<FileInfo> selectionForApplyAll, Action<OperationProgress?>? setHostOperation, int copyIndex) {
    // Suppress the slider/combobox-change cascade while the visual tree is
    // built. Avalonia raises SelectionChanged for ComboBox SelectedIndex set
    // in XAML, and ValueChanged for sliders whose value defaults are loaded —
    // both would otherwise fire UpdatePreview / RefreshValueLabels while the
    // tree is still half-wired and crash on null lookups.
    this._suppressSliderEvents = true;
    try {
      this.InitializeComponent();
    } finally {
      this._suppressSliderEvents = false;
    }
    this._sourceFile = sourceFile;
    this._selectionForApplyAll = selectionForApplyAll ?? Array.Empty<FileInfo>();
    this._setHostOperation = setHostOperation;
    this._copyIndex = Math.Max(0, copyIndex);
    this.UpdateCopyTitle();

    this.PopulateUpscaleModelCombo();
    this.PopulateDenoiseModelCombo();
    this.PopulateColorizeModelCombo();
    this.RefreshTemplateList();
    this.PopulateFilmSimCombo();
    this.EnsureDetectClassFlyoutPopulated();

    // Hook eyedropper sampling on the preview image.
    if (this.FindControl<Avalonia.Controls.Image>("PreviewImage") is { } preview)
      preview.PointerPressed += this.OnPreviewPointerPressed;

    // Mask overlay — drag handles for Linear / Radial, painting for Brush.
    if (this.FindControl<MaskOverlayCanvas>("MaskOverlay") is { } overlay) {
      overlay.MaskChanged         += this.OnMaskOverlayChanged;
      overlay.BrushDabAdded       += this.OnMaskOverlayBrushDab;
      overlay.BrushRadiusChanged  += this.OnMaskOverlayBrushRadiusChanged;
    }

    // Curve editor → settings round-trip. The editor never owns the
    // settings; it just yells when the user drags a point.
    if (this.FindControl<ToneCurveEditor>("CurveEditor") is { } curve)
      curve.CurveChanged += this.OnCurveChanged;

    if (this.FindControl<Avalonia.Controls.Image>("PreviewImageSlider") is { } sliderImg)
      sliderImg.SizeChanged += (_, _) => this.UpdateSliderClip();

    if (this.FindControl<CropOverlayCanvas>("CropOverlay") is { } cropOverlay)
      cropOverlay.CropChanged += this.OnCropOverlayChanged;

    this.RefreshLookList();
    this.RefreshCopiesCombo();

    if (sourceFile is not null)
      _ = this.LoadPreviewAsync();
  }

  private void OnCurveChanged(object? sender, IReadOnlyList<CurvePoint> points) {
    this._settings = this.WriteCurveForActiveChannel(this._settings, points);
    this.SchedulePreviewUpdate();
  }

  private void OnResetCurveClick(object? sender, RoutedEventArgs e) {
    if (this.FindControl<ToneCurveEditor>("CurveEditor") is { } curve)
      curve.SetCurve(null);
    this._settings = this.WriteCurveForActiveChannel(this._settings, null);
    this.UpdatePreview();
  }

  private void OnCurveChannelChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    if (sender is not ComboBox combo)
      return;
    this._activeCurveChannel = combo.SelectedIndex switch {
      1 => CurveChannel.Red,
      2 => CurveChannel.Green,
      3 => CurveChannel.Blue,
      _ => CurveChannel.Master
    };
    if (this.FindControl<ToneCurveEditor>("CurveEditor") is { } curve)
      curve.SetCurve(this.ReadCurveForActiveChannel(this._settings));
  }

  private IReadOnlyList<CurvePoint>? ReadCurveForActiveChannel(DevelopSettings s) => this._activeCurveChannel switch {
    CurveChannel.Red   => s.RedCurvePoints,
    CurveChannel.Green => s.GreenCurvePoints,
    CurveChannel.Blue  => s.BlueCurvePoints,
    _                  => s.ToneCurvePoints
  };

  private DevelopSettings WriteCurveForActiveChannel(DevelopSettings s, IReadOnlyList<CurvePoint>? points) => this._activeCurveChannel switch {
    CurveChannel.Red   => s with { RedCurvePoints   = points },
    CurveChannel.Green => s with { GreenCurvePoints = points },
    CurveChannel.Blue  => s with { BlueCurvePoints  = points },
    _                  => s with { ToneCurvePoints  = points }
  };

  private void OnCurveInterpolationChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    if (sender is not ComboBox combo)
      return;
    var mode = combo.SelectedIndex switch {
      1 => CurveInterpolation.CatmullRom,
      2 => CurveInterpolation.Bezier,
      _ => CurveInterpolation.Linear
    };
    this._settings = this._settings with { ToneCurveInterpolation = mode };
    if (this.FindControl<ToneCurveEditor>("CurveEditor") is { } curve)
      curve.SetInterpolation(mode);
    this.UpdatePreview();
  }

  // ---------- AI enhancement (denoise + upscale) ----------

  /// <summary>
  /// Fires whenever the AI denoise slider moves. The first time the slider
  /// goes above 0 we check the NAFNet ONNX is on disk; if not, we prompt
  /// the user immediately (per UI conventions) and reset the slider so they
  /// don't think the feature silently no-ops.
  /// </summary>
  private async void OnAiDenoiseChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    if (sender is not Slider slider)
      return;

    // Resolve which denoise model is active so the prompt targets the right
    // file — the picker below the slider lets the user swap between NAFNet,
    // SCUNet, etc.
    var modelIdx = this.FindControl<ComboBox>("AiDenoiseModelCombo") is { } modelCombo
      ? Math.Max(0, modelCombo.SelectedIndex)
      : 0;
    if (modelIdx >= ModelRegistry.Denoisers.Count)
      modelIdx = 0;
    var model = ModelRegistry.Denoisers[modelIdx];

    if (slider.Value > 0 && !model.IsInstalled()) {
      this._suppressSliderEvents = true;
      try { slider.Value = 0; }
      finally { this._suppressSliderEvents = false; }
      var ok = await ModelPrompt.EnsureInstalledAsync(this, model, $"AI denoise ({model.DisplayName})");
      if (!ok)
        return;
    }
    this.SchedulePreviewUpdate();
  }

  /// <summary>Seed the denoise model picker once, in <see cref="ModelRegistry.Denoisers"/> order.</summary>
  private void PopulateDenoiseModelCombo() {
    if (this.FindControl<ComboBox>("AiDenoiseModelCombo") is not { } combo)
      return;
    this._suppressSliderEvents = true;
    try {
      combo.Items.Clear();
      foreach (var model in ModelRegistry.Denoisers)
        combo.Items.Add(new ComboBoxItem { Content = model.DisplayName, Tag = model.FileName });
      combo.SelectedIndex = 0;
    } finally {
      this._suppressSliderEvents = false;
    }
  }

  /// <summary>Maps model filename → combo index for the denoise picker.</summary>
  private static int DenoiseModelToComboIndex(string? fileName) {
    var pick = string.IsNullOrWhiteSpace(fileName) ? ModelRegistry.Denoisers[0].FileName : fileName;
    for (var i = 0; i < ModelRegistry.Denoisers.Count; i++)
      if (string.Equals(ModelRegistry.Denoisers[i].FileName, pick, StringComparison.OrdinalIgnoreCase))
        return i;
    return 0;
  }

  /// <summary>
  /// Picking a different denoise model: write the model's filename onto
  /// <see cref="DevelopSettings.AiDenoiseModel"/>, prompt for download if
  /// the file is missing while the slider is non-zero, and re-render so
  /// the user sees the new model's character.
  /// </summary>
  private async void OnAiDenoiseModelChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    if (sender is not ComboBox combo)
      return;
    var idx = combo.SelectedIndex;
    if (idx < 0 || idx >= ModelRegistry.Denoisers.Count)
      return;
    var model = ModelRegistry.Denoisers[idx];

    var newName = idx == 0 ? null : model.FileName;
    this._settings = this._settings with { AiDenoiseModel = newName };

    if (this._settings.AiDenoiseStrength > 1e-6 && !model.IsInstalled())
      await ModelPrompt.EnsureInstalledAsync(this, model, $"AI denoise ({model.DisplayName})");

    this.SchedulePreviewUpdate();
  }

  // ---------- AI colorize ----------

  /// <summary>
  /// Fires whenever the AI colorize slider moves. Mirrors the denoise
  /// pattern: model-presence prompt the first time the slider goes above 0.
  /// </summary>
  private async void OnAiColorizeChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    if (sender is not Slider slider)
      return;

    var modelIdx = this.FindControl<ComboBox>("AiColorizeModelCombo") is { } modelCombo
      ? Math.Max(0, modelCombo.SelectedIndex)
      : 0;
    if (modelIdx >= ModelRegistry.Colorizers.Count)
      modelIdx = 0;
    var model = ModelRegistry.Colorizers[modelIdx];

    if (slider.Value > 0 && !model.IsInstalled()) {
      this._suppressSliderEvents = true;
      try { slider.Value = 0; }
      finally { this._suppressSliderEvents = false; }
      var ok = await ModelPrompt.EnsureInstalledAsync(this, model, $"AI colorize ({model.DisplayName})");
      if (!ok)
        return;
    }
    this.SchedulePreviewUpdate();
  }

  private void PopulateColorizeModelCombo() {
    if (this.FindControl<ComboBox>("AiColorizeModelCombo") is not { } combo)
      return;
    this._suppressSliderEvents = true;
    try {
      combo.Items.Clear();
      foreach (var model in ModelRegistry.Colorizers)
        combo.Items.Add(new ComboBoxItem { Content = model.DisplayName, Tag = model.FileName });
      combo.SelectedIndex = 0;
    } finally {
      this._suppressSliderEvents = false;
    }
  }

  private static int ColorizeModelToComboIndex(string? fileName) {
    var pick = string.IsNullOrWhiteSpace(fileName) ? ModelRegistry.Colorizers[0].FileName : fileName;
    for (var i = 0; i < ModelRegistry.Colorizers.Count; i++)
      if (string.Equals(ModelRegistry.Colorizers[i].FileName, pick, StringComparison.OrdinalIgnoreCase))
        return i;
    return 0;
  }

  private async void OnAiColorizeModelChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    if (sender is not ComboBox combo)
      return;
    var idx = combo.SelectedIndex;
    if (idx < 0 || idx >= ModelRegistry.Colorizers.Count)
      return;
    var model = ModelRegistry.Colorizers[idx];

    var newName = idx == 0 ? null : model.FileName;
    this._settings = this._settings with { AiColorizeModel = newName };

    if (this._settings.AiColorizeAmount > 1e-6 && !model.IsInstalled())
      await ModelPrompt.EnsureInstalledAsync(this, model, $"AI colorize ({model.DisplayName})");

    this.SchedulePreviewUpdate();
  }

  /// <summary>Combo-index → upscale factor. Inverse of <see cref="UpscaleFactorToComboIndex"/>.</summary>
  private static int ComboIndexToUpscaleFactor(int index) => index switch {
    1 => 2,
    2 => 4,
    3 => 16,
    4 => 64,
    _ => 1
  };

  private static int UpscaleFactorToComboIndex(int factor) => factor switch {
    >= 64 => 4,
    >= 16 => 3,
    >= 4 => 2,
    2 => 1,
    _ => 0
  };

  /// <summary>Seed the model picker once, in <see cref="ModelRegistry.Upscalers"/> order.</summary>
  private void PopulateUpscaleModelCombo() {
    if (this.FindControl<ComboBox>("AiUpscaleModelCombo") is not { } combo)
      return;
    this._suppressSliderEvents = true;
    try {
      combo.Items.Clear();
      foreach (var model in ModelRegistry.Upscalers)
        combo.Items.Add(new ComboBoxItem { Content = model.DisplayName, Tag = model.FileName });
      combo.SelectedIndex = 0;
    } finally {
      this._suppressSliderEvents = false;
    }
  }

  /// <summary>Maps model filename ("upscale.onnx", "upscale-128.onnx", ...) → combo index.</summary>
  private static int UpscaleModelToComboIndex(string? fileName) {
    var pick = string.IsNullOrWhiteSpace(fileName) ? ModelRegistry.Upscalers[0].FileName : fileName;
    for (var i = 0; i < ModelRegistry.Upscalers.Count; i++)
      if (string.Equals(ModelRegistry.Upscalers[i].FileName, pick, StringComparison.OrdinalIgnoreCase))
        return i;
    return 0;
  }

  /// <summary>
  /// Picking a different upscale model: write the model's filename onto
  /// <see cref="DevelopSettings.AiUpscaleModel"/>, prompt for download if
  /// the file is missing, and re-render the preview so the user sees the
  /// new model's character.
  /// </summary>
  private async void OnAiUpscaleModelChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    if (sender is not ComboBox combo)
      return;
    var idx = combo.SelectedIndex;
    if (idx < 0 || idx >= ModelRegistry.Upscalers.Count)
      return;
    var model = ModelRegistry.Upscalers[idx];

    // Write null when the user picks the default — keeps XMP packets small
    // and matches how prior versions of PhotoManager wrote them.
    var newName = idx == 0 ? null : model.FileName;
    this._settings = this._settings with { AiUpscaleModel = newName };

    // If the user picked a missing model AND the upscale stage is currently
    // active (factor > 1), prompt them to download it now — otherwise the
    // pipeline would silently fall through to no-op on Save As.
    if (this._settings.AiUpscaleFactor > 1 && !model.IsInstalled())
      await ModelPrompt.EnsureInstalledAsync(this, model, $"AI upscale ({model.DisplayName})");

    this.SchedulePreviewUpdate();
  }

  private async void OnAiUpscaleChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    if (sender is not ComboBox combo)
      return;
    var factor = ComboIndexToUpscaleFactor(combo.SelectedIndex);

    // Resolve which upscale model is active — picker default is index 0 if
    // unset / unrecognised. The presence prompt below targets that model.
    var modelIdx = this.FindControl<ComboBox>("AiUpscaleModelCombo") is { } modelCombo
      ? Math.Max(0, modelCombo.SelectedIndex)
      : 0;
    if (modelIdx >= ModelRegistry.Upscalers.Count)
      modelIdx = 0;
    var model = ModelRegistry.Upscalers[modelIdx];

    if (factor > 1 && !model.IsInstalled()) {
      var requestedIndex = combo.SelectedIndex;
      this._suppressSliderEvents = true;
      try { combo.SelectedIndex = 0; }
      finally { this._suppressSliderEvents = false; }
      if (this.FindControl<TextBlock>("AiUpscaleValue") is { } resetLabel)
        resetLabel.Text = "1×";
      this._settings = this._settings with { AiUpscaleFactor = 1 };
      var ok = await ModelPrompt.EnsureInstalledAsync(this, model, $"AI upscale ({model.DisplayName})");
      if (!ok)
        return;
      // Re-apply the requested factor now that the model is in place.
      this._suppressSliderEvents = true;
      try { combo.SelectedIndex = requestedIndex; }
      finally { this._suppressSliderEvents = false; }
    }

    this._settings = this._settings with { AiUpscaleFactor = factor };
    if (this.FindControl<TextBlock>("AiUpscaleValue") is { } label)
      label.Text = factor.ToString(CultureInfo.InvariantCulture) + "×";
    this.SchedulePreviewUpdate();
  }

  /// <summary>
  /// Disable combo entries whose target dimensions would exceed Avalonia /
  /// most encoder caps (32 768 px on either side) on the FULL-RESOLUTION
  /// source — that's what Save As will feed to the upscaler. Called every
  /// time a new source loads so the gating reflects the current image's
  /// pixels.
  /// </summary>
  private void RefreshUpscaleComboAvailability() {
    if (this.FindControl<ComboBox>("AiUpscaleCombo") is not { } combo)
      return;
    var w = this._sourceFullWidth;
    var h = this._sourceFullHeight;
    if (w <= 0 || h <= 0)
      return;
    // ComboBoxItems are declared inline in XAML, so they appear directly
    // in Items. ContainerFromIndex isn't reliable while the dropdown is
    // closed (containers may be virtualised), but Items is always populated.
    for (var i = 0; i < combo.Items.Count; i++) {
      if (combo.Items[i] is not ComboBoxItem item)
        continue;
      var factor = ComboIndexToUpscaleFactor(i);
      var fits = (long)w * factor <= OnnxUpscaler.MaxOutputDimension
              && (long)h * factor <= OnnxUpscaler.MaxOutputDimension;
      item.IsEnabled = fits;
    }
    // If the currently-selected factor no longer fits, snap back to 1×.
    var currentFits = (long)w * this._settings.AiUpscaleFactor <= OnnxUpscaler.MaxOutputDimension
                   && (long)h * this._settings.AiUpscaleFactor <= OnnxUpscaler.MaxOutputDimension;
    if (!currentFits) {
      this._suppressSliderEvents = true;
      try { combo.SelectedIndex = 0; }
      finally { this._suppressSliderEvents = false; }
      if (this.FindControl<TextBlock>("AiUpscaleValue") is { } label)
        label.Text = "1×";
      this._settings = this._settings with { AiUpscaleFactor = 1 };
    }
  }

  private async void OnDetectAiModelsClick(object? sender, RoutedEventArgs e) {
    var window = new ModelDownloadWindow();
    await window.ShowDialog(this);
  }

  // ---------- HSL color mixer ----------

  private void OnHslChipClick(object? sender, RoutedEventArgs e) {
    if (sender is not ToggleButton toggle || toggle.Tag is not string tag || !int.TryParse(tag, out var band))
      return;
    this._activeHslBand = band;
    // Make the chip row mutually exclusive — keep the clicked one checked.
    for (var i = 0; i < 8; i++)
      if (this.FindControl<ToggleButton>("HslChip" + i) is { } chip)
        chip.IsChecked = i == band;
    this.RefreshHslAxisSliders();
  }

  private void OnHslAxisChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    var hue = this.ReadHslSlider("HslHueSlider");
    var sat = this.ReadHslSlider("HslSatSlider");
    var lum = this.ReadHslSlider("HslLumSlider");
    this._settings = this._settings with {
      HslHueShifts        = WriteHslBand(this._settings.HslHueShifts,        this._activeHslBand, hue),
      HslSaturationShifts = WriteHslBand(this._settings.HslSaturationShifts, this._activeHslBand, sat),
      HslLuminanceShifts  = WriteHslBand(this._settings.HslLuminanceShifts,  this._activeHslBand, lum)
    };
    this.RefreshHslAxisLabels();
    this.SchedulePreviewUpdate();
  }

  private void OnResetHslClick(object? sender, RoutedEventArgs e) {
    this._settings = this._settings with {
      HslHueShifts = null, HslSaturationShifts = null, HslLuminanceShifts = null
    };
    this.RefreshHslAxisSliders();
    this.UpdatePreview();
    this.SetStatus("HSL bands reset to identity.");
  }

  private void RefreshHslAxisSliders() {
    var hue = ReadHslBand(this._settings.HslHueShifts,        this._activeHslBand);
    var sat = ReadHslBand(this._settings.HslSaturationShifts, this._activeHslBand);
    var lum = ReadHslBand(this._settings.HslLuminanceShifts,  this._activeHslBand);
    this._suppressSliderEvents = true;
    try {
      if (this.FindControl<Slider>("HslHueSlider") is { } h) h.Value = hue;
      if (this.FindControl<Slider>("HslSatSlider") is { } s) s.Value = sat;
      if (this.FindControl<Slider>("HslLumSlider") is { } l) l.Value = lum;
    } finally {
      this._suppressSliderEvents = false;
    }
    this.RefreshHslAxisLabels();
  }

  private void RefreshHslAxisLabels() {
    var inv = CultureInfo.InvariantCulture;
    if (this.FindControl<TextBlock>("HslHueValue") is { } h)
      h.Text = ((int)Math.Round(this.ReadHslSlider("HslHueSlider"))).ToString("+0;-0;0", inv);
    if (this.FindControl<TextBlock>("HslSatValue") is { } s)
      s.Text = ((int)Math.Round(this.ReadHslSlider("HslSatSlider"))).ToString("+0;-0;0", inv);
    if (this.FindControl<TextBlock>("HslLumValue") is { } l)
      l.Text = ((int)Math.Round(this.ReadHslSlider("HslLumSlider"))).ToString("+0;-0;0", inv);
  }

  private double ReadHslSlider(string name)
    => this.FindControl<Slider>(name)?.Value ?? 0;

  private static double ReadHslBand(IReadOnlyList<double>? values, int band)
    => values is { } v && band >= 0 && band < v.Count ? v[band] : 0;

  private static IReadOnlyList<double>? WriteHslBand(IReadOnlyList<double>? values, int band, double value) {
    var arr = new double[8];
    if (values is not null)
      for (var i = 0; i < arr.Length && i < values.Count; i++)
        arr[i] = values[i];
    if (band >= 0 && band < arr.Length)
      arr[band] = value;
    foreach (var v in arr)
      if (Math.Abs(v) > 1e-6) return arr;
    return null;
  }

  // ---------- B&W mixer ----------

  private void OnConvertToBwChanged(object? sender, RoutedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    var enabled = (sender as CheckBox)?.IsChecked == true;
    this._settings = this._settings with { ConvertToGrayscale = enabled };
    this.UpdatePreview();
  }

  private void OnBwChipClick(object? sender, RoutedEventArgs e) {
    if (sender is not ToggleButton toggle || toggle.Tag is not string tag || !int.TryParse(tag, out var band))
      return;
    this._activeBwBand = band;
    for (var i = 0; i < 8; i++)
      if (this.FindControl<ToggleButton>("BwChip" + i) is { } chip)
        chip.IsChecked = i == band;
    this.RefreshBwMixSlider();
  }

  private void OnBwMixChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    var mix = this.FindControl<Slider>("BwMixSlider")?.Value ?? 0;
    this._settings = this.WriteBwBand(this._settings, this._activeBwBand, mix);
    if (this.FindControl<TextBlock>("BwMixValue") is { } label)
      label.Text = ((int)Math.Round(mix)).ToString("+0;-0;0", CultureInfo.InvariantCulture);
    this.SchedulePreviewUpdate();
  }

  private void OnResetBwClick(object? sender, RoutedEventArgs e) {
    this._settings = this._settings with {
      GrayMixerRed = 0, GrayMixerOrange = 0, GrayMixerYellow = 0, GrayMixerGreen = 0,
      GrayMixerAqua = 0, GrayMixerBlue = 0, GrayMixerPurple = 0, GrayMixerMagenta = 0
    };
    this.RefreshBwMixSlider();
    this.UpdatePreview();
  }

  private void RefreshBwMixSlider() {
    var v = this.ReadBwBand(this._settings, this._activeBwBand);
    this._suppressSliderEvents = true;
    try {
      if (this.FindControl<Slider>("BwMixSlider") is { } s) s.Value = v;
    } finally {
      this._suppressSliderEvents = false;
    }
    if (this.FindControl<TextBlock>("BwMixValue") is { } label)
      label.Text = ((int)Math.Round(v)).ToString("+0;-0;0", CultureInfo.InvariantCulture);
  }

  private double ReadBwBand(DevelopSettings s, int band) => band switch {
    0 => s.GrayMixerRed,
    1 => s.GrayMixerOrange,
    2 => s.GrayMixerYellow,
    3 => s.GrayMixerGreen,
    4 => s.GrayMixerAqua,
    5 => s.GrayMixerBlue,
    6 => s.GrayMixerPurple,
    7 => s.GrayMixerMagenta,
    _ => 0
  };

  private DevelopSettings WriteBwBand(DevelopSettings s, int band, double v) => band switch {
    0 => s with { GrayMixerRed     = v },
    1 => s with { GrayMixerOrange  = v },
    2 => s with { GrayMixerYellow  = v },
    3 => s with { GrayMixerGreen   = v },
    4 => s with { GrayMixerAqua    = v },
    5 => s with { GrayMixerBlue    = v },
    6 => s with { GrayMixerPurple  = v },
    7 => s with { GrayMixerMagenta = v },
    _ => s
  };

  // ---------- Local adjustments ----------

  private void OnAddLinearLocalClick(object? sender, RoutedEventArgs e) {
    var current = (this._settings.LocalAdjustments ?? Array.Empty<LocalAdjustment>()).ToList();
    current.Add(new LocalAdjustment(
      Mask: new LocalMask(Type: LocalMaskType.Linear),
      Name: $"Linear {current.Count + 1}"));
    this._settings = this._settings with { LocalAdjustments = current };
    this.RefreshLocalAdjustmentsList(selectIndex: current.Count - 1);
    this.UpdatePreview();
  }

  private void OnAddRadialLocalClick(object? sender, RoutedEventArgs e) {
    var current = (this._settings.LocalAdjustments ?? Array.Empty<LocalAdjustment>()).ToList();
    current.Add(new LocalAdjustment(
      Mask: new LocalMask(Type: LocalMaskType.Radial),
      Name: $"Radial {current.Count + 1}"));
    this._settings = this._settings with { LocalAdjustments = current };
    this.RefreshLocalAdjustmentsList(selectIndex: current.Count - 1);
    this.UpdatePreview();
  }

  private void OnAddBrushLocalClick(object? sender, RoutedEventArgs e) {
    var current = (this._settings.LocalAdjustments ?? Array.Empty<LocalAdjustment>()).ToList();
    current.Add(new LocalAdjustment(
      Mask: new LocalMask(Type: LocalMaskType.Brush, BrushDabs: Array.Empty<BrushDab>()),
      Name: $"Brush {current.Count + 1}"));
    this._settings = this._settings with { LocalAdjustments = current };
    this.RefreshLocalAdjustmentsList(selectIndex: current.Count - 1);
    this.UpdatePreview();
  }

  // ---------- Mask overlay (drag handles + brush painting) ----------

  private void OnMaskOverlayChanged(object? sender, LocalMask updated) {
    var current = this._settings.LocalAdjustments;
    if (current is null || this._activeLocalIndex < 0 || this._activeLocalIndex >= current.Count)
      return;
    var adj = current[this._activeLocalIndex] with { Mask = updated };
    this._settings = this._settings with { LocalAdjustments = ReplaceAt(current, this._activeLocalIndex, adj) };
    this.SchedulePreviewUpdate();
  }

  private void OnMaskOverlayBrushDab(object? sender, BrushDab dab) {
    var current = this._settings.LocalAdjustments;
    if (current is null || this._activeLocalIndex < 0 || this._activeLocalIndex >= current.Count)
      return;
    var adj = current[this._activeLocalIndex];
    if (adj.Mask.Type is not LocalMaskType.Brush and not LocalMaskType.Inpaint)
      return;
    var newDabs = (adj.Mask.BrushDabs ?? Array.Empty<BrushDab>()).ToList();
    newDabs.Add(dab);
    var newMask = adj.Mask with { BrushDabs = newDabs };
    this._settings = this._settings with { LocalAdjustments = ReplaceAt(current, this._activeLocalIndex, adj with { Mask = newMask }) };
    if (this.FindControl<MaskOverlayCanvas>("MaskOverlay") is { } overlay)
      overlay.SetActiveAdjustment(this._settings.LocalAdjustments![this._activeLocalIndex]);
    this.SchedulePreviewUpdate();
  }

  private void OnMaskOverlayBrushRadiusChanged(object? sender, double newRadius) {
    // Sync the slider so the user sees the same value the canvas is using.
    this._suppressSliderEvents = true;
    try {
      if (this.FindControl<Slider>("BrushSizeSlider") is { } slider)
        slider.Value = Math.Clamp(newRadius, slider.Minimum, slider.Maximum);
      if (this.FindControl<TextBlock>("BrushSizeValue") is { } label)
        label.Text = newRadius.ToString("0.000", CultureInfo.InvariantCulture);
    } finally {
      this._suppressSliderEvents = false;
    }
  }

  private void OnBrushSizeChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressSliderEvents) return;
    var v = this.FindControl<Slider>("BrushSizeSlider")?.Value ?? 0.05;
    if (this.FindControl<TextBlock>("BrushSizeValue") is { } label)
      label.Text = v.ToString("0.000", CultureInfo.InvariantCulture);
    if (this.FindControl<MaskOverlayCanvas>("MaskOverlay") is { } overlay) {
      overlay.BrushRadius = v;
      overlay.InvalidateVisual();  // refresh the cursor preview
    }
  }

  private void OnBrushFlowChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressSliderEvents) return;
    var v = this.FindControl<Slider>("BrushFlowSlider")?.Value ?? 1;
    if (this.FindControl<TextBlock>("BrushFlowValue") is { } label)
      label.Text = v.ToString("0.00", CultureInfo.InvariantCulture);
    if (this.FindControl<MaskOverlayCanvas>("MaskOverlay") is { } overlay)
      overlay.BrushFlow = v;
  }

  private void OnBrushEraseChanged(object? sender, RoutedEventArgs e) {
    if (this._suppressSliderEvents) return;
    var erase = (sender as CheckBox)?.IsChecked == true;
    if (this.FindControl<MaskOverlayCanvas>("MaskOverlay") is { } overlay) {
      overlay.EraseMode = erase;
      overlay.InvalidateVisual();
    }
  }

  private void OnBrushClearClick(object? sender, RoutedEventArgs e) {
    var current = this._settings.LocalAdjustments;
    if (current is null || this._activeLocalIndex < 0 || this._activeLocalIndex >= current.Count)
      return;
    var adj = current[this._activeLocalIndex];
    if (adj.Mask.Type is not LocalMaskType.Brush and not LocalMaskType.Inpaint)
      return;
    var newMask = adj.Mask with { BrushDabs = Array.Empty<BrushDab>() };
    this._settings = this._settings with { LocalAdjustments = ReplaceAt(current, this._activeLocalIndex, adj with { Mask = newMask }) };
    if (this.FindControl<MaskOverlayCanvas>("MaskOverlay") is { } overlay)
      overlay.SetActiveAdjustment(this._settings.LocalAdjustments![this._activeLocalIndex]);
    this.UpdatePreview();
  }

  private void OnRemoveLocalClick(object? sender, RoutedEventArgs e) {
    if (this._activeLocalIndex < 0)
      return;
    var current = (this._settings.LocalAdjustments ?? Array.Empty<LocalAdjustment>()).ToList();
    if (this._activeLocalIndex >= current.Count)
      return;
    current.RemoveAt(this._activeLocalIndex);
    this._settings = this._settings with { LocalAdjustments = current.Count == 0 ? null : current };
    this.RefreshLocalAdjustmentsList(selectIndex: Math.Min(this._activeLocalIndex, current.Count - 1));
    this.UpdatePreview();
  }

  private void OnLocalSelected(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    if (this.FindControl<ListBox>("LocalAdjustmentsList") is { } lb)
      this._activeLocalIndex = lb.SelectedIndex;
    this._activeSubMaskIndex = -1;
    this.RefreshLocalDetailsPanel();
  }

  private void OnLocalSliderChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    var current = this._settings.LocalAdjustments;
    if (current is null || this._activeLocalIndex < 0 || this._activeLocalIndex >= current.Count)
      return;
    var adj = current[this._activeLocalIndex] with {
      Exposure         = this.FindControl<Slider>("LocalExposureSlider")?.Value     ?? 0,
      Contrast         = this.FindControl<Slider>("LocalContrastSlider")?.Value     ?? 0,
      Highlights       = this.FindControl<Slider>("LocalHighlightsSlider")?.Value   ?? 0,
      Shadows          = this.FindControl<Slider>("LocalShadowsSlider")?.Value      ?? 0,
      Saturation       = this.FindControl<Slider>("LocalSaturationSlider")?.Value   ?? 0,
      Temperature      = this.FindControl<Slider>("LocalTemperatureSlider")?.Value  ?? 0,
      Tint             = this.FindControl<Slider>("LocalTintSlider")?.Value         ?? 0,
      Clarity          = this.FindControl<Slider>("LocalClaritySlider")?.Value      ?? 0,
      Luminance        = this.FindControl<Slider>("LocalLuminanceSlider")?.Value    ?? 0,
      ToningHue        = this.FindControl<Slider>("LocalToningHueSlider")?.Value    ?? 0,
      ToningSaturation = this.FindControl<Slider>("LocalToningSatSlider")?.Value    ?? 0,
      Defringe         = this.FindControl<Slider>("LocalDefringeSlider")?.Value     ?? 0
    };
    this._settings = this._settings with { LocalAdjustments = ReplaceAt(current, this._activeLocalIndex, adj) };
    this.RefreshLocalSliderLabels();
    this.SchedulePreviewUpdate();
  }

  private void OnLocalMaskGeometryChanged(object? sender, NumericUpDownValueChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    var current = this._settings.LocalAdjustments;
    if (current is null || this._activeLocalIndex < 0 || this._activeLocalIndex >= current.Count)
      return;
    var adj = current[this._activeLocalIndex];
    var target = this.ResolveActiveMask(adj);
    LocalMask newMask = target.Type switch {
      LocalMaskType.Linear => target with {
        X0 = (double)(this.FindControl<NumericUpDown>("LinX0Box")?.Value ?? 0m),
        Y0 = (double)(this.FindControl<NumericUpDown>("LinY0Box")?.Value ?? 0m),
        X1 = (double)(this.FindControl<NumericUpDown>("LinX1Box")?.Value ?? 0m),
        Y1 = (double)(this.FindControl<NumericUpDown>("LinY1Box")?.Value ?? 0m)
      },
      LocalMaskType.Radial => target with {
        CenterX = (double)(this.FindControl<NumericUpDown>("RadCxBox")?.Value ?? 0m),
        CenterY = (double)(this.FindControl<NumericUpDown>("RadCyBox")?.Value ?? 0m),
        RadiusX = (double)(this.FindControl<NumericUpDown>("RadRxBox")?.Value ?? 0m),
        RadiusY = (double)(this.FindControl<NumericUpDown>("RadRyBox")?.Value ?? 0m)
      },
      _ => target
    };
    this._settings = this._settings with { LocalAdjustments = ReplaceAt(current, this._activeLocalIndex, this.WithActiveMask(adj, newMask)) };
    this.SchedulePreviewUpdate();
  }

  private void OnRangeMaskChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    var current = this._settings.LocalAdjustments;
    if (current is null || this._activeLocalIndex < 0 || this._activeLocalIndex >= current.Count)
      return;
    var adj = current[this._activeLocalIndex];
    var lo  = this.FindControl<Slider>("RangeLumMinSlider")?.Value     ?? 0;
    var hi  = this.FindControl<Slider>("RangeLumMaxSlider")?.Value     ?? 1;
    var ft  = this.FindControl<Slider>("RangeLumFeatherSlider")?.Value ?? 0.1;
    var hMin = this.FindControl<Slider>("RangeHueMinSlider")?.Value     ?? 0;
    var hMax = this.FindControl<Slider>("RangeHueMaxSlider")?.Value     ?? 1;
    var hFt  = this.FindControl<Slider>("RangeHueFeatherSlider")?.Value ?? 0.05;
    if (lo >= hi) lo = hi - 0.01;
    var target = this.ResolveActiveMask(adj);
    var updated = target with {
      LuminanceRangeMin = lo,
      LuminanceRangeMax = hi,
      LuminanceRangeFeather = ft,
      HueRangeMin = hMin,
      HueRangeMax = hMax,
      HueRangeFeather = hFt
    };
    var newAdj = this.WithActiveMask(adj, updated);
    this._settings = this._settings with { LocalAdjustments = ReplaceAt(current, this._activeLocalIndex, newAdj) };
    var inv = CultureInfo.InvariantCulture;
    if (this.FindControl<TextBlock>("RangeLumMinValue")     is { } a) a.Text = lo.ToString("0.00", inv);
    if (this.FindControl<TextBlock>("RangeLumMaxValue")     is { } b) b.Text = hi.ToString("0.00", inv);
    if (this.FindControl<TextBlock>("RangeLumFeatherValue") is { } c) c.Text = ft.ToString("0.00", inv);
    if (this.FindControl<TextBlock>("RangeHueMinValue")     is { } d) d.Text = hMin.ToString("0.00", inv);
    if (this.FindControl<TextBlock>("RangeHueMaxValue")     is { } ep) ep.Text = hMax.ToString("0.00", inv);
    if (this.FindControl<TextBlock>("RangeHueFeatherValue") is { } f) f.Text = hFt.ToString("0.00", inv);
    this.SchedulePreviewUpdate();
  }

  /// <summary>Returns the mask the UI is currently editing — primary or the
  /// active sub-mask if one is selected in the SubMasksList.</summary>
  private LocalMask ResolveActiveMask(LocalAdjustment adj) {
    if (this._activeSubMaskIndex < 0 || adj.SubMasks is null
        || this._activeSubMaskIndex >= adj.SubMasks.Count)
      return adj.Mask;
    return adj.SubMasks[this._activeSubMaskIndex];
  }

  /// <summary>Re-build a LocalAdjustment with the active mask replaced.</summary>
  private LocalAdjustment WithActiveMask(LocalAdjustment adj, LocalMask updated) {
    if (this._activeSubMaskIndex < 0 || adj.SubMasks is null
        || this._activeSubMaskIndex >= adj.SubMasks.Count)
      return adj with { Mask = updated };
    var copy = adj.SubMasks.ToList();
    copy[this._activeSubMaskIndex] = updated;
    return adj with { SubMasks = copy };
  }

  // ---------- Sub-masks ----------

  private void OnAddSubMaskLinearClick(object? sender, RoutedEventArgs e) => this.AppendSubMask(new LocalMask(LocalMaskType.Linear, Combine: MaskCombineOp.Subtract));
  private void OnAddSubMaskRadialClick(object? sender, RoutedEventArgs e) => this.AppendSubMask(new LocalMask(LocalMaskType.Radial, Combine: MaskCombineOp.Intersect));
  private void OnAddSubMaskBrushClick (object? sender, RoutedEventArgs e) => this.AppendSubMask(new LocalMask(LocalMaskType.Brush, BrushDabs: Array.Empty<BrushDab>(), Combine: MaskCombineOp.Subtract));

  private void AppendSubMask(LocalMask mask) {
    var current = this._settings.LocalAdjustments;
    if (current is null || this._activeLocalIndex < 0 || this._activeLocalIndex >= current.Count)
      return;
    var adj = current[this._activeLocalIndex];
    var subs = (adj.SubMasks ?? Array.Empty<LocalMask>()).ToList();
    subs.Add(mask);
    var newAdj = adj with { SubMasks = subs };
    this._settings = this._settings with { LocalAdjustments = ReplaceAt(current, this._activeLocalIndex, newAdj) };
    this._activeSubMaskIndex = subs.Count - 1;
    this.RefreshLocalDetailsPanel();
    this.UpdatePreview();
  }

  private void OnRemoveSubMaskClick(object? sender, RoutedEventArgs e) {
    if (this._activeSubMaskIndex < 0)
      return;
    var current = this._settings.LocalAdjustments;
    if (current is null || this._activeLocalIndex < 0 || this._activeLocalIndex >= current.Count)
      return;
    var adj = current[this._activeLocalIndex];
    if (adj.SubMasks is null || this._activeSubMaskIndex >= adj.SubMasks.Count)
      return;
    var subs = adj.SubMasks.ToList();
    subs.RemoveAt(this._activeSubMaskIndex);
    var newAdj = adj with { SubMasks = subs.Count == 0 ? null : subs };
    this._settings = this._settings with { LocalAdjustments = ReplaceAt(current, this._activeLocalIndex, newAdj) };
    this._activeSubMaskIndex = -1;
    this.RefreshLocalDetailsPanel();
    this.UpdatePreview();
  }

  private void OnSubMaskSelected(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    if (sender is not ListBox lb)
      return;
    // Index 0 in the listbox is the primary mask; sub-masks start at index 1.
    this._activeSubMaskIndex = lb.SelectedIndex <= 0 ? -1 : lb.SelectedIndex - 1;
    this.RefreshLocalDetailsPanel();
  }

  private void OnSubMaskOpChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    if (this._activeSubMaskIndex < 0)
      return;
    if (sender is not ComboBox combo)
      return;
    var op = combo.SelectedIndex switch {
      1 => MaskCombineOp.Subtract,
      2 => MaskCombineOp.Intersect,
      _ => MaskCombineOp.Add
    };
    var current = this._settings.LocalAdjustments;
    if (current is null || this._activeLocalIndex < 0 || this._activeLocalIndex >= current.Count)
      return;
    var adj = current[this._activeLocalIndex];
    if (adj.SubMasks is null || this._activeSubMaskIndex >= adj.SubMasks.Count)
      return;
    var newSub = adj.SubMasks[this._activeSubMaskIndex] with { Combine = op };
    var subs = adj.SubMasks.ToList();
    subs[this._activeSubMaskIndex] = newSub;
    this._settings = this._settings with { LocalAdjustments = ReplaceAt(current, this._activeLocalIndex, adj with { SubMasks = subs }) };
    this.UpdatePreview();
  }

  private void OnLocalMaskFeatherChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    var current = this._settings.LocalAdjustments;
    if (current is null || this._activeLocalIndex < 0 || this._activeLocalIndex >= current.Count)
      return;
    var adj = current[this._activeLocalIndex];
    var feather = this.FindControl<Slider>("RadFeatherSlider")?.Value ?? 50;
    if (this.FindControl<TextBlock>("RadFeatherValue") is { } label)
      label.Text = ((int)Math.Round(feather)).ToString(CultureInfo.InvariantCulture);
    var target = this.ResolveActiveMask(adj);
    this._settings = this._settings with { LocalAdjustments = ReplaceAt(current, this._activeLocalIndex, this.WithActiveMask(adj, target with { Feather = feather })) };
    this.SchedulePreviewUpdate();
  }

  private void OnLocalMaskInvertChanged(object? sender, RoutedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    var current = this._settings.LocalAdjustments;
    if (current is null || this._activeLocalIndex < 0 || this._activeLocalIndex >= current.Count)
      return;
    var adj = current[this._activeLocalIndex];
    var invert = (sender as CheckBox)?.IsChecked == true;
    var target = this.ResolveActiveMask(adj);
    this._settings = this._settings with { LocalAdjustments = ReplaceAt(current, this._activeLocalIndex, this.WithActiveMask(adj, target with { Invert = invert })) };
    this.UpdatePreview();
  }

  private static IReadOnlyList<LocalAdjustment> ReplaceAt(IReadOnlyList<LocalAdjustment> source, int index, LocalAdjustment replacement) {
    var copy = source.ToList();
    copy[index] = replacement;
    return copy;
  }

  private void RefreshLocalAdjustmentsList(int selectIndex = -1) {
    if (this.FindControl<ListBox>("LocalAdjustmentsList") is not { } lb)
      return;
    this._suppressSliderEvents = true;
    try {
      var items = (this._settings.LocalAdjustments ?? Array.Empty<LocalAdjustment>())
        .Select((a, i) => $"{i + 1}. {(string.IsNullOrWhiteSpace(a.Name) ? a.Mask.Type.ToString() : a.Name)}")
        .ToList();
      lb.ItemsSource = items;
      if (selectIndex >= 0 && selectIndex < items.Count) {
        lb.SelectedIndex = selectIndex;
        this._activeLocalIndex = selectIndex;
      } else {
        this._activeLocalIndex = lb.SelectedIndex;
      }
    } finally {
      this._suppressSliderEvents = false;
    }
    this.RefreshLocalDetailsPanel();
  }

  private void RefreshLocalDetailsPanel() {
    var current = this._settings.LocalAdjustments;
    var hasSelection = current is not null && this._activeLocalIndex >= 0 && this._activeLocalIndex < current.Count;
    if (this.FindControl<Grid>("LocalDetailsHeader") is { } hdr)   hdr.IsVisible = hasSelection;
    // The geometry rows' visibility is set per-edit-mask further down.
    LocalAdjustment? selectedAdj = hasSelection ? current![this._activeLocalIndex] : null;
    var activeMask = selectedAdj is null ? null : (LocalMask?)this.ResolveActiveMask(selectedAdj);
    var brushMaskActive = activeMask?.Type is LocalMaskType.Brush or LocalMaskType.Inpaint;

    var isInpaint = activeMask?.Type == LocalMaskType.Inpaint;
    if (this.FindControl<StackPanel>("BrushControlsRow") is { } brush) brush.IsVisible = hasSelection && brushMaskActive;
    if (this.FindControl<StackPanel>("RangeMaskRow") is { } rng) rng.IsVisible = hasSelection && !isInpaint;
    if (this.FindControl<StackPanel>("SubMasksRow") is { } subRow) subRow.IsVisible = hasSelection && !isInpaint;
    if (this.FindControl<Grid>("SubMaskOpRow") is { } subOpRow) subOpRow.IsVisible = hasSelection && this._activeSubMaskIndex >= 0 && !isInpaint;
    if (this.FindControl<StackPanel>("LocalSlidersPanel") is { } sliders) sliders.IsVisible = hasSelection && !isInpaint;

    if (this.FindControl<ListBox>("SubMasksList") is { } subListBox) {
      this._suppressSliderEvents = true;
      try {
        var items = new List<string> { "primary: " + (selectedAdj?.Mask.Type.ToString() ?? "") };
        if (selectedAdj?.SubMasks is { } subs)
          for (var i = 0; i < subs.Count; i++)
            items.Add($"  {subs[i].Combine.ToString().ToLowerInvariant()}: {subs[i].Type}");
        subListBox.ItemsSource = items;
        // Map: index 0 = primary, index 1+ = sub at i-1
        subListBox.SelectedIndex = this._activeSubMaskIndex < 0 ? 0 : this._activeSubMaskIndex + 1;
      } finally {
        this._suppressSliderEvents = false;
      }
    }
    if (this.FindControl<ComboBox>("SubMaskOpCombo") is { } opCombo && this._activeSubMaskIndex >= 0
        && selectedAdj?.SubMasks is { } sm && this._activeSubMaskIndex < sm.Count) {
      this._suppressSliderEvents = true;
      try {
        opCombo.SelectedIndex = (int)sm[this._activeSubMaskIndex].Combine;
      } finally {
        this._suppressSliderEvents = false;
      }
    }

    if (this.FindControl<MaskOverlayCanvas>("MaskOverlay") is { } overlay) {
      if (hasSelection) {
        overlay.IsVisible = true;
        overlay.IsHitTestVisible = true;
        overlay.SetActiveAdjustment(current![this._activeLocalIndex]);
        overlay.BrushMode = current[this._activeLocalIndex].Mask.Type is LocalMaskType.Brush or LocalMaskType.Inpaint;
      } else {
        overlay.IsVisible = false;
        overlay.IsHitTestVisible = false;
        overlay.SetActiveAdjustment(null);
        overlay.BrushMode = false;
      }
    }

    if (!hasSelection) return;

    var adj = current![this._activeLocalIndex];
    var editMask = activeMask ?? adj.Mask;
    if (this.FindControl<TextBlock>("LocalMaskTypeLabel") is { } label)
      label.Text = (this._activeSubMaskIndex >= 0 ? "(sub) " : "") + editMask.Type;

    // Linear / Radial geometry rows reflect the *active* mask (primary or sub-mask).
    if (this.FindControl<Grid>("LinearMaskRow") is { } linRowOuter)
      linRowOuter.IsVisible = editMask.Type == LocalMaskType.Linear;
    if (this.FindControl<StackPanel>("RadialMaskRows") is { } radRowOuter)
      radRowOuter.IsVisible = editMask.Type == LocalMaskType.Radial;

    this._suppressSliderEvents = true;
    try {
      if (editMask.Type == LocalMaskType.Linear) {
        if (this.FindControl<NumericUpDown>("LinX0Box") is { } x0) x0.Value = (decimal)editMask.X0;
        if (this.FindControl<NumericUpDown>("LinY0Box") is { } y0) y0.Value = (decimal)editMask.Y0;
        if (this.FindControl<NumericUpDown>("LinX1Box") is { } x1) x1.Value = (decimal)editMask.X1;
        if (this.FindControl<NumericUpDown>("LinY1Box") is { } y1) y1.Value = (decimal)editMask.Y1;
      } else if (editMask.Type == LocalMaskType.Radial) {
        if (this.FindControl<NumericUpDown>("RadCxBox") is { } cx) cx.Value = (decimal)editMask.CenterX;
        if (this.FindControl<NumericUpDown>("RadCyBox") is { } cy) cy.Value = (decimal)editMask.CenterY;
        if (this.FindControl<NumericUpDown>("RadRxBox") is { } rx) rx.Value = (decimal)editMask.RadiusX;
        if (this.FindControl<NumericUpDown>("RadRyBox") is { } ry) ry.Value = (decimal)editMask.RadiusY;
        if (this.FindControl<Slider>("RadFeatherSlider") is { } fs) fs.Value = editMask.Feather;
        if (this.FindControl<CheckBox>("RadInvertCheck") is { } iv) iv.IsChecked = editMask.Invert;
      }
      if (this.FindControl<Slider>("RangeLumMinSlider")     is { } rl0) rl0.Value = editMask.LuminanceRangeMin;
      if (this.FindControl<Slider>("RangeLumMaxSlider")     is { } rl1) rl1.Value = editMask.LuminanceRangeMax;
      if (this.FindControl<Slider>("RangeLumFeatherSlider") is { } rl2) rl2.Value = editMask.LuminanceRangeFeather;
      if (this.FindControl<Slider>("RangeHueMinSlider")     is { } rh0) rh0.Value = editMask.HueRangeMin;
      if (this.FindControl<Slider>("RangeHueMaxSlider")     is { } rh1) rh1.Value = editMask.HueRangeMax;
      if (this.FindControl<Slider>("RangeHueFeatherSlider") is { } rh2) rh2.Value = editMask.HueRangeFeather;
      if (this.FindControl<Slider>("LocalExposureSlider")    is { } sl0) sl0.Value = adj.Exposure;
      if (this.FindControl<Slider>("LocalContrastSlider")    is { } sl1) sl1.Value = adj.Contrast;
      if (this.FindControl<Slider>("LocalHighlightsSlider")  is { } sl2) sl2.Value = adj.Highlights;
      if (this.FindControl<Slider>("LocalShadowsSlider")     is { } sl3) sl3.Value = adj.Shadows;
      if (this.FindControl<Slider>("LocalSaturationSlider")  is { } sl4) sl4.Value = adj.Saturation;
      if (this.FindControl<Slider>("LocalTemperatureSlider") is { } sl5) sl5.Value = adj.Temperature;
      if (this.FindControl<Slider>("LocalTintSlider")        is { } sl6) sl6.Value = adj.Tint;
      if (this.FindControl<Slider>("LocalClaritySlider")     is { } sl7) sl7.Value = adj.Clarity;
      if (this.FindControl<Slider>("LocalLuminanceSlider")   is { } sl8) sl8.Value = adj.Luminance;
      if (this.FindControl<Slider>("LocalToningHueSlider")   is { } sl9) sl9.Value = adj.ToningHue;
      if (this.FindControl<Slider>("LocalToningSatSlider")   is { } sl10) sl10.Value = adj.ToningSaturation;
      if (this.FindControl<Slider>("LocalDefringeSlider")    is { } sl11) sl11.Value = adj.Defringe;
    } finally {
      this._suppressSliderEvents = false;
    }
    this.RefreshLocalSliderLabels();
  }

  private void RefreshLocalSliderLabels() {
    var inv = CultureInfo.InvariantCulture;
    void Set(string name, string format) {
      var v = this.FindControl<Slider>(name)?.Value ?? 0;
      var labelName = name.Replace("Slider", "Value");
      if (this.FindControl<TextBlock>(labelName) is { } lb)
        lb.Text = v.ToString(format, inv);
    }
    Set("LocalExposureSlider",    "+0.0;-0.0;0.0");
    Set("LocalContrastSlider",    "+0;-0;0");
    Set("LocalHighlightsSlider",  "+0;-0;0");
    Set("LocalShadowsSlider",     "+0;-0;0");
    Set("LocalSaturationSlider",  "+0;-0;0");
    Set("LocalTemperatureSlider", "+0;-0;0");
    Set("LocalTintSlider",        "+0;-0;0");
    Set("LocalClaritySlider",     "+0;-0;0");
    Set("LocalLuminanceSlider",   "+0;-0;0");
    Set("LocalToningHueSlider",   "0;0;0");
    Set("LocalToningSatSlider",   "0;0;0");
    Set("LocalDefringeSlider",    "0;0;0");
  }

  // ---------- Color Grading ----------

  private void OnGradeWheelClick(object? sender, RoutedEventArgs e) {
    if (sender is not ToggleButton toggle || toggle.Tag is not string tag || !int.TryParse(tag, out var wheel))
      return;
    this._activeGradeWheel = wheel;
    foreach (var (name, idx) in new[] {
      ("GradeWheelShadow", 0), ("GradeWheelMidtone", 1),
      ("GradeWheelHighlight", 2), ("GradeWheelGlobal", 3)
    }) {
      if (this.FindControl<ToggleButton>(name) is { } tb)
        tb.IsChecked = idx == wheel;
    }
    this.RefreshGradingSliders();
  }

  private void OnGradeAxisChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    var hue = this.FindControl<Slider>("GradeHueSlider")?.Value ?? 0;
    var sat = this.FindControl<Slider>("GradeSatSlider")?.Value ?? 0;
    var lum = this.FindControl<Slider>("GradeLumSlider")?.Value ?? 0;
    this._settings = WriteGradeWheel(this._settings, this._activeGradeWheel, hue, sat, lum);
    this.RefreshGradingLabels();
    this.SchedulePreviewUpdate();
  }

  private void OnResetGradingClick(object? sender, RoutedEventArgs e) {
    this._settings = this._settings with {
      GradeShadowHue = 0, GradeShadowSat = 0, GradeShadowLum = 0,
      GradeMidtoneHue = 0, GradeMidtoneSat = 0, GradeMidtoneLum = 0,
      GradeHighlightHue = 0, GradeHighlightSat = 0, GradeHighlightLum = 0,
      GradeGlobalHue = 0, GradeGlobalSat = 0, GradeGlobalLum = 0
    };
    this.RefreshGradingSliders();
    this.UpdatePreview();
    this.SetStatus("Color grading reset.");
  }

  private void RefreshGradingSliders() {
    var (hue, sat, lum) = ReadGradeWheel(this._settings, this._activeGradeWheel);
    this._suppressSliderEvents = true;
    try {
      if (this.FindControl<Slider>("GradeHueSlider") is { } h) h.Value = hue;
      if (this.FindControl<Slider>("GradeSatSlider") is { } s) s.Value = sat;
      if (this.FindControl<Slider>("GradeLumSlider") is { } l) l.Value = lum;
    } finally {
      this._suppressSliderEvents = false;
    }
    this.RefreshGradingLabels();
  }

  private void RefreshGradingLabels() {
    var inv = CultureInfo.InvariantCulture;
    if (this.FindControl<TextBlock>("GradeHueValue") is { } h)
      h.Text = ((int)Math.Round(this.FindControl<Slider>("GradeHueSlider")?.Value ?? 0)).ToString(inv);
    if (this.FindControl<TextBlock>("GradeSatValue") is { } s)
      s.Text = ((int)Math.Round(this.FindControl<Slider>("GradeSatSlider")?.Value ?? 0)).ToString(inv);
    if (this.FindControl<TextBlock>("GradeLumValue") is { } l)
      l.Text = ((int)Math.Round(this.FindControl<Slider>("GradeLumSlider")?.Value ?? 0)).ToString("+0;-0;0", inv);
  }

  private static (double Hue, double Sat, double Lum) ReadGradeWheel(DevelopSettings s, int wheel) => wheel switch {
    0 => (s.GradeShadowHue,    s.GradeShadowSat,    s.GradeShadowLum),
    1 => (s.GradeMidtoneHue,   s.GradeMidtoneSat,   s.GradeMidtoneLum),
    2 => (s.GradeHighlightHue, s.GradeHighlightSat, s.GradeHighlightLum),
    3 => (s.GradeGlobalHue,    s.GradeGlobalSat,    s.GradeGlobalLum),
    _ => (0, 0, 0)
  };

  private static DevelopSettings WriteGradeWheel(DevelopSettings s, int wheel, double hue, double sat, double lum) => wheel switch {
    0 => s with { GradeShadowHue    = hue, GradeShadowSat    = sat, GradeShadowLum    = lum },
    1 => s with { GradeMidtoneHue   = hue, GradeMidtoneSat   = sat, GradeMidtoneLum   = lum },
    2 => s with { GradeHighlightHue = hue, GradeHighlightSat = sat, GradeHighlightLum = lum },
    3 => s with { GradeGlobalHue    = hue, GradeGlobalSat    = sat, GradeGlobalLum    = lum },
    _ => s
  };

  // ---------- Crop ----------

  private void OnCropEdgeChanged(object? sender, NumericUpDownValueChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    var l = (double)(this.FindControl<NumericUpDown>("CropLeftBox")?.Value   ?? 0m);
    var t = (double)(this.FindControl<NumericUpDown>("CropTopBox")?.Value    ?? 0m);
    var r = (double)(this.FindControl<NumericUpDown>("CropRightBox")?.Value  ?? 1m);
    var b = (double)(this.FindControl<NumericUpDown>("CropBottomBox")?.Value ?? 1m);
    this._settings = this._settings with {
      CropLeft = l, CropTop = t, CropRight = r, CropBottom = b
    };
    this.SyncCropOverlay();
    this.SchedulePreviewUpdate();
  }

  private void OnResetCropClick(object? sender, RoutedEventArgs e) {
    this._settings = this._settings with {
      CropAngleDegrees = 0, CropLeft = 0, CropTop = 0, CropRight = 1, CropBottom = 1
    };
    this._suppressSliderEvents = true;
    try {
      if (this.FindControl<Slider>("CropAngleSlider")        is { } a) a.Value = 0;
      if (this.FindControl<NumericUpDown>("CropLeftBox")     is { } l) l.Value = 0m;
      if (this.FindControl<NumericUpDown>("CropTopBox")      is { } t) t.Value = 0m;
      if (this.FindControl<NumericUpDown>("CropRightBox")    is { } r) r.Value = 1m;
      if (this.FindControl<NumericUpDown>("CropBottomBox")   is { } b) b.Value = 1m;
    } finally {
      this._suppressSliderEvents = false;
    }
    this.SyncCropOverlay();
    this.UpdatePreview();
  }

  private void OnResetPerspectiveClick(object? sender, RoutedEventArgs e) {
    this._settings = this._settings with {
      PerspectiveVertical = 0, PerspectiveHorizontal = 0, PerspectiveRotate = 0,
      PerspectiveScale = 100, PerspectiveAspect = 0,
      PerspectiveX = 0, PerspectiveY = 0
    };
    this._suppressSliderEvents = true;
    try {
      if (this.FindControl<Slider>("PerspVerticalSlider")   is { } v) v.Value = 0;
      if (this.FindControl<Slider>("PerspHorizontalSlider") is { } h) h.Value = 0;
      if (this.FindControl<Slider>("PerspRotateSlider")     is { } r) r.Value = 0;
      if (this.FindControl<Slider>("PerspScaleSlider")      is { } sc) sc.Value = 100;
      if (this.FindControl<Slider>("PerspAspectSlider")     is { } a) a.Value = 0;
      if (this.FindControl<Slider>("PerspXSlider")          is { } x) x.Value = 0;
      if (this.FindControl<Slider>("PerspYSlider")          is { } y) y.Value = 0;
    } finally {
      this._suppressSliderEvents = false;
    }
    this.UpdatePreview();
    this.SetStatus("Perspective reset.");
  }

  private void OnAutoLevelClick(object? sender, RoutedEventArgs e) {
    if (this._developedPreview is not { } src)
      return;
    var next = AutoEnhancements.AutoLevel(this._settings, src);
    this.ApplySettingsToUi(next);
    this.UpdatePreview();
    this.SetStatus($"Auto-level: rotate by {next.CropAngleDegrees:+0.0;-0.0;0.0}° to align dominant edges with horizontal.");
  }

  private void OnAutoCropClick(object? sender, RoutedEventArgs e) {
    if (this._developedPreview is not { } src)
      return;
    var next = AutoEnhancements.AutoCrop(this._settings, src);
    this.ApplySettingsToUi(next);
    this.UpdatePreview();
    this.SetStatus($"Auto-crop: bounded to ({next.CropLeft:0.00},{next.CropTop:0.00}) → ({next.CropRight:0.00},{next.CropBottom:0.00}).");
  }

  /// <summary>
  /// Pop a small flyout showing one preview tile per default aspect ratio,
  /// scored by Sobel-edge density. Clicking a tile applies its crop to the
  /// develop settings without touching any other slider.
  /// </summary>
  private async void OnAutoCropSuggestionsClick(object? sender, RoutedEventArgs e) {
    if (this._developedPreview is not { } src) {
      this.SetStatus("No preview to analyse yet.");
      return;
    }
    if (sender is not Button anchor)
      return;

    this.SetStatus("Computing crop suggestions…");
    IReadOnlyList<CropSuggestion> suggestions;
    try {
      // The suggester is CPU-bound but cheap on the downscaled preview;
      // jumping to a worker thread keeps the UI responsive on big images.
      suggestions = await Task.Run(() => AutoCropSuggester.Suggest(src, AutoCropSuggester.DefaultAspectRatios));
    } catch (Exception ex) {
      this.SetStatus($"Auto-crop suggestions failed: {ex.Message}");
      return;
    }

    if (suggestions.Count == 0) {
      this.SetStatus("Auto-crop suggestions: image too small to analyse.");
      return;
    }

    var popup = new Avalonia.Controls.Primitives.Popup {
      PlacementTarget = anchor,
      Placement = PlacementMode.Bottom,
      IsLightDismissEnabled = true
    };

    var panel = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Avalonia.Thickness(6) };
    foreach (var s in suggestions)
      panel.Children.Add(this.BuildSuggestionTile(src, s, popup));

    popup.Child = new Border {
      Background = Avalonia.Media.Brushes.White,
      BorderBrush = Avalonia.Media.Brushes.Gray,
      BorderThickness = new Avalonia.Thickness(1),
      CornerRadius = new Avalonia.CornerRadius(4),
      Padding = new Avalonia.Thickness(6),
      Child = panel
    };
    ((ISetLogicalParent)popup).SetParent(this);
    popup.IsOpen = true;
    this.SetStatus($"Auto-crop suggestions: {suggestions.Count} aspect ratio(s) ranked by edge density. Click a tile to apply.");
  }

  private Control BuildSuggestionTile(Image<Rgba32> src, CropSuggestion s, Avalonia.Controls.Primitives.Popup popup) {
    var thumb = new Avalonia.Controls.Image {
      Stretch = Avalonia.Media.Stretch.Uniform,
      Width = 140,
      Height = 100
    };

    // Render the suggested crop into a thumbnail bitmap so the user sees
    // exactly what they'd get on apply.
    try {
      using var clone = src.Clone();
      var x = (int)Math.Round(s.Left * clone.Width);
      var y = (int)Math.Round(s.Top * clone.Height);
      var w = Math.Max(1, (int)Math.Round((s.Right - s.Left) * clone.Width));
      var h = Math.Max(1, (int)Math.Round((s.Bottom - s.Top) * clone.Height));
      clone.Mutate(c => c.Crop(new SixLabors.ImageSharp.Rectangle(x, y, w, h))
                          .Resize(140, 100, SixLabors.ImageSharp.Processing.KnownResamplers.Bicubic));
      using var ms = new MemoryStream();
      clone.SaveAsJpeg(ms);
      ms.Position = 0;
      thumb.Source = new Bitmap(ms);
    } catch {
      // Tile shows blank if cropping fails — non-fatal.
    }

    var tile = new Border {
      BorderBrush = Avalonia.Media.Brushes.LightGray,
      BorderThickness = new Avalonia.Thickness(1),
      CornerRadius = new Avalonia.CornerRadius(3),
      Margin = new Avalonia.Thickness(4),
      Padding = new Avalonia.Thickness(4),
      Child = new StackPanel {
        Spacing = 2,
        Children = {
          thumb,
          new TextBlock {
            Text = FormatAspectLabel(s.AspectRatio),
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            FontSize = 11,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
          }
        }
      },
      Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
    };
    tile.PointerPressed += (_, _) => {
      this._settings = this._settings with {
        CropLeft = s.Left, CropTop = s.Top, CropRight = s.Right, CropBottom = s.Bottom
      };
      this.ApplySettingsToUi(this._settings);
      this.UpdatePreview();
      this.SetStatus($"Auto-crop applied: {FormatAspectLabel(s.AspectRatio)} ({s.Left:0.00},{s.Top:0.00})→({s.Right:0.00},{s.Bottom:0.00}).");
      popup.IsOpen = false;
    };
    return tile;
  }

  private static string FormatAspectLabel(double aspect) => aspect switch {
    var a when Math.Abs(a - 1.0)   < 0.01 => "1:1",
    var a when Math.Abs(a - 0.8)   < 0.01 => "4:5",
    var a when Math.Abs(a - 1.5)   < 0.01 => "3:2",
    var a when Math.Abs(a - 1.778) < 0.01 => "16:9",
    var a when Math.Abs(a - 0.667) < 0.01 => "2:3",
    var a when Math.Abs(a - 1.5 / (5.0 / 7.0)) < 0.01 => "5:7",
    _ => $"{aspect:0.00}:1"
  };

  private void OnUprightAutoClick(object? sender, RoutedEventArgs e) {
    if (this._developedPreview is not { } src)
      return;
    var next = AutoEnhancements.UprightAuto(this._settings, src);
    this.ApplySettingsToUi(next);
    this.UpdatePreview();
    this.SetStatus($"Upright auto: rotate by {next.CropAngleDegrees:+0.0;-0.0;0.0}°.");
  }

  private void OnAutoChannelStretchClick(object? sender, RoutedEventArgs e) {
    if (this._developedPreview is not { } src)
      return;
    var hist = HistogramAnalyzer.Compute(src);
    var opts = this.ReadAutoOptions();
    var next = AutoDeveloper.AutoChannelStretch(this._settings, hist, opts);
    this.ApplySettingsToUi(next);
    this.UpdatePreview();
    this.SetStatus($"Auto RGB stretch: top percentile={opts.StretchTopPercentile * 100:F1}%.");
  }

  private async Task LoadPreviewAsync() {
    this.SetPreviewPhase(PreviewPhase.Loading, "loading source",
      $"file={(this._sourceFile?.Name ?? "(null)")} exists={(this._sourceFile?.Exists.ToString() ?? "n/a")}");

    if (this._sourceFile is not { Exists: true } file) {
      this.SetPreviewPhase(PreviewPhase.Error, "no file",
        $"_sourceFile null or non-existent: {this._sourceFile?.FullName ?? "(null)"}");
      this.SetStatus("No file selected.");
      return;
    }

    this.SetStatus("Loading...");
    try {
      // RawImageLoader routes RAW extensions through PNGCrushCS
      // (FileFormat.CameraRaw + FileFormat.Dng) and falls through to
      // ImageSharp for JPEG / PNG / TIFF / etc.
      var image = await RawImageLoader.LoadAsync(file);
      // Track full-resolution dimensions so the upscale combo can gate
      // factors against the file the Save As pass will actually see.
      this._sourceFullWidth = image.Width;
      this._sourceFullHeight = image.Height;
      var longest = Math.Max(image.Width, image.Height);
      if (longest > PreviewMaxEdgePixels) {
        var scale = (double)PreviewMaxEdgePixels / longest;
        image.Mutate(c => c.Resize((int)(image.Width * scale), (int)(image.Height * scale)));
      }
      this._previewSource = image;
    } catch (Exception ex) {
      this.SetPreviewPhase(PreviewPhase.Error, "load failed",
        $"{ex.GetType().Name}: {ex.Message}");
      this.Title = $"LOAD FAILED: {ex.GetType().Name}: {ex.Message}";
      try {
        var logPath = Path.Combine(Path.GetTempPath(), "photomanager-develop-diag.log");
        File.AppendAllText(logPath,
          $"[{DateTime.Now:HH:mm:ss.fff}] LOAD FAILED for {file.FullName}:{Environment.NewLine}" +
          $"  {ex}{Environment.NewLine}");
      } catch { /* logging is best-effort */ }
      this.SetStatus($"Load failed: {ex.Message}");
      return;
    }

    this.SetPreviewPhase(PreviewPhase.Developing, "preparing preview",
      $"src={this._previewSource!.Width}×{this._previewSource.Height} loaded");
    this.SetStatus($"{this._sourceFile.Name} ({this._previewSource.Width}×{this._previewSource.Height} preview)");
    // Disable upscale factors that would exceed the 32K cap on this source.
    this.RefreshUpscaleComboAvailability();
    if (this.FindControl<MaskOverlayCanvas>("MaskOverlay") is { } overlay) {
      overlay.ImageWidth  = this._previewSource.Width;
      overlay.ImageHeight = this._previewSource.Height;
    }
    if (this.FindControl<CropOverlayCanvas>("CropOverlay") is { } cropOverlay) {
      cropOverlay.ImageWidth  = this._previewSource.Width;
      cropOverlay.ImageHeight = this._previewSource.Height;
    }

    // Pick up any develop edits embedded in the file's XMP (copy 0) or the
    // selected virtual-copy sidecar so the user resumes mid-edit instead of
    // starting from defaults each time.
    DevelopSettings? embedded = null;
    try { embedded = await DevelopMetadataStore.LoadAsync(file, this._copyIndex); } catch { /* best-effort */ }
    if (embedded is not null) {
      try {
        this.ApplySettingsToUi(embedded);
        this.SetStatus(this._copyIndex == 0
          ? $"Resumed embedded develop settings for {file.Name}."
          : $"Resumed copy {this._copyIndex} settings for {file.Name}.");
      } catch (Exception ex) {
        this.SetPreviewPhase(PreviewPhase.Error, "apply-embedded failed",
          $"ApplySettingsToUi: {ex.GetType().Name}: {ex.Message}");
        try {
          File.AppendAllText(Path.Combine(Path.GetTempPath(), "photomanager-develop-diag.log"),
            $"[{DateTime.Now:HH:mm:ss.fff}] ApplySettingsToUi:{Environment.NewLine}  {ex}{Environment.NewLine}");
        } catch { /* logging is best-effort */ }
        // Continue — render with whatever defaults we have rather than bail
        // and leave the preview blank.
      }
    }
    // Wrap UpdatePreview in a try so an unobserved throw from the develop
    // pipeline (the fire-and-forget LoadPreviewAsync would swallow it
    // otherwise) shows up in the diagnostic strip.
    try {
      this.UpdatePreview();
    } catch (Exception ex) {
      this.SetPreviewPhase(PreviewPhase.Error, "UpdatePreview throw",
        $"{ex.GetType().Name}: {ex.Message}");
      try {
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "photomanager-develop-diag.log"),
          $"[{DateTime.Now:HH:mm:ss.fff}] UpdatePreview throw:{Environment.NewLine}  {ex}{Environment.NewLine}");
      } catch { /* logging is best-effort */ }
    }
    this.InvalidateBaseline();
  }

  private void OnSettingsChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    this.SchedulePreviewUpdate();
  }

  private void OnRotateLeftClick(object? sender, RoutedEventArgs e) => this.BumpRotation(-90);
  private void OnRotateRightClick(object? sender, RoutedEventArgs e) => this.BumpRotation(90);
  private void OnRotate180Click(object? sender, RoutedEventArgs e) => this.BumpRotation(180);

  private void BumpRotation(int degrees) {
    var current = this._settings.RotationDegrees;
    var next = ((current + degrees) % 360 + 360) % 360;
    this._settings = this._settings with { RotationDegrees = next };
    this.UpdatePreview();
  }

  private void OnResetRotationClick(object? sender, RoutedEventArgs e) {
    this._settings = this._settings with { RotationDegrees = 0 };
    this.UpdatePreview();
  }

  private void OnResetAllClick(object? sender, RoutedEventArgs e) {
    this.ApplySettingsToUi(new DevelopSettings());
    // Clear any active film simulation so the combo goes back to "(none)".
    this._filmSimBaseSettings = null;
    this._suppressFilmSimSelect = true;
    try {
      if (this.FindControl<ComboBox>("FilmSimCombo") is { } filmCombo)
        filmCombo.SelectedIndex = 0;
    } finally {
      this._suppressFilmSimSelect = false;
    }
    this.UpdatePreview();
    this.InvalidateBaseline();
  }

  private void SchedulePreviewUpdate() {
    // Debounce so dragging a slider doesn't queue a preview per tick.
    this._updateTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, OnTimerTick);
    this._updateTimer.Stop();
    this._updateTimer.Start();
  }

  private void OnTimerTick(object? sender, EventArgs e) {
    this._updateTimer?.Stop();
    this.UpdatePreview();
  }

  /// <summary>
  /// Cancellation source for the most recent in-flight AI preview render.
  /// Cancelled and replaced on every <see cref="UpdatePreview"/> call so a
  /// slider drag doesn't have to wait for the previous AI pass to finish.
  /// </summary>
  private CancellationTokenSource? _aiPreviewCts;

  private void UpdatePreview() {
    this._settings = this.ReadSettingsFromUi();
    this.RefreshValueLabels();

    if (this._previewSource is null) {
      this.SetPreviewPhase(PreviewPhase.Error, "no source",
        "_previewSource is null in UpdatePreview");
      this.Title = "UPDATE BAILED: _previewSource is null";
      return;
    }

    // Always cancel an in-flight AI render before kicking a new pipeline —
    // settings changed, so the in-flight result is stale.
    var prevCts = Interlocked.Exchange(ref this._aiPreviewCts, null);
    prevCts?.Cancel();
    prevCts?.Dispose();

    Image<Rgba32> fastDeveloped;
    try {
      // Fast pass: full develop pipeline minus AI stages. Keeps slider
      // drags responsive on the UI thread. AI runs off-thread below.
      fastDeveloped = ImageDeveloper.Apply(this._previewSource, this._settings, previewMode: true);
    } catch (Exception ex) {
      this.SetPreviewPhase(PreviewPhase.Error, "develop throw",
        $"ImageDeveloper.Apply: {ex.GetType().Name}: {ex.Message}");
      this.Title = $"APPLY FAILED: {ex.GetType().Name}: {ex.Message}";
      try {
        var logPath = Path.Combine(Path.GetTempPath(), "photomanager-develop-diag.log");
        File.AppendAllText(logPath,
          $"[{DateTime.Now:HH:mm:ss.fff}] APPLY FAILED:{Environment.NewLine}  {ex}{Environment.NewLine}");
      } catch { /* logging is best-effort */ }
      this.SetStatus($"Preview failed: {ex.Message}");
      return;
    }

    this._developedPreview?.Dispose();
    this._developedPreview = fastDeveloped;
    this.PaintPreviewImages(fastDeveloped);
    this.RenderHistogram(fastDeveloped);

    // Persist the current edits inside the file's XMP packet (or the active
    // copy sidecar) so the workflow stays non-destructive — the pixel data
    // on disk is never touched, but the user's slider state travels with
    // the photo. Auto-saves leave the snapshot stack alone (snapshotLabel
    // null); explicit Save As… and create-virtual-copy paths push history.
    if (this._sourceFile is { } src && (this._copyIndex > 0 || DevelopMetadataStore.SupportsContainer(src)))
      _ = DevelopMetadataStore.SaveAsync(src, this._settings, this._copyIndex, snapshotLabel: null);

    var wantsDenoise = this._settings.AiDenoiseStrength > 1e-6;
    var wantsUpscale = this._settings.AiUpscaleFactor > 1;
    var wantsColorize = this._settings.AiColorizeAmount > 1e-6;
    if (!wantsDenoise && !wantsUpscale && !wantsColorize) {
      this.SetAiOverlayVisible(false);
      this.SetPreviewEta(null, null);
      return;
    }

    // Hand the AI pass off to a background task. Cancellation flows through
    // to the tile loops in OnnxDenoiser / OnnxUpscaler / OnnxColorizer so
    // a follow-up settings change unblocks within the next tile.
    var stages = new List<string>();
    if (wantsDenoise)  stages.Add("denoise");
    if (wantsColorize) stages.Add("colorize");
    if (wantsUpscale)  stages.Add("upscale");
    var label = $"Computing AI {string.Join(" + ", stages)}…";
    this.SetAiOverlayLabel(label);
    this.SetAiOverlayVisible(true);
    this.SetPreviewPhase(PreviewPhase.AiWork, $"AI: {string.Join(" + ", stages)}",
      $"running AI stage(s) on {fastDeveloped.Width}×{fastDeveloped.Height}");
    this.SetPreviewEta(0, "starting…");
    var aiStarted = DateTime.UtcNow;

    var cts = new CancellationTokenSource();
    this._aiPreviewCts = cts;
    var capturedSettings = this._settings;
    var capturedSourceClone = this._previewSource.Clone();

    _ = Task.Run(() => {
      try {
        return ImageDeveloper.Apply(capturedSourceClone, capturedSettings, previewMode: false, ct: cts.Token);
      } catch (OperationCanceledException) {
        return null;
      } catch {
        return null;
      } finally {
        capturedSourceClone.Dispose();
      }
    }, cts.Token).ContinueWith(t => {
      var aiResult = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
      var aiElapsed = DateTime.UtcNow - aiStarted;
      Avalonia.Threading.Dispatcher.UIThread.Post(() => {
        // Newer call already cancelled and replaced us, or our token was
        // cancelled mid-flight — discard the result either way.
        if (cts.IsCancellationRequested || !ReferenceEquals(this._aiPreviewCts, cts)) {
          aiResult?.Dispose();
          return;
        }
        if (aiResult is null) {
          this.SetAiOverlayVisible(false);
          this.SetPreviewEta(null, null);
          return;
        }
        this._developedPreview?.Dispose();
        this._developedPreview = aiResult;
        this.PaintPreviewImages(aiResult);
        this.RenderHistogram(aiResult);
        this.SetAiOverlayVisible(false);
        this.SetPreviewEta(null, $"AI {string.Join("+", stages)} done in {aiElapsed.TotalSeconds:F1}s");
      });
    });
  }

  private void PaintPreviewImages(Image<Rgba32> developed) {
    // Direct RGBA copy via WriteableBitmap — bypasses JPEG encode/decode
    // and stream-disposal hazards entirely. Each Image control gets its
    // OWN WriteableBitmap so Avalonia's per-Image render pipeline can't
    // share decode state across them.
    var w = developed.Width;
    var h = developed.Height;
    long brightSum = 0;
    var brightSamples = 0;

    Avalonia.Media.Imaging.WriteableBitmap? BuildBitmap() {
      try {
        var wb = new Avalonia.Media.Imaging.WriteableBitmap(
          new Avalonia.PixelSize(w, h),
          new Avalonia.Vector(96, 96),
          Avalonia.Platform.PixelFormat.Bgra8888,
          Avalonia.Platform.AlphaFormat.Premul);
        using var fb = wb.Lock();
        // ImageSharp is RGBA, Avalonia's Bgra8888 wants BGRA. Convert
        // into a managed byte[] row and Marshal.Copy it into the
        // framebuffer (avoids needing /unsafe at the project level).
        var stride = fb.RowBytes;
        var rowBytes = new byte[stride];
        var basePtr = fb.Address;
        developed.ProcessPixelRows(accessor => {
          for (var y = 0; y < accessor.Height; y++) {
            var row = accessor.GetRowSpan(y);
            for (var x = 0; x < row.Length; x++) {
              var p = row[x];
              rowBytes[x * 4 + 0] = p.B;
              rowBytes[x * 4 + 1] = p.G;
              rowBytes[x * 4 + 2] = p.R;
              rowBytes[x * 4 + 3] = p.A;
            }
            System.Runtime.InteropServices.Marshal.Copy(
              rowBytes, 0, basePtr + y * stride, stride);
          }
        });
        return wb;
      } catch {
        return null;
      }
    }

    // Brightness probe (sparse) — tells us if the develop pipeline
    // produced a black/transparent buffer regardless of paint success.
    developed.ProcessPixelRows(accessor => {
      var step = Math.Max(1, accessor.Height / 32);
      for (var y = 0; y < accessor.Height; y += step) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x += step) {
          brightSum += row[x].R + row[x].G + row[x].B;
          brightSamples++;
        }
      }
    });
    var meanBright = brightSamples == 0 ? 0 : brightSum / (3.0 * brightSamples);

    var foundPreview = false;
    var foundSplit = false;
    var foundSlider = false;
    Avalonia.Media.Imaging.WriteableBitmap? bitmap = null;

    void Assign(string name, ref bool found) {
      if (this.FindControl<Avalonia.Controls.Image>(name) is not { } img)
        return;
      bitmap ??= BuildBitmap();
      if (bitmap is null)
        return;
      found = true;
      img.Source = bitmap;
    }

    var foundOverlay = false;
    Assign("PreviewImage",        ref foundPreview);
    Assign("PreviewImageSplit",   ref foundSplit);
    Assign("PreviewImageOverlay", ref foundOverlay);
    Assign("PreviewImageSlider",  ref foundSlider);

    // Count the applied edits so the rich strip can summarise. Cheap
    // — just a handful of non-zero checks. Pure-logic helper in Core
    // so the count is exercised by the test suite.
    var appliedEdits = AppliedEditsCounter.Count(this._settings);
    var diag = $"{w}×{h} mean={meanBright:F0} " +
               $"bmp={(bitmap is null ? "FAIL" : "ok")} " +
               $"img={(foundPreview ? "ok" : "MISS")}/" +
               $"split={(foundSplit ? "ok" : "MISS")}/" +
               $"slider={(foundSlider ? "ok" : "MISS")} " +
               $"edits={appliedEdits}";
    this.Title = $"Develop · {diag}";

    var phaseLabel = (bitmap, foundPreview, foundSplit, foundSlider) switch {
      (null, _, _, _) => "bitmap build failed",
      (_, false, _, _) when !foundSplit && !foundSlider => "no Image controls found",
      _ => appliedEdits == 0 ? "ready (no edits)" : $"ready ({appliedEdits} edit{(appliedEdits == 1 ? "" : "s")})",
    };
    var phase = bitmap is null ? PreviewPhase.Error : PreviewPhase.Ready;
    this.SetPreviewPhase(phase, phaseLabel, diag);
  }

  private void SetAiOverlayVisible(bool visible) {
    if (this.FindControl<Border>("AiProgressOverlay") is { } overlay)
      overlay.IsVisible = visible;
  }

  private void SetAiOverlayLabel(string text) {
    if (this.FindControl<TextBlock>("AiProgressText") is { } label)
      label.Text = text;
  }

  /// <summary>
  /// Paint the histogram canvas from the developed preview. R/G/B drawn
  /// additively (over each other) so the user sees per-channel clipping
  /// at either end without a fourth panel to read.
  /// </summary>
  private void RenderHistogram(Image<Rgba32> developed) {
    if (this.FindControl<Canvas>("HistogramCanvas") is not { } canvas)
      return;

    canvas.Children.Clear();
    var width = canvas.Bounds.Width > 0 ? canvas.Bounds.Width : 256;
    var height = canvas.Bounds.Height > 0 ? canvas.Bounds.Height : 80;
    var hist = HistogramAnalyzer.Compute(developed);

    void DrawChannel(int[] counts, Avalonia.Media.Color color) {
      var max = 1;
      for (var i = 0; i < counts.Length; i++)
        if (counts[i] > max) max = counts[i];

      var poly = new Avalonia.Controls.Shapes.Polyline {
        Stroke = new Avalonia.Media.SolidColorBrush(color, opacity: 0.7),
        StrokeThickness = 1
      };
      var points = new Avalonia.Collections.AvaloniaList<Avalonia.Point>();
      for (var i = 0; i < counts.Length; i++) {
        var x = i / 255.0 * width;
        var y = height - counts[i] / (double)max * height;
        points.Add(new Avalonia.Point(x, y));
      }
      poly.Points = points;
      canvas.Children.Add(poly);
    }

    DrawChannel(hist.Red,   Avalonia.Media.Color.FromRgb(0xE7, 0x4C, 0x3C));
    DrawChannel(hist.Green, Avalonia.Media.Color.FromRgb(0x2E, 0xCC, 0x71));
    DrawChannel(hist.Blue,  Avalonia.Media.Color.FromRgb(0x34, 0x98, 0xDB));

    // Mirror the luminance histogram onto the curve editor so users can
    // shape responses to the actual tonal distribution.
    if (this.FindControl<ToneCurveEditor>("CurveEditor") is { } curveEditor)
      curveEditor.SetHistogram(hist.Luminance);
  }

  // --- Auto buttons ---

  private AutoAdjustOptions ReadAutoOptions() {
    var clipPct = (double?)this.FindControl<NumericUpDown>("WbClipPctUpDown")?.Value ?? 2;
    return new AutoAdjustOptions(
      WbClipLowPct: clipPct / 100.0,
      WbClipHighPct: clipPct / 100.0,
      WbHighlightLumMin: (int)((double?)this.FindControl<NumericUpDown>("WbHiLumMinUpDown")?.Value ?? 200),
      WbHighlightLumMax: (int)((double?)this.FindControl<NumericUpDown>("WbHiLumMaxUpDown")?.Value ?? 250),
      WbHighlightBlendWeight: (double?)this.FindControl<NumericUpDown>("WbHighlightBlendUpDown")?.Value ?? 0.5,
      WbTemperatureSensitivity: (double?)this.FindControl<NumericUpDown>("WbTempSensUpDown")?.Value ?? 50,
      WbTintSensitivity: (double?)this.FindControl<NumericUpDown>("WbTempSensUpDown")?.Value ?? 50,
      ToneBlackClipPct: ((double?)this.FindControl<NumericUpDown>("ToneClipPctUpDown")?.Value ?? 0.5) / 100.0,
      ToneWhiteClipPct: ((double?)this.FindControl<NumericUpDown>("ToneClipPctUpDown")?.Value ?? 0.5) / 100.0,
      ToneRecoveryStrength: (double?)this.FindControl<NumericUpDown>("ToneRecoveryUpDown")?.Value ?? 1.5,
      StretchTopPercentile: ((double?)this.FindControl<NumericUpDown>("StretchTopPctUpDown")?.Value ?? 99.5) / 100.0
    );
  }

  private void OnAutoSettingsToggle(object? sender, RoutedEventArgs e) {
    if (this.FindControl<Border>("AutoSettingsPanel") is { } panel)
      panel.IsVisible = this.FindControl<ToggleButton>("AutoSettingsToggle")?.IsChecked == true;
  }

  private void OnAutoToneClick(object? sender, RoutedEventArgs e) {
    if (this._developedPreview is not { } src)
      return;
    var hist = HistogramAnalyzer.Compute(src);
    var opts = this.ReadAutoOptions();
    var next = AutoDeveloper.AutoTone(this._settings, hist, opts);
    this.ApplySettingsToUi(next);
    this.UpdatePreview();
    this.SetStatus($"Auto-tone: clip={opts.ToneBlackClipPct * 100:F1}%, recovery={opts.ToneRecoveryStrength:F2}×.");
  }

  private void OnAutoWhiteBalanceClick(object? sender, RoutedEventArgs e) {
    if (this._previewSource is not { } src)
      return;
    var opts = this.ReadAutoOptions();
    var (temp, tint) = AutoWhiteBalance.Estimate(src, opts);
    var next = this._settings with {
      TemperatureShift = temp,
      TintShift = tint
    };
    this.ApplySettingsToUi(next);
    this.UpdatePreview();
    this.SetStatus($"Auto WB: temp={temp:+0.0;-0.0} tint={tint:+0.0;-0.0} (clip={opts.WbClipLowPct * 100:F0}%, blend={opts.WbHighlightBlendWeight:F0}).");
  }

  private void OnResetAutoClick(object? sender, RoutedEventArgs e) {
    var next = this._settings with {
      WhitesPercent = 0,
      BlacksPercent = 0,
      ShadowsPercent = 0,
      HighlightsPercent = 0,
      TemperatureShift = 0,
      TintShift = 0,
      RedGain = 0,
      GreenGain = 0,
      BlueGain = 0
    };
    this.ApplySettingsToUi(next);
    this.UpdatePreview();
  }

  // --- Eyedroppers ---

  private void OnEyedropToggleClick(object? sender, RoutedEventArgs e) {
    if (sender is not Avalonia.Controls.Primitives.ToggleButton clicked)
      return;
    var which = clicked.Tag as string;
    var nowActive = clicked.IsChecked == true;

    // Mutually exclusive — only one eyedropper at a time.
    foreach (var name in EyedropperNames) {
      if (this.FindControl<Avalonia.Controls.Primitives.ToggleButton>(name) is { } tb && !ReferenceEquals(tb, clicked))
        tb.IsChecked = false;
    }

    this._activeEyedropper = nowActive ? which : null;
    this.SetStatus(nowActive ? $"Click on the preview to set the {which?.ToLowerInvariant()} point." : "");
  }

  private static readonly string[] EyedropperNames = {
    "EyedropBlack", "EyedropWhite", "EyedropGrey",
    "EyedropRed", "EyedropGreen", "EyedropBlue"
  };

  private void OnPreviewPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) {
    if (this._activeEyedropper is null)
      return;
    if (sender is not Avalonia.Controls.Image img)
      return;
    if (this._developedPreview is not { } src)
      return;

    var properties = e.GetCurrentPoint(img).Properties;
    if (!properties.IsLeftButtonPressed)
      return;

    var screen = e.GetPosition(img);
    var bounds = img.Bounds;
    if (bounds.Width <= 0 || bounds.Height <= 0)
      return;

    // Image is rendered Stretch=Uniform / DownOnly; recompute the actual
    // displayed rectangle so we sample the right pixel.
    var srcW = src.Width;
    var srcH = src.Height;
    var scale = Math.Min(bounds.Width / srcW, bounds.Height / srcH);
    if (scale > 1) scale = 1;
    var renderedW = srcW * scale;
    var renderedH = srcH * scale;
    var offsetX = (bounds.Width - renderedW) / 2;
    var offsetY = (bounds.Height - renderedH) / 2;

    if (screen.X < offsetX || screen.X > offsetX + renderedW
        || screen.Y < offsetY || screen.Y > offsetY + renderedH)
      return;

    var srcX = (int)Math.Clamp((screen.X - offsetX) / scale, 0, srcW - 1);
    var srcY = (int)Math.Clamp((screen.Y - offsetY) / scale, 0, srcH - 1);
    var sample = src[srcX, srcY];

    var next = this._activeEyedropper switch {
      "Black" => AutoDeveloper.PickBlackPoint(this._settings, sample.R, sample.G, sample.B),
      "White" => AutoDeveloper.PickWhitePoint(this._settings, sample.R, sample.G, sample.B),
      "Grey"  => AutoDeveloper.PickGreyPoint (this._settings, sample.R, sample.G, sample.B),
      "Red"   => AutoDeveloper.PickRedPoint  (this._settings, sample.R, sample.G, sample.B),
      "Green" => AutoDeveloper.PickGreenPoint(this._settings, sample.R, sample.G, sample.B),
      "Blue"  => AutoDeveloper.PickBluePoint (this._settings, sample.R, sample.G, sample.B),
      _ => this._settings
    };

    this.ApplySettingsToUi(next);
    this.UpdatePreview();
    this.SetStatus($"{this._activeEyedropper}-point sampled @ ({srcX},{srcY}).");

    // One-shot: turn the eyedropper off after a successful pick.
    foreach (var name in EyedropperNames) {
      if (this.FindControl<Avalonia.Controls.Primitives.ToggleButton>(name) is { } tb)
        tb.IsChecked = false;
    }
    this._activeEyedropper = null;
    e.Handled = true;
  }

  /// <summary>Capture the current slider values into a DevelopSettings.</summary>
  private DevelopSettings ReadSettingsFromUi() {
    var s = this._settings with { };
    foreach (var (sliderName, _, _, set, _) in SliderBindings)
      if (this.FindControl<Slider>(sliderName) is { } slider)
        s = set(s, slider.Value);
    return s;
  }

  /// <summary>Push a DevelopSettings onto the sliders without firing the
  /// usual ValueChanged → preview cascade per slider; the caller is
  /// expected to call <see cref="UpdatePreview"/> once at the end.</summary>
  private void ApplySettingsToUi(DevelopSettings target) {
    this._settings = target;
    this._suppressSliderEvents = true;
    try {
      foreach (var (sliderName, _, get, _, _) in SliderBindings) {
        if (this.FindControl<Slider>(sliderName) is { } slider)
          slider.Value = get(target);
      }
      // Sync the rotation NumericUpDown with the slider (they share
      // the same CropAngleDegrees field but the UpDown gives 0.1°
      // fine control the slider can't).
      if (this.FindControl<NumericUpDown>("CropAngleUpDown") is { } angleUpDown)
        angleUpDown.Value = (decimal)target.CropAngleDegrees;
      if (this.FindControl<ToneCurveEditor>("CurveEditor") is { } curve) {
        // Display the channel that's currently selected — switching channels
        // doesn't change the underlying settings, just which curve the editor shows.
        curve.SetCurve(this.ReadCurveForActiveChannel(target));
        curve.SetInterpolation(target.ToneCurveInterpolation);
      }
      if (this.FindControl<ComboBox>("CurveInterpolationCombo") is { } interpCombo)
        interpCombo.SelectedIndex = target.ToneCurveInterpolation switch {
          CurveInterpolation.CatmullRom => 1,
          CurveInterpolation.Bezier     => 2,
          _                             => 0
        };
      if (this.FindControl<ComboBox>("AiUpscaleCombo") is { } upscaleCombo)
        upscaleCombo.SelectedIndex = UpscaleFactorToComboIndex(target.AiUpscaleFactor);
      if (this.FindControl<TextBlock>("AiUpscaleValue") is { } upscaleLabel)
        upscaleLabel.Text = (target.AiUpscaleFactor <= 1 ? 1 : target.AiUpscaleFactor)
          .ToString(CultureInfo.InvariantCulture) + "×";
      if (this.FindControl<ComboBox>("AiUpscaleModelCombo") is { } upscaleModelCombo)
        upscaleModelCombo.SelectedIndex = UpscaleModelToComboIndex(target.AiUpscaleModel);
      if (this.FindControl<ComboBox>("AiDenoiseModelCombo") is { } denoiseModelCombo)
        denoiseModelCombo.SelectedIndex = DenoiseModelToComboIndex(target.AiDenoiseModel);
      if (this.FindControl<ComboBox>("AiColorizeModelCombo") is { } colorizeModelCombo)
        colorizeModelCombo.SelectedIndex = ColorizeModelToComboIndex(target.AiColorizeModel);
      // Crop edges
      if (this.FindControl<NumericUpDown>("CropLeftBox")   is { } leftBox)   leftBox.Value   = (decimal)target.CropLeft;
      if (this.FindControl<NumericUpDown>("CropTopBox")    is { } topBox)    topBox.Value    = (decimal)target.CropTop;
      if (this.FindControl<NumericUpDown>("CropRightBox")  is { } rightBox)  rightBox.Value  = (decimal)target.CropRight;
      if (this.FindControl<NumericUpDown>("CropBottomBox") is { } bottomBox) bottomBox.Value = (decimal)target.CropBottom;
      if (this.FindControl<Slider>("LookOpacitySlider") is { } lookOp) lookOp.Value = target.LookOpacity;
      if (this.FindControl<ComboBox>("LookCombo") is { } lookCombo && lookCombo.ItemsSource is IEnumerable<string> looks) {
        var pick = string.IsNullOrEmpty(target.LookName) ? "(no look)" : target.LookName!;
        var idx = looks.ToList().IndexOf(pick);
        lookCombo.SelectedIndex = idx < 0 ? 0 : idx;
      }
    } finally {
      this._suppressSliderEvents = false;
    }
    this.SyncCropOverlay();
    if (this.FindControl<TextBlock>("LookOpacityValue") is { } lookLabel)
      lookLabel.Text = target.LookOpacity.ToString("0.00", CultureInfo.InvariantCulture);
    this.RefreshValueLabels();
    this.RefreshHslAxisSliders();
    this.RefreshGradingSliders();
    this.RefreshBwMixSlider();
    this._suppressSliderEvents = true;
    try {
      if (this.FindControl<CheckBox>("ConvertToBwCheck") is { } cb)
        cb.IsChecked = target.ConvertToGrayscale;
    } finally {
      this._suppressSliderEvents = false;
    }
    this.RefreshLocalAdjustmentsList(selectIndex: -1);
  }

  private void RefreshValueLabels() {
    var inv = CultureInfo.InvariantCulture;
    foreach (var (_, labelName, get, _, format) in SliderBindings) {
      // labelName is nullable in practice — CropAngleSlider uses a
      // NumericUpDown (not a TextBlock) for its value, so its label
      // entry is intentionally null. Skip null-named entries so we
      // don't throw ArgumentNullException from FindControl(null).
      if (string.IsNullOrEmpty(labelName))
        continue;
      if (this.FindControl<TextBlock>(labelName) is { } label)
        label.Text = get(this._settings).ToString(format, inv) + (labelName == "ExposureValue" ? " EV" : "");
    }
  }

  private async void OnBakeThumbnailClick(object? sender, RoutedEventArgs e) {
    if (this._sourceFile is not { Exists: true } source) {
      this.SetStatus("Nothing to bake.");
      return;
    }
    if (!DevelopMetadataStore.SupportsContainer(source)) {
      this.SetStatus("Embedded thumbnail update only supported on JPEG sources.");
      return;
    }
    if (this._developedPreview is not { } developed) {
      this.SetStatus("No preview to bake — wait for the first render.");
      return;
    }

    var requestedEdge = (int)(this.FindControl<NumericUpDown>("ThumbMaxEdgeBox")?.Value  ?? 160);
    var requestedQ    = (int)(this.FindControl<NumericUpDown>("ThumbQualityBox")?.Value ?? 75);
    requestedEdge = Math.Clamp(requestedEdge, 16, 2048);
    requestedQ    = Math.Clamp(requestedQ,     1,  100);

    this.SetStatus("Baking thumbnail...");
    try {
      var result = await DevelopMetadataStore.BakeThumbnailAsync(source, developed, requestedEdge, requestedQ);
      if (result.Success) {
        var psnr = double.IsPositiveInfinity(result.PsnrDb) ? "∞" : result.PsnrDb.ToString("0.0");
        var prefix = result.DidAutoFit
          ? $"Auto-fit (request didn't fit): {result.Width}×{result.Height} q={result.Quality}"
          : $"Embedded {result.Width}×{result.Height} q={result.Quality}";
        this.SetStatus($"{prefix}, {result.ThumbnailByteCount} thumb bytes / {result.ExifPayloadByteCount} EXIF bytes, PSNR {psnr} dB.");
      } else if (result.BytesOverBudget is { } over) {
        this.SetStatus($"Thumbnail couldn't fit: requested settings exceeded the 64 KB EXIF cap by {over} bytes and even the smallest probe was over budget. {result.Error}");
      } else {
        this.SetStatus($"Bake failed: {result.Error ?? "unknown error"}.");
      }
    } catch (Exception ex) {
      this.SetStatus($"Bake failed: {ex.Message}");
    }
  }

  private async void OnSaveAsClick(object? sender, RoutedEventArgs e) {
    if (this._sourceFile is not { Exists: true } source) {
      this.SetStatus("Nothing to save.");
      return;
    }

    var topLevel = TopLevel.GetTopLevel(this);
    if (topLevel?.StorageProvider is not { CanSave: true } storage) {
      this.SetStatus("Save picker unavailable.");
      return;
    }

    var suggested = Path.GetFileNameWithoutExtension(source.Name) + "_developed.jpg";
    var result = await storage.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save developed JPEG",
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

    this.SetStatus("Rendering...");
    try {
      await ImageDeveloper.RenderToJpegAsync(source, new FileInfo(destination), this._settings);
      // Snapshot the develop settings as they stood at this Save As so the
      // user can roll back to the version they exported.
      if (this._copyIndex > 0 || DevelopMetadataStore.SupportsContainer(source))
        await DevelopMetadataStore.SaveAsync(source, this._settings, this._copyIndex,
          snapshotLabel: $"Save As {Path.GetFileName(destination)}");
      this.SetStatus($"Saved {Path.GetFileName(destination)}.");
      // Refresh the inline history panel if it's open so the new snapshot appears.
      if (this.FindControl<ToggleButton>("HistoryToggleButton")?.IsChecked == true)
        await this.RefreshInlineHistoryAsync();
    } catch (Exception ex) {
      this.SetStatus($"Save failed: {ex.Message}");
    }
  }

  // ---------- Templates ----------

  private void RefreshTemplateList(string? selectName = null) {
    if (this.FindControl<ComboBox>("TemplateCombo") is not { } combo)
      return;
    var list = this._templates.List();
    this._suppressTemplateSelect = true;
    try {
      combo.ItemsSource = list.Select(t => t.Name).ToList();
      combo.SelectedIndex = selectName is null
        ? -1
        : list.Select((t, i) => (t.Name, i)).FirstOrDefault(p => p.Name == selectName).i;
    } finally {
      this._suppressTemplateSelect = false;
    }
  }

  private void OnTemplateSelected(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressTemplateSelect)
      return;
    if (sender is not ComboBox combo)
      return;
    if (combo.SelectedItem is not string name)
      return;

    var match = this._templates.List().FirstOrDefault(t => t.Name == name);
    if (match is null)
      return;
    this.ApplySettingsToUi(match.Settings);
    this.UpdatePreview();
    this.SetStatus($"Applied template \"{name}\".");
  }

  private async void OnSaveTemplateClick(object? sender, RoutedEventArgs e) {
    var prompt = new InputDialogWindow("Save develop template", "Name for this template:", string.Empty);
    var name = await prompt.ShowDialog<string?>(this);
    if (string.IsNullOrWhiteSpace(name))
      return;

    try {
      this._templates.Save(new DevelopTemplate(name, this._settings));
    } catch (Exception ex) {
      this.SetStatus($"Save template failed: {ex.Message}");
      return;
    }

    this.RefreshTemplateList(selectName: name);
    this.SetStatus($"Saved template \"{name}\".");
  }

  private async void OnApplyToSelectionClick(object? sender, RoutedEventArgs e) {
    if (this._selectionForApplyAll.Count == 0) {
      this.SetStatus("Select files in the main window before using Apply to selection.");
      return;
    }

    var total = this._selectionForApplyAll.Count;
    this.SetStatus($"Rendering {total} file(s)...");

    // Surface the develop-batch in the main window's status strip when we
    // were given a hook into it. The strip carries its own ProgressBar +
    // Cancel button so the user can abort without searching for this dialog.
    var cts = new CancellationTokenSource();
    var op = new OperationProgress($"Developing {total} files…", cts.Cancel) { Fraction = 0 };
    this._setHostOperation?.Invoke(op);

    var failed = 0;
    var written = 0;
    try {
      var index = 0;
      foreach (var src in this._selectionForApplyAll) {
        if (cts.IsCancellationRequested)
          break;
        if (!src.Exists) {
          index++;
          continue;
        }
        var dest = new FileInfo(Path.Combine(
          src.Directory!.FullName,
          Path.GetFileNameWithoutExtension(src.Name) + "_developed.jpg"));
        try {
          await ImageDeveloper.RenderToJpegAsync(src, dest, this._settings);
          written++;
        } catch {
          failed++;
        }
        index++;
        op.Description = $"Developing {index}/{total} — {src.Name}";
        op.Fraction = (double)index / total;
      }
    } finally {
      this._setHostOperation?.Invoke(null);
      cts.Dispose();
    }

    this.SetStatus(failed == 0
      ? $"Rendered {written} file(s) with current settings."
      : $"Rendered {written}; {failed} failed.");
  }

  // ---------- Film simulation ----------

  private void PopulateFilmSimCombo() {
    if (this.FindControl<ComboBox>("FilmSimCombo") is not { } combo)
      return;
    var names = new List<string> { "(none)" };
    names.AddRange(FilmPreset.All.Select(p => p.Name));
    this._suppressFilmSimSelect = true;
    try {
      combo.ItemsSource = names;
      combo.SelectedIndex = 0;
    } finally {
      this._suppressFilmSimSelect = false;
    }
  }

  private void OnFilmSimSelected(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressFilmSimSelect || this._suppressSliderEvents)
      return;
    if (sender is not ComboBox combo)
      return;
    if (combo.SelectedItem is not string name)
      return;

    if (name == "(none)") {
      // Restore the base settings captured before the film look was applied.
      if (this._filmSimBaseSettings is { } baseSettings) {
        this.ApplySettingsToUi(baseSettings);
        this._filmSimBaseSettings = null;
        this.UpdatePreview();
        this.SetStatus("Film simulation removed.");
      }
      return;
    }

    var preset = FilmPreset.All.FirstOrDefault(p => p.Name == name);
    if (preset is null)
      return;

    // Capture the current settings as the base before applying the film look,
    // but only if we haven't already captured them (switching between presets
    // should re-merge from the original base, not the already-merged state).
    this._filmSimBaseSettings ??= this._settings;

    var merged = FilmPreset.MergeOnto(this._filmSimBaseSettings, preset.Adjustments);
    this.ApplySettingsToUi(merged);
    this.UpdatePreview();
    this.SetStatus($"Applied film simulation \"{name}\": {preset.Description}");
  }

  private async void OnDetectSubjectClick(object? sender, RoutedEventArgs e) {
    if (this._sourceFile is not { } src || !src.Exists) {
      this.SetStatus("No source image loaded.");
      return;
    }

    if (!await this.EnsureModelAsync(ModelRegistry.SubjectMaskMODNet, "subject mask"))
      return;

    this.SetStatus("Detecting subject…");
    try {
      this._subjectSegmenter?.Dispose();
      this._subjectSegmenter = new OnnxSegmentationDetector(ModelRegistry.SubjectMaskMODNet.ResolveDestination());
      if (!this._subjectSegmenter.IsAvailable) {
        this.SetStatus("Subject mask model failed to load.");
        return;
      }

      using var alpha = await this._subjectSegmenter.SegmentAsync(src);
      if (alpha is null) {
        this.SetStatus("Subject detection returned no result.");
        return;
      }

      var dabs = BrushDabsFromAlphaMask.Build(alpha);
      if (dabs.Count == 0) {
        this.SetStatus("No subject pixels found above the threshold.");
        return;
      }

      var current = (this._settings.LocalAdjustments ?? Array.Empty<LocalAdjustment>()).ToList();
      current.Add(new LocalAdjustment(
        Mask: new LocalMask(Type: LocalMaskType.Brush, BrushDabs: dabs),
        Name: "Subject"));
      this._settings = this._settings with { LocalAdjustments = current };
      this.RefreshLocalAdjustmentsList(selectIndex: current.Count - 1);
      this.UpdatePreview();
      this.SetStatus($"Subject mask added ({dabs.Count} dabs).");
    } catch (Exception ex) {
      this.SetStatus($"Subject detection failed: {ex.Message}");
    }
  }

  private async void OnDetectSkyClick(object? sender, RoutedEventArgs e) {
    if (this._previewSource is not { } src) {
      this.SetStatus("No source image loaded.");
      return;
    }

    IReadOnlyList<BrushDab> dabs;

    // Try ONNX model first; fall back to heuristic when model is unavailable.
    if (ModelRegistry.SkySegmenter.IsInstalled()) {
      this.SetStatus("Detecting sky (ONNX model)…");
      try {
        using var segmenter = new OnnxSkySegmenter(ModelRegistry.SkySegmenter.ResolveDestination());
        if (!segmenter.IsAvailable) {
          this.SetStatus("Sky segmenter model failed to load — falling back to heuristic.");
          dabs = HeuristicSkyMask.Build(src);
        } else {
          using var alpha = await segmenter.SegmentSkyAsync(src);
          if (alpha is null) {
            this.SetStatus("Sky detection returned no result — falling back to heuristic.");
            dabs = HeuristicSkyMask.Build(src);
          } else {
            dabs = BrushDabsFromAlphaMask.Build(alpha);
          }
        }
      } catch (Exception ex) {
        this.SetStatus($"ONNX sky detection failed ({ex.Message}) — falling back to heuristic.");
        dabs = HeuristicSkyMask.Build(src);
      }
    } else {
      // Model not installed — offer download, then fall back to heuristic.
      var ok = await this.EnsureModelAsync(ModelRegistry.SkySegmenter, "sky segmenter");
      if (ok) {
        // User downloaded the model — retry with ONNX path.
        OnDetectSkyClick(sender, e);
        return;
      }
      this.SetStatus("Detecting sky (heuristic fallback)…");
      dabs = HeuristicSkyMask.Build(src);
    }

    if (dabs.Count == 0) {
      this.SetStatus("No sky pixels found.");
      return;
    }

    var current = (this._settings.LocalAdjustments ?? Array.Empty<LocalAdjustment>()).ToList();
    current.Add(new LocalAdjustment(
      Mask: new LocalMask(Type: LocalMaskType.Brush, BrushDabs: dabs),
      Name: "Sky"));
    this._settings = this._settings with { LocalAdjustments = current };
    this.RefreshLocalAdjustmentsList(selectIndex: current.Count - 1);
    this.UpdatePreview();
    this.SetStatus($"Sky mask added ({dabs.Count} dabs).");
  }

  /// <summary>
  /// Populates the "Detect class…" flyout from <see cref="Ade20kClasses.FlyoutClasses"/>.
  /// Each menu item wires <see cref="OnDetectAdeClassClick"/> with the class index
  /// stored in the menu item's Tag. Safe to call multiple times — clears first.
  /// </summary>
  private void EnsureDetectClassFlyoutPopulated() {
    if (this.FindControl<Button>("DetectClassButton") is not { Flyout: MenuFlyout flyout })
      return;
    if (flyout.Items.Count > 0)
      return;
    foreach (var cls in Ade20kClasses.FlyoutClasses) {
      var item = new MenuItem {
        Header = $"{cls.Emoji} {cls.DisplayName}",
        Tag = cls
      };
      item.Click += this.OnDetectAdeClassClick;
      flyout.Items.Add(item);
    }
  }

  /// <summary>
  /// Runs the SegFormer-B0 ADE20K segmenter for the picked class and
  /// appends a brush-masked local adjustment named after the class.
  /// Prompts to download the model if it isn't installed.
  /// </summary>
  private async void OnDetectAdeClassClick(object? sender, RoutedEventArgs e) {
    if (sender is not MenuItem item || item.Tag is not Ade20kClasses.AdeClass cls)
      return;
    if (this._previewSource is not { } src) {
      this.SetStatus("No source image loaded.");
      return;
    }

    if (!ModelRegistry.SegformerAde150.IsInstalled()) {
      var ok = await this.EnsureModelAsync(ModelRegistry.SegformerAde150, "multi-class segmenter");
      if (!ok)
        return;
    }

    this.SetStatus($"Detecting {cls.DisplayName.ToLowerInvariant()} (ONNX model)…");
    IReadOnlyList<BrushDab> dabs;
    try {
      using var segmenter = new OnnxAdeSegmenter(ModelRegistry.SegformerAde150.ResolveDestination());
      if (!segmenter.IsAvailable) {
        this.SetStatus("Multi-class segmenter failed to load.");
        return;
      }
      using var alpha = await segmenter.SegmentClassAsync(src, cls.Index);
      if (alpha is null) {
        this.SetStatus($"No {cls.DisplayName.ToLowerInvariant()} pixels found.");
        return;
      }
      dabs = BrushDabsFromAlphaMask.Build(alpha);
    } catch (Exception ex) {
      this.SetStatus($"Detection failed: {ex.Message}");
      return;
    }

    if (dabs.Count == 0) {
      this.SetStatus($"No {cls.DisplayName.ToLowerInvariant()} pixels found.");
      return;
    }

    var current = (this._settings.LocalAdjustments ?? Array.Empty<LocalAdjustment>()).ToList();
    current.Add(new LocalAdjustment(
      Mask: new LocalMask(Type: LocalMaskType.Brush, BrushDabs: dabs),
      Name: cls.DisplayName));
    this._settings = this._settings with { LocalAdjustments = current };
    this.RefreshLocalAdjustmentsList(selectIndex: current.Count - 1);
    this.UpdatePreview();
    this.SetStatus($"{cls.DisplayName} mask added ({dabs.Count} dabs).");
  }

  /// <summary>
  /// Runs Depth Anything V2 on the source, applies a depth-aware Gaussian
  /// blur for portrait bokeh (background separated from the sharp subject),
  /// and saves the result as a sidecar JPEG next to the original. Default
  /// focus = closest pixel (the subject); strength = 14 px max radius.
  /// </summary>
  private async void OnApplyBokehBlurClick(object? sender, RoutedEventArgs e) {
    if (this._sourceFile is not { Exists: true } srcFile) {
      this.SetStatus("No file loaded.");
      return;
    }
    if (this._previewSource is not { } src) {
      this.SetStatus("No source image loaded.");
      return;
    }

    if (!ModelRegistry.DepthAnythingV2Small.IsInstalled()) {
      var ok = await this.EnsureModelAsync(ModelRegistry.DepthAnythingV2Small, "depth estimator");
      if (!ok)
        return;
    }

    this.SetStatus("Estimating depth (Depth Anything V2)…");
    DepthMap? depth;
    try {
      using var estimator = new OnnxDepthEstimator(ModelRegistry.DepthAnythingV2Small.ResolveDestination());
      if (!estimator.IsAvailable) {
        this.SetStatus("Depth estimator failed to load.");
        return;
      }
      depth = await estimator.EstimateAsync(src);
    } catch (Exception ex) {
      this.SetStatus($"Depth estimation failed: {ex.Message}");
      return;
    }
    if (depth is null) {
      this.SetStatus("Depth estimator returned no result.");
      return;
    }

    this.SetStatus("Applying depth-aware bokeh blur…");
    Image<Rgba32>? blurred;
    try {
      blurred = await Task.Run(() => DepthBokehBlur.Apply(src, depth, focus: 1.0, strength: 14.0));
    } catch (Exception ex) {
      this.SetStatus($"Bokeh blur failed: {ex.Message}");
      return;
    }

    var outName = Path.Combine(srcFile.DirectoryName ?? ".", Path.GetFileNameWithoutExtension(srcFile.Name) + ".bokeh.jpg");
    try {
      await blurred.SaveAsJpegAsync(outName);
    } catch (Exception ex) {
      this.SetStatus($"Failed to save bokeh output: {ex.Message}");
      return;
    } finally {
      blurred.Dispose();
    }

    this.SetStatus($"Bokeh saved: {Path.GetFileName(outName)}");
  }

  /// <summary>
  /// Runs Zero-DCE++ on the source to brighten under-exposed regions, and
  /// saves the result as a sidecar JPEG next to the original. Tiny model
  /// (~52 KB), runs in milliseconds even on CPU.
  /// </summary>
  private async void OnBrightenLowLightClick(object? sender, RoutedEventArgs e) {
    if (this._sourceFile is not { Exists: true } srcFile) {
      this.SetStatus("No file loaded.");
      return;
    }
    if (this._previewSource is not { } src) {
      this.SetStatus("No source image loaded.");
      return;
    }

    if (!ModelRegistry.ZeroDcePp.IsInstalled()) {
      var ok = await this.EnsureModelAsync(ModelRegistry.ZeroDcePp, "low-light enhancer");
      if (!ok)
        return;
    }

    this.SetStatus("Enhancing low-light (Zero-DCE++)…");
    Image<Rgba32>? enhanced;
    try {
      using var enhancer = new OnnxLowLightEnhancer(ModelRegistry.ZeroDcePp.ResolveDestination());
      if (!enhancer.IsAvailable) {
        this.SetStatus("Low-light enhancer failed to load.");
        return;
      }
      enhanced = await enhancer.EnhanceAsync(src);
    } catch (Exception ex) {
      this.SetStatus($"Enhancement failed: {ex.Message}");
      return;
    }
    if (enhanced is null) {
      this.SetStatus("Enhancement returned no result.");
      return;
    }

    var outName = Path.Combine(srcFile.DirectoryName ?? ".", Path.GetFileNameWithoutExtension(srcFile.Name) + ".lowlight.jpg");
    try {
      await enhanced.SaveAsJpegAsync(outName);
    } catch (Exception ex) {
      this.SetStatus($"Failed to save enhanced output: {ex.Message}");
      return;
    } finally {
      enhanced.Dispose();
    }

    this.SetStatus($"Enhanced: {Path.GetFileName(outName)}");
  }

  /// <summary>
  /// Runs AOD-Net dehazing on the source to remove atmospheric haze, and
  /// saves the result as a sidecar JPEG next to the original.
  /// </summary>
  private async void OnDehazeClick(object? sender, RoutedEventArgs e) {
    if (this._sourceFile is not { Exists: true } srcFile) {
      this.SetStatus("No file loaded.");
      return;
    }
    if (this._previewSource is not { } src) {
      this.SetStatus("No source image loaded.");
      return;
    }

    if (!ModelRegistry.AodNetDehazer.IsInstalled()) {
      var ok = await this.EnsureModelAsync(ModelRegistry.AodNetDehazer, "dehazer");
      if (!ok)
        return;
    }

    this.SetStatus("Dehazing (AOD-Net)…");
    Image<Rgba32>? dehazed;
    try {
      using var dehazer = new OnnxDehazer(ModelRegistry.AodNetDehazer.ResolveDestination());
      if (!dehazer.IsAvailable) {
        this.SetStatus("Dehazer failed to load.");
        return;
      }
      dehazed = await dehazer.DehazeAsync(src);
    } catch (Exception ex) {
      this.SetStatus($"Dehazing failed: {ex.Message}");
      return;
    }
    if (dehazed is null) {
      this.SetStatus("Dehazing returned no result.");
      return;
    }

    var outName = Path.Combine(srcFile.DirectoryName ?? ".", Path.GetFileNameWithoutExtension(srcFile.Name) + ".dehazed.jpg");
    try {
      await dehazed.SaveAsJpegAsync(outName);
    } catch (Exception ex) {
      this.SetStatus($"Failed to save dehazed output: {ex.Message}");
      return;
    } finally {
      dehazed.Dispose();
    }

    this.SetStatus($"Dehazed: {Path.GetFileName(outName)}");
  }

  /// <summary>
  /// Runs NIMA (MobileNetV2) on the current source and shows the
  /// aesthetic score in the status bar. Mean ~5 is average snapshot
  /// quality; >6.5 is portfolio-worthy.
  /// </summary>
  private async void OnScoreAestheticClick(object? sender, RoutedEventArgs e) {
    if (this._previewSource is not { } src) {
      this.SetStatus("No source image loaded.");
      return;
    }

    if (!ModelRegistry.NimaMobileNetV2.IsInstalled()) {
      var ok = await this.EnsureModelAsync(ModelRegistry.NimaMobileNetV2, "aesthetic scorer");
      if (!ok)
        return;
    }

    this.SetStatus("Scoring (NIMA)…");
    try {
      using var scorer = new OnnxAestheticScorer(ModelRegistry.NimaMobileNetV2.ResolveDestination());
      if (!scorer.IsAvailable) {
        this.SetStatus("Aesthetic scorer failed to load.");
        return;
      }
      var result = await scorer.ScoreAsync(src);
      if (result is null) {
        this.SetStatus("Aesthetic scoring returned no result.");
        return;
      }
      this.SetStatus($"Aesthetic score: {result.Mean:F2} / 10  (±{result.StdDev:F2})");
    } catch (Exception ex) {
      this.SetStatus($"Aesthetic scoring failed: {ex.Message}");
    }
  }

  /// <summary>
  /// One-click "fix everything" pipeline. Detects which restoration
  /// stages apply via <see cref="PhotoIssueDetector"/>, runs only those
  /// stages, writes the enhanced image as <c>IMG.enhanced.jpg</c> next
  /// to the source, and reports the NIMA score delta in the status bar.
  /// </summary>
  private async void OnMagicEnhanceClick(object? sender, RoutedEventArgs e) {
    if (this._sourceFile is not { Exists: true } srcFile) {
      this.SetStatus("No file loaded.");
      return;
    }
    if (this._previewSource is not { } src) {
      this.SetStatus("No source image loaded.");
      return;
    }

    this.SetStatus("Magic Enhance — analysing…");
    MagicEnhanceResult result;
    try {
      var progress = new Progress<string>(stage =>
        Dispatcher.UIThread.Post(() => this.SetStatus($"Magic Enhance — {stage}…")));
      result = await MagicEnhancer.EnhanceAsync(src, options: null, ct: default, progress: progress);
    } catch (Exception ex) {
      this.SetStatus($"Magic Enhance failed: {ex.Message}");
      return;
    }

    var outName = Path.Combine(srcFile.DirectoryName ?? ".", Path.GetFileNameWithoutExtension(srcFile.Name) + ".enhanced.jpg");
    try {
      await result.Enhanced.SaveAsJpegAsync(outName);
    } catch (Exception ex) {
      this.SetStatus($"Failed to save enhanced output: {ex.Message}");
      return;
    } finally {
      result.Enhanced.Dispose();
    }

    var stages = string.Join(", ", result.Log.Where(l => l.StartsWith("✓")).Select(l => l.Substring(2)));
    if (stages.Length == 0)
      stages = "(no stages applied)";
    var nimaDelta = result.NimaScoreBefore.HasValue && result.NimaScoreAfter.HasValue
      ? $" — NIMA {result.NimaScoreBefore.Value:F2}→{result.NimaScoreAfter.Value:F2} ({(result.NimaScoreAfter.Value - result.NimaScoreBefore.Value):+0.00;-0.00})"
      : string.Empty;
    this.SetStatus($"Enhanced: {Path.GetFileName(outName)} • {stages}{nimaDelta}");
  }

  /// <summary>
  /// Runs NAFNet-GoPro on the source to recover from camera-shake or
  /// subject-motion blur, and saves the result as a sidecar JPEG. The
  /// SOTA defocus-only model (DPDNet / IFAN) isn't in the catalog yet —
  /// for now this is the all-purpose deblur option.
  /// </summary>
  private async void OnMotionDeblurClick(object? sender, RoutedEventArgs e) {
    if (this._sourceFile is not { Exists: true } srcFile) {
      this.SetStatus("No file loaded.");
      return;
    }
    if (this._previewSource is not { } src) {
      this.SetStatus("No source image loaded.");
      return;
    }

    if (!ModelRegistry.NafnetGoPro.IsInstalled()) {
      var ok = await this.EnsureModelAsync(ModelRegistry.NafnetGoPro, "motion deblur");
      if (!ok)
        return;
    }

    this.SetStatus("Deblurring (NAFNet-GoPro)…");
    Image<Rgba32>? deblurred;
    try {
      using var denoiser = new OnnxDenoiser(ModelRegistry.NafnetGoPro.ResolveDestination());
      if (!denoiser.IsAvailable) {
        this.SetStatus("Deblurrer failed to load.");
        return;
      }
      deblurred = await Task.Run(() => denoiser.Denoise(src, strength: 1.0));
    } catch (Exception ex) {
      this.SetStatus($"Deblur failed: {ex.Message}");
      return;
    }
    if (deblurred is null) {
      this.SetStatus("Deblur returned no result.");
      return;
    }

    var outName = Path.Combine(srcFile.DirectoryName ?? ".", Path.GetFileNameWithoutExtension(srcFile.Name) + ".deblurred.jpg");
    try {
      await deblurred.SaveAsJpegAsync(outName);
    } catch (Exception ex) {
      this.SetStatus($"Failed to save deblurred output: {ex.Message}");
      return;
    } finally {
      deblurred.Dispose();
    }

    this.SetStatus($"Deblurred: {Path.GetFileName(outName)}");
  }

  /// <summary>
  /// Runs <see cref="NimaCropSuggester"/> to enumerate standard-aspect
  /// crops, score each with NIMA, and save the best one as a sidecar
  /// JPEG. The top 3 ranked suggestions are listed in the status bar
  /// for comparison.
  /// </summary>
  private async void OnSuggestCropsClick(object? sender, RoutedEventArgs e) {
    if (this._sourceFile is not { Exists: true } srcFile) {
      this.SetStatus("No file loaded.");
      return;
    }
    if (this._previewSource is not { } src) {
      this.SetStatus("No source image loaded.");
      return;
    }

    if (!ModelRegistry.NimaMobileNetV2.IsInstalled()) {
      var ok = await this.EnsureModelAsync(ModelRegistry.NimaMobileNetV2, "aesthetic scorer");
      if (!ok)
        return;
    }

    this.SetStatus("Scoring candidate crops…");
    IReadOnlyList<AestheticCropSuggestion> suggestions;
    try {
      using var scorer = new OnnxAestheticScorer(ModelRegistry.NimaMobileNetV2.ResolveDestination());
      if (!scorer.IsAvailable) {
        this.SetStatus("Aesthetic scorer failed to load.");
        return;
      }
      suggestions = await NimaCropSuggester.SuggestAsync(src, scorer, topK: 3);
    } catch (Exception ex) {
      this.SetStatus($"Crop suggestions failed: {ex.Message}");
      return;
    }

    if (suggestions.Count == 0) {
      this.SetStatus("No crop suggestions produced.");
      return;
    }

    var best = suggestions[0];
    using var cropped = src.Clone(c => c.Crop(best.Rectangle));
    var outName = Path.Combine(srcFile.DirectoryName ?? ".", Path.GetFileNameWithoutExtension(srcFile.Name) + ".cropped.jpg");
    try {
      await cropped.SaveAsJpegAsync(outName);
    } catch (Exception ex) {
      this.SetStatus($"Failed to save cropped output: {ex.Message}");
      return;
    }

    var ranked = string.Join("  •  ", suggestions.Select((s, i) =>
      $"{i + 1}. {s.AspectName} {s.Rectangle.Width}×{s.Rectangle.Height} → {s.Score:F2}"));
    this.SetStatus($"Best: {Path.GetFileName(outName)}  |  {ranked}");
  }

  /// <summary>
  /// Adds an Inpaint-type local adjustment (content-aware fill). The user
  /// paints the mask using the existing brush overlay, and the region is
  /// fed to LaMa inpainting at render time. Each removal is one undo entry.
  /// </summary>
  private async void OnRemoveObjectClick(object? sender, RoutedEventArgs e) {
    if (this._previewSource is null) {
      this.SetStatus("No source image loaded.");
      return;
    }

    if (!await this.EnsureModelAsync(ModelRegistry.LamaInpaint, "LaMa inpainting"))
      return;

    var current = (this._settings.LocalAdjustments ?? Array.Empty<LocalAdjustment>()).ToList();
    current.Add(new LocalAdjustment(
      Mask: new LocalMask(Type: LocalMaskType.Inpaint, BrushDabs: Array.Empty<BrushDab>()),
      Name: $"Remove {current.Count(a => a.Mask.Type == LocalMaskType.Inpaint) + 1}"));
    this._settings = this._settings with { LocalAdjustments = current };
    this.RefreshLocalAdjustmentsList(selectIndex: current.Count - 1);
    this.UpdatePreview();
    this.SetStatus("Remove Object: paint over the object to remove, then the inpainter will fill it.");
  }

  /// Mirror of MainWindow's EnsureModelAsync: prompts the user via the
  /// ModelDownloadWindow if the requested model isn't installed, returning
  /// true when the file is present after the dialog closes.
  private async Task<bool> EnsureModelAsync(ModelInfo model, string friendlyName) {
    if (model.IsInstalled())
      return true;

    this.SetStatus($"The {friendlyName} model isn't installed yet — opening the downloader.");

    var window = new ModelDownloadWindow();
    await window.ShowDialog(this);

    if (model.IsInstalled())
      return true;

    this.SetStatus($"{friendlyName} model still not installed — click Download or Install from file and try again.");
    return false;
  }

  // ---------- Compare mode (After / Split / Slider) ----------

  private void OnCompareToggleClick(object? sender, RoutedEventArgs e) {
    this._compareState.Cycle();
    this.ApplyCompareModeUi();
    if (this._compareState.NeedsBaseline && this._baselinePreview is null)
      _ = this.RebuildBaselineAsync();
  }

  private void ApplyCompareModeUi() {
    if (this.FindControl<Avalonia.Controls.Image>("PreviewImage") is { } single)
      single.IsVisible = this._compareState.IsAfterVisible;
    if (this.FindControl<Grid>("SplitPanel") is { } split)
      split.IsVisible = this._compareState.IsSplitVisible;
    if (this.FindControl<Grid>("OverlayPanel") is { } overlay)
      overlay.IsVisible = this._compareState.IsOverlayVisible;
    if (this.FindControl<Grid>("SliderPanel") is { } slider)
      slider.IsVisible = this._compareState.IsSliderVisible;
    // The control bars for Overlay (alpha slider) and Slider (wipe handle)
    // live OUTSIDE the zoom/pan transform so they stay at fixed screen
    // positions when the user zooms the image. Toggled in parallel with
    // the matching image-content panels.
    if (this.FindControl<Border>("OverlayControlsBar") is { } overlayCtrls)
      overlayCtrls.IsVisible = this._compareState.IsOverlayVisible;
    if (this.FindControl<Canvas>("SliderHandleCanvas") is { } sliderCanvas)
      sliderCanvas.IsVisible = this._compareState.IsSliderVisible;
    if (this.FindControl<Button>("CompareToggleButton") is { } btn) {
      btn.Content = this._compareState.ButtonContent;
      ToolTip.SetTip(btn, this._compareState.ButtonTooltip);
    }
    if (this._compareState.Mode == CompareMode.Slider) {
      this.UpdateSliderClip();
      Dispatcher.UIThread.Post(this.UpdateSliderClip, DispatcherPriority.Loaded);
    }
  }

  /// <summary>
  /// Overlay alpha slider — sets the developed image's Opacity 0..1 over
  /// the baseline so the user can dial a partial blend.
  /// </summary>
  private void OnOverlayAlphaChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (sender is not Slider slider) return;
    var a = Math.Clamp(slider.Value, 0, 1);
    if (this.FindControl<Avalonia.Controls.Image>("PreviewImageOverlay") is { } preview)
      preview.Opacity = a;
    if (this.FindControl<TextBlock>("OverlayAlphaLabel") is { } lbl)
      lbl.Text = $"α={a:F2}";
  }

  /// <summary>Normalised position 0..1 of the wipe slider handle in CompareRoot
  /// screen coordinates. The wipe handle stays at this fraction of the screen
  /// width regardless of zoom/pan; the clip on the developed image is recomputed
  /// from this + the current transform so the wipe edge ALWAYS lines up with
  /// the visible Thumb position.</summary>
  private double _sliderWipeFraction = 0.5;

  /// <summary>
  /// Recompute the wipe clip on PreviewImageSlider so that the clip edge,
  /// AFTER the PreviewTransformRoot scale + translate is applied, lands at
  /// the Thumb's screen X position. Called on:
  ///   - drag of the Thumb
  ///   - wheel zoom
  ///   - right-drag pan
  ///   - double-right-click reset
  ///   - SizeChanged on PreviewImageSlider (initial layout)
  /// </summary>
  private void UpdateSliderClip() {
    if (this.FindControl<Avalonia.Controls.Image>("PreviewImageSlider") is not { } preview)
      return;
    if (this.FindControl<Canvas>("SliderHandleCanvas") is not { } canvas)
      return;

    var imageW = preview.Bounds.Width;
    var imageH = preview.Bounds.Height;
    var canvasW = canvas.Bounds.Width;
    var canvasH = canvas.Bounds.Height;
    if (imageW <= 0 || imageH <= 0 || canvasW <= 0) {
      preview.Clip = null;
      return;
    }

    // Thumb sits in CompareRoot screen coordinates (canvas isn't
    // inside the transform).
    var thumbScreenX = Math.Clamp(this._sliderWipeFraction, 0, 1) * canvasW;

    // Map screen X back into the IMAGE control's local coordinate space.
    // The transform pipeline is:
    //   screen_x = local_x * scale + translate.X
    // ⇒ local_x = (screen_x - translate.X) / scale
    var (scale, translateX) = this.GetPreviewTransforms() is { } tx
      ? (tx.Scale.ScaleX, tx.Translate.X)
      : (1.0, 0.0);
    if (scale <= 0) scale = 1.0;

    var clipLocalX = Math.Clamp((thumbScreenX - translateX) / scale, 0, imageW);
    preview.Clip = new Avalonia.Media.RectangleGeometry(
      new Avalonia.Rect(clipLocalX, 0, imageW - clipLocalX, imageH));

    // Snap the Thumb to the screen position so its visible centre lines
    // up with the wipe edge. (Drag offsets are applied to _sliderWipeFraction
    // BEFORE this method runs, so we're just confirming the layout here.)
    if (this.FindControl<Avalonia.Controls.Primitives.Thumb>("SliderHandle") is { } thumb) {
      Canvas.SetLeft(thumb, thumbScreenX - thumb.Bounds.Width / 2);
      thumb.Height = canvasH;
    }
  }

  /// <summary>Pin the wipe Thumb's height to the host canvas height so the vertical line spans the full preview.</summary>
  private void OnSliderHandleDragStarted(object? sender, Avalonia.Input.VectorEventArgs e) {
    if (this.FindControl<Canvas>("SliderHandleCanvas") is { } canvas
        && this.FindControl<Avalonia.Controls.Primitives.Thumb>("SliderHandle") is { } thumb)
      thumb.Height = canvas.Bounds.Height;
  }

  /// <summary>Drag the wipe handle — convert pointer delta into a 0..1 fraction
  /// and re-clip the developed image so the wipe edge follows the cursor.</summary>
  private void OnSliderHandleDragDelta(object? sender, Avalonia.Input.VectorEventArgs e) {
    if (this.FindControl<Canvas>("SliderHandleCanvas") is not { } canvas) return;
    if (sender is not Avalonia.Controls.Primitives.Thumb thumb) return;
    var canvasW = canvas.Bounds.Width;
    if (canvasW <= 0) return;
    var currentLeft = Canvas.GetLeft(thumb);
    if (double.IsNaN(currentLeft)) currentLeft = canvasW / 2 - thumb.Bounds.Width / 2;
    var newLeft = Math.Clamp(currentLeft + e.Vector.X, -thumb.Bounds.Width / 2, canvasW - thumb.Bounds.Width / 2);
    Canvas.SetLeft(thumb, newLeft);
    this._sliderWipeFraction = Math.Clamp((newLeft + thumb.Bounds.Width / 2) / canvasW, 0, 1);
    this.UpdateSliderClip();
  }

  // ---------- Zoom + pan ----------

  private double _previewZoom = 1.0;
  private double _previewPanX;
  private double _previewPanY;
  private Avalonia.Point? _panStartCursor;
  private double _panStartX;
  private double _panStartY;

  /// <summary>Read the (scale, translate) pair from PreviewTransformRoot's TransformGroup.</summary>
  private (Avalonia.Media.ScaleTransform Scale, Avalonia.Media.TranslateTransform Translate)? GetPreviewTransforms() {
    if (this.FindControl<Grid>("PreviewTransformRoot") is not { RenderTransform: Avalonia.Media.TransformGroup grp })
      return null;
    Avalonia.Media.ScaleTransform? scale = null;
    Avalonia.Media.TranslateTransform? translate = null;
    foreach (var t in grp.Children) {
      if (t is Avalonia.Media.ScaleTransform s) scale = s;
      else if (t is Avalonia.Media.TranslateTransform tr) translate = tr;
    }
    if (scale is null || translate is null) return null;
    return (scale, translate);
  }

  /// <summary>Mouse wheel = zoom anchored at the cursor position.</summary>
  private void OnPreviewWheelChanged(object? sender, Avalonia.Input.PointerWheelEventArgs e) {
    if (this.FindControl<Grid>("CompareRoot") is not { } root) return;
    if (this.GetPreviewTransforms() is not { } tx) return;
    var scale = tx.Scale;
    var translate = tx.Translate;

    var oldZoom = this._previewZoom;
    var step = e.Delta.Y > 0 ? 1.20 : 1.0 / 1.20;
    var newZoom = Math.Clamp(oldZoom * step, 0.1, 32.0);
    if (Math.Abs(newZoom - oldZoom) < 1e-6) return;

    // Anchor the zoom at the cursor so the point under the mouse stays
    // under the mouse: solve   newPan = cursor - (cursor - oldPan) * (newZoom / oldZoom)
    var cursor = e.GetPosition(root);
    var ratio = newZoom / oldZoom;
    this._previewPanX = cursor.X - (cursor.X - this._previewPanX) * ratio;
    this._previewPanY = cursor.Y - (cursor.Y - this._previewPanY) * ratio;
    this._previewZoom = newZoom;
    scale.ScaleX = newZoom;
    scale.ScaleY = newZoom;
    translate.X = this._previewPanX;
    translate.Y = this._previewPanY;
    if (this._compareState.Mode == CompareMode.Slider)
      this.UpdateSliderClip();
    this.UpdateZoomLabel();
    e.Handled = true;
  }

  /// <summary>Right-click + drag begins a pan gesture; double-right-click resets zoom + pan to 100%.</summary>
  private void OnPreviewPointerPressedForPan(object? sender, Avalonia.Input.PointerPressedEventArgs e) {
    if (this.FindControl<Grid>("CompareRoot") is not { } root) return;
    var pp = e.GetCurrentPoint(root);
    if (!pp.Properties.IsRightButtonPressed) return;

    // Double-right-click — snap back to 100 % zoom / no pan.
    if (e.ClickCount >= 2) {
      this._previewZoom = 1.0;
      this._previewPanX = 0;
      this._previewPanY = 0;
      if (this.GetPreviewTransforms() is { } tx) {
        tx.Scale.ScaleX = 1;
        tx.Scale.ScaleY = 1;
        tx.Translate.X = 0;
        tx.Translate.Y = 0;
      }
      if (this._compareState.Mode == CompareMode.Slider)
        this.UpdateSliderClip();
      this.UpdateZoomLabel();
      e.Handled = true;
      return;
    }

    this._panStartCursor = pp.Position;
    this._panStartX = this._previewPanX;
    this._panStartY = this._previewPanY;
    e.Pointer.Capture(root);
    e.Handled = true;
  }

  private void OnPreviewPointerMovedForPan(object? sender, Avalonia.Input.PointerEventArgs e) {
    if (this._panStartCursor is not { } start) return;
    if (this.FindControl<Grid>("CompareRoot") is not { } root) return;
    if (this.GetPreviewTransforms() is not { } tx) return;
    var cur = e.GetPosition(root);
    this._previewPanX = this._panStartX + (cur.X - start.X);
    this._previewPanY = this._panStartY + (cur.Y - start.Y);
    tx.Translate.X = this._previewPanX;
    tx.Translate.Y = this._previewPanY;
    if (this._compareState.Mode == CompareMode.Slider)
      this.UpdateSliderClip();
    e.Handled = true;
  }

  private void OnPreviewPointerReleasedForPan(object? sender, Avalonia.Input.PointerReleasedEventArgs e) {
    if (this._panStartCursor is null) return;
    if (e.InitialPressMouseButton != Avalonia.Input.MouseButton.Right) return;
    this._panStartCursor = null;
    e.Pointer.Capture(null);
    e.Handled = true;
  }

  private void UpdateZoomLabel() {
    if (this.FindControl<TextBlock>("PreviewZoomLabel") is { } lbl)
      lbl.Text = $"zoom {this._previewZoom * 100:F0}% · wheel=zoom, right-drag=pan, double-right-click=reset";
  }

  private async Task RebuildBaselineAsync() {
    if (this._previewSource is not { } src)
      return;

    var clone = src.Clone();
    byte[]? jpegBytes = null;
    try {
      await Task.Run(() => {
        using var rendered = ImageDeveloper.Apply(clone, new DevelopSettings());
        using var ms = new MemoryStream();
        rendered.SaveAsJpeg(ms);
        jpegBytes = ms.ToArray();
      });
    } finally {
      clone.Dispose();
    }

    if (jpegBytes is null)
      return;

    Dispatcher.UIThread.Post(() => {
      // Build a fresh Bitmap per Image control (same defensive pattern as
      // PaintPreviewImages — sharing a Bitmap reference between Image
      // controls produced a "compare panels show baseline only" bug).
      this._baselinePreview?.Dispose();
      using (var msKeep = new MemoryStream(jpegBytes, writable: false))
        this._baselinePreview = new Bitmap(msKeep);
      if (this.FindControl<Avalonia.Controls.Image>("BaselineImageSplit") is { } a) {
        using var freshStream = new MemoryStream(jpegBytes, writable: false);
        a.Source = new Bitmap(freshStream);
      }
      if (this.FindControl<Avalonia.Controls.Image>("BaselineImageOverlay") is { } c) {
        using var freshStream = new MemoryStream(jpegBytes, writable: false);
        c.Source = new Bitmap(freshStream);
      }
      if (this.FindControl<Avalonia.Controls.Image>("BaselineImageSlider") is { } b) {
        using var freshStream = new MemoryStream(jpegBytes, writable: false);
        b.Source = new Bitmap(freshStream);
      }
    });
  }

  // ---------- Crop drag-handles + Look (3D LUT) ----------

  private void OnCropAngleUpDownChanged(object? sender, NumericUpDownValueChangedEventArgs e) {
    if (this._suppressSliderEvents) return;
    var val = (double?)this.FindControl<NumericUpDown>("CropAngleUpDown")?.Value ?? 0;
    this._suppressSliderEvents = true;
    try {
      if (this.FindControl<Slider>("CropAngleSlider") is { } slider)
        slider.Value = val;
    } finally {
      this._suppressSliderEvents = false;
    }
    this._settings = this._settings with { CropAngleDegrees = val };
    this.SchedulePreviewUpdate();
  }

  private void OnShowCropHandlesChanged(object? sender, RoutedEventArgs e) {
    this.SyncCropOverlay();
  }

  private void OnCropOverlayChanged(object? sender, EventArgs e) {
    if (this.FindControl<CropOverlayCanvas>("CropOverlay") is not { } overlay)
      return;
    this._settings = this._settings with {
      CropLeft   = overlay.CropLeft,
      CropTop    = overlay.CropTop,
      CropRight  = overlay.CropRight,
      CropBottom = overlay.CropBottom
    };
    this._suppressSliderEvents = true;
    try {
      if (this.FindControl<NumericUpDown>("CropLeftBox")   is { } l) l.Value = (decimal)overlay.CropLeft;
      if (this.FindControl<NumericUpDown>("CropTopBox")    is { } t) t.Value = (decimal)overlay.CropTop;
      if (this.FindControl<NumericUpDown>("CropRightBox")  is { } r) r.Value = (decimal)overlay.CropRight;
      if (this.FindControl<NumericUpDown>("CropBottomBox") is { } b) b.Value = (decimal)overlay.CropBottom;
    } finally {
      this._suppressSliderEvents = false;
    }
    this.SchedulePreviewUpdate();
  }

  private void SyncCropOverlay() {
    if (this.FindControl<CropOverlayCanvas>("CropOverlay") is not { } overlay)
      return;
    overlay.SetCrop(this._settings.CropLeft, this._settings.CropTop, this._settings.CropRight, this._settings.CropBottom);

    // The crop overlay (with drag handles) should ONLY be visible and
    // interactive when the user explicitly toggles "Show crop handles".
    // Previously it also appeared whenever crop values were non-default
    // (e.g. after auto-crop), which made the overlay intercept mouse
    // events and interfere with other tools (geometry drag, painting).
    var toggleOn = this.FindControl<ToggleButton>("ShowCropHandlesToggle")?.IsChecked == true;
    overlay.IsVisible = toggleOn;
    overlay.IsHitTestVisible = toggleOn;
  }

  private void RefreshLookList() {
    if (this.FindControl<ComboBox>("LookCombo") is not { } combo)
      return;
    var dir = Hawkynt.PhotoManager.Core.AppDataPaths.SubDirectory("luts");
    var entries = new List<string> { "(no look)" };
    try {
      foreach (var ext in new[] { "*.cube", "*.3dl" })
        foreach (var f in dir.EnumerateFiles(ext, SearchOption.TopDirectoryOnly).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
          entries.Add(f.Name);
    } catch { /* best-effort */ }

    this._suppressSliderEvents = true;
    try {
      combo.ItemsSource = entries;
      var current = this._settings.LookName;
      var idx = string.IsNullOrEmpty(current) ? 0 : entries.IndexOf(current!);
      combo.SelectedIndex = idx < 0 ? 0 : idx;
    } finally {
      this._suppressSliderEvents = false;
    }
  }

  private void OnRefreshLooksClick(object? sender, RoutedEventArgs e) {
    this.RefreshLookList();
    this.SetStatus("Look list refreshed.");
  }

  private void OnLookSelected(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    if (sender is not ComboBox combo)
      return;
    var picked = combo.SelectedItem as string;
    var lookName = string.IsNullOrEmpty(picked) || picked == "(no look)" ? null : picked;
    this._settings = this._settings with { LookName = lookName };
    this.SchedulePreviewUpdate();
  }

  private void OnLookOpacityChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    var v = e.NewValue;
    this._settings = this._settings with { LookOpacity = v };
    if (this.FindControl<TextBlock>("LookOpacityValue") is { } label)
      label.Text = v.ToString("0.00", CultureInfo.InvariantCulture);
    this.SchedulePreviewUpdate();
  }

  private void InvalidateBaseline() {
    this._baselinePreview?.Dispose();
    this._baselinePreview = null;
    if (this._compareState.NeedsBaseline)
      _ = this.RebuildBaselineAsync();
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) {
    // Stop any in-flight AI render so the background task drops out
    // promptly instead of writing to a disposed preview reference.
    var cts = Interlocked.Exchange(ref this._aiPreviewCts, null);
    cts?.Cancel();
    cts?.Dispose();
    this._previewSource?.Dispose();
    this._developedPreview?.Dispose();
    this._baselinePreview?.Dispose();
    this._subjectSegmenter?.Dispose();
    this.Close();
  }

  // ---------- Edit history (inline panel + separate dialog) ----------

  private readonly System.Collections.ObjectModel.ObservableCollection<HistorySnapshotRow> _historyRows = new();

  private async void OnHistoryToggleChanged(object? sender, RoutedEventArgs e) {
    if (this.FindControl<Border>("HistoryPanel") is not { } panel)
      return;
    var isChecked = (sender as ToggleButton)?.IsChecked == true;
    panel.IsVisible = isChecked;
    if (isChecked)
      await this.RefreshInlineHistoryAsync();
  }

  private async Task RefreshInlineHistoryAsync() {
    if (this._sourceFile is not { Exists: true } file)
      return;
    if (this.FindControl<ListBox>("HistoryList") is { } list)
      list.ItemsSource = this._historyRows;
    IReadOnlyList<DevelopSnapshot> history;
    try {
      history = await DevelopMetadataStore.LoadHistoryAsync(file, this._copyIndex);
    } catch {
      this._historyRows.Clear();
      return;
    }
    this._historyRows.Clear();
    var all = DevelopHistory.GetAll(history);
    for (var i = 0; i < all.Count; i++)
      this._historyRows.Add(new HistorySnapshotRow(i, all[i], thumbnail: null));
  }

  private void OnHistoryEntryDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e) {
    if (this.FindControl<ListBox>("HistoryList")?.SelectedItem is not HistorySnapshotRow row)
      return;
    var settings = DevelopHistory.RollbackTo(
      this._historyRows.Select(r => r.Snapshot).ToList(), row.Index);
    this.ApplySettingsToUi(settings);
    this.UpdatePreview();
    var label = string.IsNullOrWhiteSpace(row.Snapshot.Label) ? "snapshot" : $"\"{row.Snapshot.Label}\"";
    this.SetStatus($"Restored {label} from {row.Snapshot.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}. Adjust further or Save As… to keep.");
  }

  private async void OnEditHistoryClick(object? sender, RoutedEventArgs e) {
    if (this._sourceFile is not { Exists: true } file) {
      this.SetStatus("Open a file first.");
      return;
    }
    IReadOnlyList<DevelopSnapshot> history;
    try {
      history = await DevelopMetadataStore.LoadHistoryAsync(file, this._copyIndex);
    } catch (Exception ex) {
      this.SetStatus($"Couldn't read history: {ex.Message}");
      return;
    }

    var window = new EditHistoryWindow(file, this._copyIndex, this._previewSource, history);
    await window.ShowDialog(this);
    if (window.RestoredSnapshot is not { } picked)
      return;
    this.ApplySettingsToUi(picked.Settings);
    this.UpdatePreview();
    var label = string.IsNullOrWhiteSpace(picked.Label) ? "snapshot" : $"\"{picked.Label}\"";
    this.SetStatus($"Restored {label} from {picked.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}. Adjust further or Save As… to keep.");
    // Refresh the inline panel if it's open.
    if (this.FindControl<ToggleButton>("HistoryToggleButton")?.IsChecked == true)
      await this.RefreshInlineHistoryAsync();
  }

  // ---------- Virtual copies ----------

  private async void OnCreateVirtualCopyClick(object? sender, RoutedEventArgs e) {
    if (this._sourceFile is not { Exists: true } file) {
      this.SetStatus("Open a file first.");
      return;
    }
    var nextIndex = VirtualCopyDiscovery.NextAvailableIndex(file);
    var ok = await DevelopMetadataStore.SaveAsync(file, this._settings, nextIndex,
      snapshotLabel: "Created virtual copy");
    if (!ok) {
      this.SetStatus($"Failed to create virtual copy {nextIndex}.");
      return;
    }
    this._copyIndex = nextIndex;
    this.UpdateCopyTitle();
    this.RefreshCopiesCombo();
    this.SetStatus($"Created virtual copy {nextIndex} ({file.Name}.copy{nextIndex}.xmp). Window now targets this copy.");
  }

  private async void OnDeleteVirtualCopyClick(object? sender, RoutedEventArgs e) {
    if (this._sourceFile is not { Exists: true } file || this._copyIndex < 1) {
      this.SetStatus("Select a virtual copy first.");
      return;
    }
    try {
      var sidecar = VirtualCopyDiscovery.SidecarFor(file, this._copyIndex);
      if (sidecar.Exists)
        sidecar.Delete();
    } catch (Exception ex) {
      this.SetStatus($"Delete failed: {ex.Message}");
      return;
    }
    this.SetStatus($"Deleted virtual copy {this._copyIndex}.");
    this._copyIndex = 0;
    this.UpdateCopyTitle();
    this.RefreshCopiesCombo();
    await this.ReloadSettingsForCurrentCopyAsync();
  }

  private async void OnCopiesComboSelectionChanged(object? sender, SelectionChangedEventArgs e) {
    if (this._suppressSliderEvents)
      return;
    if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item)
      return;
    if (item.Tag is not int newIndex || newIndex == this._copyIndex)
      return;
    this._copyIndex = newIndex;
    this.UpdateCopyTitle();
    if (this.FindControl<Button>("DeleteCopyButton") is { } delBtn)
      delBtn.IsVisible = this._copyIndex > 0;
    await this.ReloadSettingsForCurrentCopyAsync();
    this.SetStatus(this._copyIndex == 0
      ? "Switched to original."
      : $"Switched to copy {this._copyIndex}.");
  }

  /// <summary>
  /// Populate the copies combo with the original + all virtual copies on
  /// disk, selecting the entry that matches <see cref="_copyIndex"/>.
  /// </summary>
  private void RefreshCopiesCombo() {
    if (this.FindControl<ComboBox>("CopiesCombo") is not { } combo)
      return;
    if (this._sourceFile is null)
      return;
    this._suppressSliderEvents = true;
    try {
      combo.Items.Clear();
      combo.Items.Add(new ComboBoxItem { Content = "Original", Tag = 0 });
      var copies = VirtualCopyDiscovery.EnumerateIndices(this._sourceFile);
      foreach (var idx in copies)
        combo.Items.Add(new ComboBoxItem { Content = $"Copy {idx}", Tag = idx });
      // Select the item matching the current copy index.
      for (var i = 0; i < combo.Items.Count; i++) {
        if (combo.Items[i] is ComboBoxItem ci && ci.Tag is int tag && tag == this._copyIndex) {
          combo.SelectedIndex = i;
          break;
        }
      }
    } finally {
      this._suppressSliderEvents = false;
    }
    if (this.FindControl<Button>("DeleteCopyButton") is { } delBtn)
      delBtn.IsVisible = this._copyIndex > 0;
  }

  /// <summary>
  /// Reload develop settings from the current <see cref="_copyIndex"/>
  /// sidecar (or embedded XMP for 0) and apply them to the UI.
  /// </summary>
  private async Task ReloadSettingsForCurrentCopyAsync() {
    if (this._sourceFile is not { Exists: true } file)
      return;
    var loaded = await DevelopMetadataStore.LoadAsync(file, this._copyIndex);
    this.ApplySettingsToUi(loaded ?? new DevelopSettings());
    this.UpdatePreview();
  }

  private void UpdateCopyTitle() {
    if (this._sourceFile is null)
      return;
    this.Title = this._copyIndex == 0
      ? $"Develop image — {this._sourceFile.Name}"
      : $"Develop image — {this._sourceFile.Name} (copy {this._copyIndex})";
  }

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = message;
  }

  /// <summary>
  /// Centralised "what is the develop window currently doing?" status
  /// update. Drives the phase dot + label + diag strip across the top of
  /// the preview pane so the user always sees the pipeline state.
  /// Colour palette: grey = idle, blue = loading, green = preview ready,
  /// orange = AI work running, red = error.
  /// </summary>
  private enum PreviewPhase { Idle, Loading, Developing, Ready, AiWork, Error }
  private void SetPreviewPhase(PreviewPhase phase, string label, string? diag = null) {
    var (dotColor, labelColor) = phase switch {
      PreviewPhase.Loading    => ("#3498DB", "#FFFFFF"),
      PreviewPhase.Developing => ("#F39C12", "#FFFFFF"),
      PreviewPhase.Ready      => ("#27AE60", "#FFFFFF"),
      PreviewPhase.AiWork     => ("#E67E22", "#FFFFFF"),
      PreviewPhase.Error      => ("#E74C3C", "#FFCCCC"),
      _                       => ("#888888", "#DDDDDD"),
    };
    var dotBrush = Avalonia.Media.SolidColorBrush.Parse(dotColor);
    var labelBrush = Avalonia.Media.SolidColorBrush.Parse(labelColor);
    if (this.FindControl<Avalonia.Controls.Shapes.Ellipse>("PreviewPhaseDot") is { } dot)
      dot.Fill = dotBrush;
    if (this.FindControl<Avalonia.Controls.Shapes.Ellipse>("BottomPhaseDot") is { } dotB)
      dotB.Fill = dotBrush;
    if (this.FindControl<TextBlock>("PreviewPhaseLabel") is { } lbl) {
      lbl.Text = label;
      lbl.Foreground = labelBrush;
    }
    if (this.FindControl<TextBlock>("BottomPhaseLabel") is { } lblB) {
      lblB.Text = label;
      lblB.Foreground = labelBrush;
    }
    if (diag != null) {
      if (this.FindControl<TextBlock>("PreviewDiagText") is { } strip)
        strip.Text = diag;
      if (this.FindControl<TextBlock>("BottomDiagText") is { } stripB)
        stripB.Text = diag;
    }
  }

  /// <summary>
  /// Show an ETA / progress for long AI passes. Pass null totalSeconds to
  /// hide the indicator (the default idle state).
  /// </summary>
  private void SetPreviewEta(double? fraction, string? text) {
    var visible = fraction.HasValue || !string.IsNullOrEmpty(text);
    if (this.FindControl<StackPanel>("PreviewEtaPanel") is { } panel)
      panel.IsVisible = visible;
    if (fraction.HasValue) {
      if (this.FindControl<ProgressBar>("PreviewEtaBar") is { } bar)
        bar.Value = Math.Clamp(fraction.Value, 0, 1);
      if (this.FindControl<ProgressBar>("BottomEtaBar") is { } barB) {
        barB.IsVisible = true;
        barB.Value = Math.Clamp(fraction.Value, 0, 1);
      }
    } else {
      if (this.FindControl<ProgressBar>("BottomEtaBar") is { } barB)
        barB.IsVisible = false;
    }
    if (this.FindControl<TextBlock>("PreviewEtaText") is { } et)
      et.Text = text ?? string.Empty;
    if (this.FindControl<TextBlock>("BottomEtaText") is { } etB)
      etB.Text = text ?? string.Empty;
  }
}
