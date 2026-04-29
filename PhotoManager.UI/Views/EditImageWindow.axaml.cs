using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PhotoManager.Core.Develop;
using PhotoManager.Core.Models;
using PhotoManager.Core.Segmentation;
using PhotoManager.UI.Controls;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.UI.Views;

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
  private string? _activeEyedropper;

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
    ("CropAngleSlider",  "CropAngleValue",   s => s.CropAngleDegrees,   (s, v) => s with { CropAngleDegrees = v },   "+0.0;-0.0;0.0"),
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

  public EditImageWindow() : this(null, Array.Empty<FileInfo>()) { }

  public EditImageWindow(FileInfo sourceFile)
    : this(sourceFile, Array.Empty<FileInfo>()) { }

  public EditImageWindow(FileInfo? sourceFile, IReadOnlyList<FileInfo> selectionForApplyAll) {
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

    this.RefreshTemplateList();

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
    if (adj.Mask.Type != LocalMaskType.Brush)
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
    if (adj.Mask.Type != LocalMaskType.Brush)
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
    var brushMaskActive = activeMask?.Type == LocalMaskType.Brush;

    if (this.FindControl<StackPanel>("BrushControlsRow") is { } brush) brush.IsVisible = hasSelection && brushMaskActive;
    if (this.FindControl<StackPanel>("RangeMaskRow") is { } rng) rng.IsVisible = hasSelection;
    if (this.FindControl<StackPanel>("SubMasksRow") is { } subRow) subRow.IsVisible = hasSelection;
    if (this.FindControl<Grid>("SubMaskOpRow") is { } subOpRow) subOpRow.IsVisible = hasSelection && this._activeSubMaskIndex >= 0;
    if (this.FindControl<StackPanel>("LocalSlidersPanel") is { } sliders) sliders.IsVisible = hasSelection;

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
        overlay.BrushMode = current[this._activeLocalIndex].Mask.Type == LocalMaskType.Brush;
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
    var next = AutoDeveloper.AutoChannelStretch(this._settings, hist);
    this.ApplySettingsToUi(next);
    this.UpdatePreview();
    this.SetStatus("Auto channel stretch: each R/G/B pushed to its 99.5% percentile.");
  }

  private async Task LoadPreviewAsync() {
    if (this._sourceFile is not { Exists: true } file) {
      this.SetStatus("No file selected.");
      return;
    }

    this.SetStatus("Loading...");
    try {
      // RawImageLoader routes RAW extensions through PNGCrushCS
      // (FileFormat.CameraRaw + FileFormat.Dng) and falls through to
      // ImageSharp for JPEG / PNG / TIFF / etc.
      var image = await RawImageLoader.LoadAsync(file);
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

    this.SetStatus($"{this._sourceFile.Name} ({this._previewSource.Width}×{this._previewSource.Height} preview)");
    if (this.FindControl<MaskOverlayCanvas>("MaskOverlay") is { } overlay) {
      overlay.ImageWidth  = this._previewSource.Width;
      overlay.ImageHeight = this._previewSource.Height;
    }

    // Pick up any develop edits embedded in the file's XMP so the user
    // resumes mid-edit instead of starting from defaults each time.
    DevelopSettings? embedded = null;
    try { embedded = await DevelopMetadataStore.LoadAsync(file); } catch { /* best-effort */ }
    if (embedded is not null) {
      this.ApplySettingsToUi(embedded);
      this.SetStatus($"Resumed embedded develop settings for {file.Name}.");
    }
    this.UpdatePreview();
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

  private void UpdatePreview() {
    this._settings = this.ReadSettingsFromUi();
    this.RefreshValueLabels();

    if (this._previewSource is null)
      return;

    try {
      // Keep a long-lived developed preview so eyedroppers can sample it
      // without re-running the entire pipeline. Replaced (and old one
      // disposed) on every preview update.
      var developed = ImageDeveloper.Apply(this._previewSource, this._settings);
      this._developedPreview?.Dispose();
      this._developedPreview = developed;

      using var ms = new MemoryStream();
      developed.SaveAsJpeg(ms);
      ms.Position = 0;
      var bitmap = new Bitmap(ms);
      if (this.FindControl<Avalonia.Controls.Image>("PreviewImage") is { } img)
        img.Source = bitmap;
      if (this.FindControl<Avalonia.Controls.Image>("PreviewImageSplit") is { } imgSplit)
        imgSplit.Source = bitmap;
      if (this.FindControl<Avalonia.Controls.Image>("PreviewImageSlider") is { } imgSlider)
        imgSlider.Source = bitmap;

      this.RenderHistogram(developed);

      // Persist the current edits inside the file's XMP packet so the
      // workflow stays non-destructive — the pixel data on disk is never
      // touched, but the user's slider state travels with the photo.
      // UpdatePreview is already debounced via _updateTimer, so disk I/O
      // is effectively coalesced for slider drags.
      if (this._sourceFile is { } src && DevelopMetadataStore.SupportsContainer(src))
        _ = DevelopMetadataStore.SaveAsync(src, this._settings);
    } catch (Exception ex) {
      this.SetStatus($"Preview failed: {ex.Message}");
    }
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

  private void OnAutoToneClick(object? sender, RoutedEventArgs e) {
    if (this._developedPreview is not { } src)
      return;
    var hist = HistogramAnalyzer.Compute(src);
    var next = AutoDeveloper.AutoTone(this._settings, hist);
    this.ApplySettingsToUi(next);
    this.UpdatePreview();
    this.SetStatus("Auto-tone: recovered shadows + highlights from histogram.");
  }

  private void OnAutoWhiteBalanceClick(object? sender, RoutedEventArgs e) {
    if (this._developedPreview is not { } src)
      return;
    var hist = HistogramAnalyzer.Compute(src);
    var next = AutoDeveloper.AutoWhiteBalance(this._settings, hist);
    this.ApplySettingsToUi(next);
    this.UpdatePreview();
    this.SetStatus("Auto white balance: grey-world correction applied.");
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
      // Crop edges
      if (this.FindControl<NumericUpDown>("CropLeftBox")   is { } leftBox)   leftBox.Value   = (decimal)target.CropLeft;
      if (this.FindControl<NumericUpDown>("CropTopBox")    is { } topBox)    topBox.Value    = (decimal)target.CropTop;
      if (this.FindControl<NumericUpDown>("CropRightBox")  is { } rightBox)  rightBox.Value  = (decimal)target.CropRight;
      if (this.FindControl<NumericUpDown>("CropBottomBox") is { } bottomBox) bottomBox.Value = (decimal)target.CropBottom;
    } finally {
      this._suppressSliderEvents = false;
    }
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
      this.SetStatus($"Saved {Path.GetFileName(destination)}.");
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

    this.SetStatus($"Rendering {this._selectionForApplyAll.Count} file(s)...");
    var failed = 0;
    var written = 0;
    foreach (var src in this._selectionForApplyAll) {
      if (!src.Exists)
        continue;
      var dest = new FileInfo(Path.Combine(
        src.Directory!.FullName,
        Path.GetFileNameWithoutExtension(src.Name) + "_developed.jpg"));
      try {
        await ImageDeveloper.RenderToJpegAsync(src, dest, this._settings);
        written++;
      } catch {
        failed++;
      }
    }

    this.SetStatus(failed == 0
      ? $"Rendered {written} file(s) with current settings."
      : $"Rendered {written}; {failed} failed.");
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

  private void OnCompareSliderChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    if (this._compareState.Mode == CompareMode.Slider)
      this.UpdateSliderClip();
  }

  private void ApplyCompareModeUi() {
    if (this.FindControl<Avalonia.Controls.Image>("PreviewImage") is { } single)
      single.IsVisible = this._compareState.IsAfterVisible;
    if (this.FindControl<Grid>("SplitPanel") is { } split)
      split.IsVisible = this._compareState.IsSplitVisible;
    if (this.FindControl<Grid>("SliderPanel") is { } slider)
      slider.IsVisible = this._compareState.IsSliderVisible;
    if (this.FindControl<Button>("CompareToggleButton") is { } btn) {
      btn.Content = this._compareState.ButtonContent;
      ToolTip.SetTip(btn, this._compareState.ButtonTooltip);
    }
    if (this._compareState.Mode == CompareMode.Slider) {
      this.UpdateSliderClip();
      Dispatcher.UIThread.Post(this.UpdateSliderClip, DispatcherPriority.Loaded);
    }
  }

  private void UpdateSliderClip() {
    if (this.FindControl<Avalonia.Controls.Image>("PreviewImageSlider") is not { } preview)
      return;
    if (this.FindControl<Slider>("CompareSlider") is not { } slider)
      return;
    var width  = preview.Bounds.Width;
    var height = preview.Bounds.Height;
    if (width <= 0 || height <= 0) {
      preview.Clip = null;
      return;
    }
    var t = Math.Clamp(slider.Value, 0.0, 1.0);
    preview.Clip = new Avalonia.Media.RectangleGeometry(
      new Avalonia.Rect(t * width, 0, width - t * width, height));
  }

  private async Task RebuildBaselineAsync() {
    if (this._previewSource is not { } src)
      return;

    var clone = src.Clone();
    Bitmap? bitmap = null;
    try {
      await Task.Run(() => {
        using var rendered = ImageDeveloper.Apply(clone, new DevelopSettings());
        using var ms = new MemoryStream();
        rendered.SaveAsJpeg(ms);
        ms.Position = 0;
        bitmap = new Bitmap(ms);
      });
    } finally {
      clone.Dispose();
    }

    if (bitmap is null)
      return;

    Dispatcher.UIThread.Post(() => {
      this._baselinePreview?.Dispose();
      this._baselinePreview = bitmap;
      if (this.FindControl<Avalonia.Controls.Image>("BaselineImageSplit") is { } a)
        a.Source = bitmap;
      if (this.FindControl<Avalonia.Controls.Image>("BaselineImageSlider") is { } b)
        b.Source = bitmap;
    });
  }

  private void InvalidateBaseline() {
    this._baselinePreview?.Dispose();
    this._baselinePreview = null;
    if (this._compareState.NeedsBaseline)
      _ = this.RebuildBaselineAsync();
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) {
    this._previewSource?.Dispose();
    this._developedPreview?.Dispose();
    this._baselinePreview?.Dispose();
    this._subjectSegmenter?.Dispose();
    this.Close();
  }

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = message;
  }
}
