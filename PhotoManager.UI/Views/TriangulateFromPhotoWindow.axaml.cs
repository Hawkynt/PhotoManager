using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.UI.Avalonia;
using NetTopologySuite.Geometries;
using Hawkynt.PhotoManager.Core.Geocoding;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Triangulation workspace with the photo on the left and a live map on the
/// right. Pick the active landmark with the radio buttons, single-click its
/// pixel in the photo, double-click the same landmark on the map to set
/// its GPS. Two landmarks + known camera GPS solves heading + target;
/// three landmarks also solves the camera position (full resection).
/// Right-click clears individual markers.
/// </summary>
public partial class TriangulateFromPhotoWindow : Window {
  private readonly Bitmap? _bitmap;
  private readonly GpsCoordinate _cameraGpsFromFile;

  private LandmarkState _landmark1 = new();
  private LandmarkState _landmark2 = new();

  // The user can ALSO just drop a target on the map directly (no landmarks
  // needed) — heading becomes the camera→target bearing. Set to non-null
  // only when no landmarks are in play; clearing landmarks re-enables target
  // mode and vice versa.
  private GpsCoordinate? _directTarget;

  // Map layers for landmark pins + result visualisation (camera pin + cone).
  private readonly MemoryLayer _landmarkLayer;
  private readonly MemoryLayer _resultLayer;
  // Separate layer for the "known camera" pin so it renders even before any
  // result has been computed — it's the implicit third point in the 2-
  // landmark workflow.
  private readonly MemoryLayer _knownCameraLayer;

  private double? _computedHeading;
  private GpsCoordinate? _computedTarget;
  private GpsCoordinate? _resectedCamera;

  public TriangulateFromPhotoWindow() : this(null, new GpsCoordinate(0, 0)) { }

  public TriangulateFromPhotoWindow(Bitmap? bitmap, GpsCoordinate cameraGps) {
    this._bitmap = bitmap;
    this._cameraGpsFromFile = cameraGps;

    this._landmarkLayer = new MemoryLayer {
      Name = "Landmarks",
      Style = new VectorStyle {
        Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 2)
      }
    };
    this._resultLayer = new MemoryLayer {
      Name = "Result",
      Style = new VectorStyle {
        Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(60, 46, 134, 222)),
        Line = new Mapsui.Styles.Pen(Mapsui.Styles.Color.FromArgb(220, 46, 134, 222), 2)
      }
    };
    this._knownCameraLayer = new MemoryLayer {
      Name = "Known camera",
      Style = new SymbolStyle {
        SymbolScale = 1.0,
        Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(240, 155, 89, 182)), // purple
        Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 2)
      }
    };

    this.InitializeComponent();

    if (this.FindControl<Image>("PhotoImage") is { } img && bitmap != null)
      img.Source = bitmap;

    this.UpdateCameraGpsText();

    if (this.FindControl<Image>("PhotoImage") is { } photoImage) {
      photoImage.GetObservable(Avalonia.Layout.Layoutable.BoundsProperty)
        .Subscribe(new Avalonia.Reactive.AnonymousObserver<Avalonia.Rect>(_ => this.RefreshOverlay()));
    }
    if (this.FindControl<Canvas>("PhotoOverlay") is { } overlay) {
      overlay.GetObservable(Avalonia.Layout.Layoutable.BoundsProperty)
        .Subscribe(new Avalonia.Reactive.AnonymousObserver<Avalonia.Rect>(_ => this.RefreshOverlay()));
    }

    this.InitMap();
    this.UpdateLandmarkTexts();
    this.UpdateComputeEnabled();
  }

  public Result? PickedResult { get; private set; }

  /// <summary>
  /// Outcome of the triangulation. <see cref="ResectedCamera"/> is non-null
  /// when the user solved for an unknown camera position via 3-point
  /// resection — the caller should then also write it to the photo's GPS.
  /// </summary>
  public sealed record Result(double HeadingDegrees, GpsCoordinate Target, GpsCoordinate? ResectedCamera = null);

  private sealed record LandmarkState(double? PixelX = null, double? PixelY = null, GpsCoordinate? Gps = null) {
    public bool Complete => this.PixelX is not null && this.Gps is not null;
  }

  // ---- Setup ----

  private void InitMap() {
    if (this.FindControl<MapControl>("MapControl") is not { } mc)
      return;

    mc.Map.Layers.Add(OpenStreetMap.CreateTileLayer("PhotoManager"));
    mc.Map.Layers.Add(this._resultLayer);
    mc.Map.Layers.Add(this._knownCameraLayer);
    mc.Map.Layers.Add(this._landmarkLayer);
    mc.PointerPressed += this.OnMapPointerPressed;

    // If the photo already carries a camera GPS, pin it on the map so the
    // user can see the "implicit third point" of the 2-landmark workflow.
    if (this._cameraGpsFromFile.IsValid
        && (this._cameraGpsFromFile.Latitude != 0 || this._cameraGpsFromFile.Longitude != 0)) {
      var (camMx, camMy) = SphericalMercator.FromLonLat(
        this._cameraGpsFromFile.Longitude, this._cameraGpsFromFile.Latitude);
      this._knownCameraLayer.Features = new IFeature[] {
        new GeometryFeature {
          Geometry = new NetTopologySuite.Geometries.Point(camMx, camMy)
        }
      };
    }

    // Center on the camera GPS if known, otherwise on Europe at wide zoom.
    var (centerLon, centerLat, zoom) = this._cameraGpsFromFile is { IsValid: true, Latitude: not 0, Longitude: not 0 }
      ? (this._cameraGpsFromFile.Longitude, this._cameraGpsFromFile.Latitude, 20.0)
      : (10.0, 50.0, 10000.0);
    var (mx, my) = SphericalMercator.FromLonLat(centerLon, centerLat);
    var center = new MPoint(mx, my);
    mc.Map.Home = n => n.CenterOnAndZoomTo(center, zoom);
    mc.Map.Navigator.CenterOnAndZoomTo(center, zoom);
  }

  // ---- Photo clicks ----

  private void OnPhotoPointerPressed(object? sender, PointerPressedEventArgs e) {
    if (this._bitmap == null || sender is not Image photo)
      return;

    var props = e.GetCurrentPoint(photo).Properties;

    if (props.IsRightButtonPressed) {
      var pointer = e.GetPosition(photo);
      if (TryGetImageRenderBounds(photo, this._bitmap, out var ox, out var oy, out var rw, out var rh)
          && this.TryFindNearestLandmarkPixel(pointer, ox, oy, rw, rh, out var hitIndex)) {
        this.MutateLandmark(hitIndex, l => l with { PixelX = null, PixelY = null });
        this.OnStateChanged();
        e.Handled = true;
      }
      return;
    }

    if (!props.IsLeftButtonPressed)
      return;

    var position = e.GetPosition(photo);
    if (!TryGetImageRenderBounds(photo, this._bitmap, out var offsetX, out var offsetY, out var renderedW, out var renderedH))
      return;

    var relativeX = (position.X - offsetX) / renderedW;
    var relativeY = (position.Y - offsetY) / renderedH;
    if (relativeX is < 0 or > 1 || relativeY is < 0 or > 1)
      return;

    var pixelX = relativeX * this._bitmap.PixelSize.Width;
    var pixelY = relativeY * this._bitmap.PixelSize.Height;

    // Starting a landmark flow — any existing "direct target" is cancelled
    // because the user is now building a triangulation.
    if (this._directTarget is not null) {
      this._directTarget = null;
    }

    // Auto-route to the next landmark WITHOUT a pixel. This is the core of
    // the "no radio buttons" UX: whatever needs doing next happens next.
    var targetLandmark = this.NextLandmarkNeedingPixel();
    if (targetLandmark == 0)
      return;  // all slots already have pixels; user must clear first
    this.MutateLandmark(targetLandmark, l => l with { PixelX = pixelX, PixelY = pixelY });
    this.OnStateChanged();
  }

  /// <summary>
  /// Finds the next landmark slot (1, 2 or 3) that has no pixel yet.
  /// Returns 0 when every slot already carries a pixel.
  /// </summary>
  private int NextLandmarkNeedingPixel() {
    if (this._landmark1.PixelX is null) return 1;
    if (this._landmark2.PixelX is null) return 2;
    return 0;
  }

  /// <summary>
  /// Finds the next landmark that has a pixel but no GPS yet — that's the
  /// one a map double-click should fill. Returns 0 when no landmark is
  /// waiting for GPS.
  /// </summary>
  private int NextLandmarkNeedingGps() {
    if (this._landmark1.PixelX is not null && this._landmark1.Gps is null) return 1;
    if (this._landmark2.PixelX is not null && this._landmark2.Gps is null) return 2;
    return 0;
  }

  private bool TryFindNearestLandmarkPixel(Avalonia.Point pointer, double offsetX, double offsetY, double renderedW, double renderedH, out int landmarkIndex) {
    landmarkIndex = 0;
    if (this._bitmap == null)
      return false;

    var best = double.MaxValue;
    const double hitRadiusPx = 18.0;
    foreach (var (idx, lm) in new[] { (1, this._landmark1), (2, this._landmark2) }) {
      if (lm.PixelX is not { } px || lm.PixelY is not { } py)
        continue;
      var dx = (offsetX + px / this._bitmap.PixelSize.Width  * renderedW) - pointer.X;
      var dy = (offsetY + py / this._bitmap.PixelSize.Height * renderedH) - pointer.Y;
      var d = Math.Sqrt(dx * dx + dy * dy);
      if (d <= hitRadiusPx && d < best) {
        best = d;
        landmarkIndex = idx;
      }
    }
    return landmarkIndex > 0;
  }

  // ---- Map clicks ----

  private void OnMapPointerPressed(object? sender, PointerPressedEventArgs e) {
    if (sender is not MapControl mc)
      return;

    var props = e.GetCurrentPoint(mc).Properties;

    if (props.IsRightButtonPressed) {
      var screen = e.GetPosition(mc);
      // Target first (it's a single pin), then landmarks by proximity.
      if (this._directTarget is not null
          && this.IsPinWithinHitRadius(mc, screen, this._directTarget.Value)) {
        this._directTarget = null;
        this.OnStateChanged();
        e.Handled = true;
        return;
      }
      if (this.TryFindNearestLandmarkOnMap(mc, screen, out var hitIndex)) {
        this.MutateLandmark(hitIndex, l => l with { Gps = null });
        this.OnStateChanged();
        e.Handled = true;
      }
      return;
    }

    if (!(props.IsLeftButtonPressed && e.ClickCount >= 2))
      return;

    var screenPos = e.GetPosition(mc);
    if (!TryScreenToWorld(mc, screenPos.X, screenPos.Y, out var worldX, out var worldY))
      return;

    var (lon, lat) = SphericalMercator.ToLonLat(worldX, worldY);
    var coord = new GpsCoordinate(lat, lon);
    if (!coord.IsValid)
      return;

    // Routing rules:
    //   (a) If any landmark is half-filled (pixel set, GPS not), finish it.
    //       This is the "just clicked the photo, now I double-click the
    //       map" triangulation flow.
    //   (b) Otherwise, if there are no landmarks at all, drop the target —
    //       heading becomes the camera→target bearing, FOV stays manual.
    //   (c) Landmarks all complete, no target mode — start a new landmark's
    //       GPS slot (user has to place the pixel first). Ignore the click
    //       so we don't leave orphan GPS pins floating around.
    var pendingLandmark = this.NextLandmarkNeedingGps();
    if (pendingLandmark != 0) {
      this.MutateLandmark(pendingLandmark, l => l with { Gps = coord });
      this.OnStateChanged();
      e.Handled = true;
      return;
    }

    if (this.AnyLandmarkStarted()) {
      // Landmarks exist and are complete — clicking more map pins doesn't
      // help. User should either apply or clear before switching to target.
      e.Handled = true;
      return;
    }

    this._directTarget = coord;
    this.OnStateChanged();
    e.Handled = true;
  }

  private bool AnyLandmarkStarted() =>
    this._landmark1.PixelX is not null || this._landmark1.Gps is not null
    || this._landmark2.PixelX is not null || this._landmark2.Gps is not null;

  private bool IsPinWithinHitRadius(MapControl mc, Avalonia.Point screen, GpsCoordinate coord) {
    var viewport = mc.Map.Navigator.Viewport;
    var (mx, my) = SphericalMercator.FromLonLat(coord.Longitude, coord.Latitude);
    var sx = viewport.Width  / 2.0 + (mx - viewport.CenterX) / viewport.Resolution;
    var sy = viewport.Height / 2.0 - (my - viewport.CenterY) / viewport.Resolution;
    var dx = sx - screen.X;
    var dy = sy - screen.Y;
    return Math.Sqrt(dx * dx + dy * dy) <= 18.0;
  }

  private bool TryFindNearestLandmarkOnMap(MapControl mc, Avalonia.Point screen, out int landmarkIndex) {
    landmarkIndex = 0;
    var viewport = mc.Map.Navigator.Viewport;
    var best = double.MaxValue;
    const double hitRadiusPx = 18.0;

    foreach (var (idx, lm) in new[] { (1, this._landmark1), (2, this._landmark2) }) {
      if (lm.Gps is not { } g) continue;
      var (mx, my) = SphericalMercator.FromLonLat(g.Longitude, g.Latitude);
      // world → screen
      var sx = viewport.Width  / 2.0 + (mx - viewport.CenterX) / viewport.Resolution;
      var sy = viewport.Height / 2.0 - (my - viewport.CenterY) / viewport.Resolution;
      var dx = sx - screen.X;
      var dy = sy - screen.Y;
      var d = Math.Sqrt(dx * dx + dy * dy);
      if (d <= hitRadiusPx && d < best) {
        best = d;
        landmarkIndex = idx;
      }
    }
    return landmarkIndex > 0;
  }

  private static bool TryScreenToWorld(MapControl mc, double screenX, double screenY, out double worldX, out double worldY) {
    worldX = worldY = 0;
    var viewport = mc.Map.Navigator.Viewport;
    if (viewport.Width <= 0 || viewport.Height <= 0 || viewport.Resolution <= 0)
      return false;

    worldX = viewport.CenterX + (screenX - viewport.Width / 2.0) * viewport.Resolution;
    worldY = viewport.CenterY - (screenY - viewport.Height / 2.0) * viewport.Resolution;
    return true;
  }

  // ---- Clear buttons + state sync ----

  private void OnClearLandmark1Click(object? sender, RoutedEventArgs e) => this.ClearLandmark(1);
  private void OnClearLandmark2Click(object? sender, RoutedEventArgs e) => this.ClearLandmark(2);

  private void OnClearTargetClick(object? sender, RoutedEventArgs e) {
    this._directTarget = null;
    this.OnStateChanged();
  }

  private void ClearLandmark(int idx) {
    this.MutateLandmark(idx, _ => new LandmarkState());
    this.OnStateChanged();
  }

  private void MutateLandmark(int idx, Func<LandmarkState, LandmarkState> f) {
    switch (idx) {
      case 1: this._landmark1 = f(this._landmark1); break;
      case 2: this._landmark2 = f(this._landmark2); break;
    }
  }

  private LandmarkState GetLandmark(int idx) => idx switch {
    1 => this._landmark1,
    2 => this._landmark2,
    _ => throw new ArgumentOutOfRangeException(nameof(idx))
  };

  /// <summary>
  /// Called after any click / clear. Rebuilds visuals, refreshes field
  /// texts, re-derives the FOV from landmarks when possible, re-renders
  /// the result cone on the map (if the current state is solvable), and
  /// updates the flow hint at the top of the controls column.
  /// </summary>
  private void OnStateChanged() {
    this.RefreshOverlay();
    this.RefreshMapPins();
    this.UpdateLandmarkTexts();
    this.TryDeriveAndFillFov();
    this.AutoCompute();
    this.UpdateComputeEnabled();
    this.UpdateFlowHint();
  }

  private void UpdateFlowHint() {
    if (this.FindControl<TextBlock>("FlowHintText") is not { } hint)
      return;

    if (this._landmark1.Complete && this._landmark2.Complete) {
      hint.Text = "Both landmarks set — heading + target derived below. Click Apply to write to the photo.";
    } else if (this.NextLandmarkNeedingGps() is var g and > 0) {
      hint.Text = $"Double-click landmark {g}'s location on the map.";
    } else if (this.NextLandmarkNeedingPixel() is var p and > 0 && this.AnyLandmarkStarted()) {
      hint.Text = $"Click landmark {p} in the photo (or right-click a marker to clear, or clear a landmark to drop a direct target instead).";
    } else if (this._directTarget is not null) {
      hint.Text = "Target set — heading = camera→target. Click Apply to write to the photo.";
    } else {
      hint.Text = "Double-click the map to drop a target, OR click two landmarks in the photo and double-click each on the map for full triangulation.";
    }
  }

  // ---- Compute / Resect / Apply ----

  /// <summary>
  /// Derives the horizontal FOV from two landmarks whose pixel X positions
  /// are known AND whose real bearings from the camera can be computed.
  /// Math: the angular span between the two landmarks (seen from the
  /// camera) maps linearly onto their pixel separation, so
  /// <c>FOV = angular_span × imageWidth / pixel_span</c>. Only when the
  /// photo has a valid camera GPS can we compute the real-world angular
  /// span — otherwise the FOV stays whatever the user entered manually.
  /// </summary>
  private void TryDeriveAndFillFov() {
    if (this.FindControl<TextBox>("FovBox") is not { } fovBox) return;
    if (this._bitmap == null) return;
    if (!(this._cameraGpsFromFile.IsValid
          && (this._cameraGpsFromFile.Latitude != 0 || this._cameraGpsFromFile.Longitude != 0)))
      return;
    if (!this._landmark1.Complete || !this._landmark2.Complete)
      return;

    var pxSpread = Math.Abs(this._landmark2.PixelX!.Value - this._landmark1.PixelX!.Value);
    if (pxSpread < 1) return;

    var bearing1 = GreatCircle.BearingDegrees(this._cameraGpsFromFile, this._landmark1.Gps!.Value);
    var bearing2 = GreatCircle.BearingDegrees(this._cameraGpsFromFile, this._landmark2.Gps!.Value);
    var span = Math.Abs(bearing2 - bearing1);
    if (span > 180) span = 360 - span;  // handle 0°/360° wrap
    if (span <= 0) return;

    var fov = span * this._bitmap.PixelSize.Width / pxSpread;
    if (fov is <= 0 or >= 180) return;

    var inv = CultureInfo.InvariantCulture;
    var derivedText = fov.ToString("0.##", inv);
    if (fovBox.Text != derivedText)
      fovBox.Text = derivedText;
  }

  /// <summary>
  /// Re-runs the compute step whenever enough state is present. Covers three
  /// cases: (a) two landmarks + camera GPS → <see cref="OnComputeClick"/>;
  /// (b) three landmarks regardless of camera → <see cref="OnResectClick"/>;
  /// (c) target + camera → heading = camera→target bearing. Silent on
  /// incomplete state so the button never becomes a blocker.
  /// </summary>
  private void AutoCompute() {
    var cameraKnown = this._cameraGpsFromFile.IsValid
      && (this._cameraGpsFromFile.Latitude != 0 || this._cameraGpsFromFile.Longitude != 0);

    if (this._landmark1.Complete && this._landmark2.Complete && cameraKnown) {
      this.RunTriangulation();
      return;
    }
    if (this._directTarget is { } target && cameraKnown) {
      var heading = GreatCircle.BearingDegrees(this._cameraGpsFromFile, target);
      this._computedHeading = heading;
      this._computedTarget = target;
      this._resectedCamera = null;
      this.RenderResultOnMap(this._cameraGpsFromFile, target, heading);
      this.PublishResultText(this._cameraGpsFromFile, target, heading, null);
      return;
    }

    // Nothing solvable yet — wipe any prior result.
    this._computedHeading = null;
    this._computedTarget = null;
    this._resectedCamera = null;
    this._resultLayer.Features = Array.Empty<IFeature>();
    if (this.FindControl<MapControl>("MapControl") is { } mc) mc.RefreshGraphics();
    if (this.FindControl<TextBlock>("ResultText") is { } rt)
      rt.Text = "Place landmarks or drop a target to see the result.";
    if (this.FindControl<Button>("ApplyButton") is { } apply)
      apply.IsEnabled = false;
  }

  private void RunTriangulation() {
    if (!this.TryGetFovDegrees(out var fov) || this._bitmap == null) return;
    var imageWidth = (double)this._bitmap.PixelSize.Width;
    var lm1 = new Triangulation.Landmark(this._landmark1.Gps!.Value, this._landmark1.PixelX!.Value);
    var lm2 = new Triangulation.Landmark(this._landmark2.Gps!.Value, this._landmark2.PixelX!.Value);
    var heading = Triangulation.CameraHeadingFromTwoLandmarks(this._cameraGpsFromFile, lm1, lm2, imageWidth, fov);
    var d1 = GreatCircle.DistanceMeters(this._cameraGpsFromFile, lm1.Gps);
    var d2 = GreatCircle.DistanceMeters(this._cameraGpsFromFile, lm2.Gps);
    var targetDistance = (d1 + d2) / 2.0;
    var target = Triangulation.TargetFromCameraAndHeading(this._cameraGpsFromFile, heading, targetDistance);
    this._computedHeading = heading;
    this._computedTarget = target;
    this._resectedCamera = null;
    this.RenderResultOnMap(this._cameraGpsFromFile, target, heading);
    this.PublishResultText(this._cameraGpsFromFile, target, heading, null);
  }

  private void PublishResultText(GpsCoordinate camera, GpsCoordinate target, double heading, double? rmsError) {
    var inv = CultureInfo.InvariantCulture;
    var text = $"Camera:  {camera.Latitude:0.######}, {camera.Longitude:0.######}\n"
             + $"Heading: {heading:0.00}°\n"
             + $"Target:  {target.Latitude:0.######}, {target.Longitude:0.######}";
    if (rmsError is { } rms)
      text += $"\nRMS err: {rms:0.00}°";

    if (this.FindControl<TextBlock>("ResultText") is { } rt)
      rt.Text = text;
    if (this.FindControl<Button>("ApplyButton") is { } apply)
      apply.IsEnabled = true;

    this.SetStatus(this._directTarget is not null
      ? "Target set. Click Apply to write heading + target to the photo."
      : "Triangulated from 2 landmarks. Click Apply to write heading + target.");
  }

  private void OnApplyClick(object? sender, RoutedEventArgs e) {
    if (this._computedHeading is not { } heading || this._computedTarget is not { } target)
      return;
    this.PickedResult = new Result(heading, target, this._resectedCamera);
    this.Close(this.PickedResult);
  }

  private void OnConvertFocalToFovClick(object? sender, RoutedEventArgs e) {
    if (this.FindControl<TextBox>("FocalBox")?.Text is not { } focalText
        || !double.TryParse(focalText, NumberStyles.Float, CultureInfo.InvariantCulture, out var focal)
        || focal <= 0)
      return;
    var fov = Triangulation.HorizontalFovFromFocalLength35(focal);
    if (this.FindControl<TextBox>("FovBox") is { } fovBox)
      fovBox.Text = fov.ToString("0.##", CultureInfo.InvariantCulture);
  }

  private void OnClearClick(object? sender, RoutedEventArgs e) {
    this._landmark1 = new();
    this._landmark2 = new();
    this._directTarget = null;
    this._computedHeading = null;
    this._computedTarget = null;
    this._resectedCamera = null;
    this._resultLayer.Features = Array.Empty<IFeature>();
    this.RefreshOverlay();
    this.RefreshMapPins();
    this.UpdateLandmarkTexts();
    this.UpdateComputeEnabled();
    if (this.FindControl<TextBlock>("ResultText") is { } rt)
      rt.Text = "Place landmarks, then click Compute (2 pts) or Resect (3 pts).";
    if (this.FindControl<Button>("ApplyButton") is { } applyBtn)
      applyBtn.IsEnabled = false;
    this.SetStatus(string.Empty);
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close(null);

  private bool TryGetFovDegrees(out double fov) {
    fov = 0;
    if (this.FindControl<TextBox>("FovBox")?.Text is not { } text
        || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out fov))
      return false;
    return fov is > 0 and < 180;
  }

  private void UpdateCameraGpsText() {
    if (this.FindControl<TextBlock>("CameraGpsText") is not { } tb)
      return;
    var inv = CultureInfo.InvariantCulture;
    if (this._cameraGpsFromFile.IsValid
        && (this._cameraGpsFromFile.Latitude != 0 || this._cameraGpsFromFile.Longitude != 0)) {
      tb.Text = string.Create(inv, $"GPS from photo: {this._cameraGpsFromFile.Latitude:0.######}, {this._cameraGpsFromFile.Longitude:0.######}");
    } else {
      tb.Text = "Photo has no GPS yet — 2-point triangulation is disabled. Add a 3rd landmark to solve for the camera position.";
    }
  }

  private void UpdateLandmarkTexts() {
    SetText("L1PixelText", Format(this._landmark1, pixels: true));
    SetText("L1GpsText",   Format(this._landmark1, pixels: false));
    SetText("L2PixelText", Format(this._landmark2, pixels: true));
    SetText("L2GpsText",   Format(this._landmark2, pixels: false));

    if (this.FindControl<TextBlock>("TargetGpsText") is { } tgt) {
      var inv = CultureInfo.InvariantCulture;
      tgt.Text = this._directTarget is { } t
        ? string.Create(inv, $"map: {t.Latitude:0.######}, {t.Longitude:0.######}")
        : "map: —";
    }

    void SetText(string name, string text) {
      if (this.FindControl<TextBlock>(name) is { } tb)
        tb.Text = text;
    }
    string Format(LandmarkState l, bool pixels) {
      var inv = CultureInfo.InvariantCulture;
      if (pixels) {
        return l.PixelX is { } x
          ? string.Create(inv, $"photo: x={x:0}, y={l.PixelY:0}")
          : "photo: —";
      }
      return l.Gps is { } g
        ? string.Create(inv, $"map: {g.Latitude:0.######}, {g.Longitude:0.######}")
        : "map: —";
    }
  }

  private void UpdateComputeEnabled() {
    // No explicit Compute button in the UI any more — AutoCompute handles
    // it whenever enough state is present. Apply button is driven by the
    // actual computed result (see PublishResultText / AutoCompute).
  }

  private void SetStatus(string text) {
    if (this.FindControl<TextBlock>("StatusText") is { } tb)
      tb.Text = text;
  }

  // ---- Overlay (photo side) ----

  private void RefreshOverlay() {
    if (this.FindControl<Canvas>("PhotoOverlay") is not { } canvas
        || this.FindControl<Image>("PhotoImage") is not { } photo
        || this._bitmap == null)
      return;

    canvas.Children.Clear();
    if (!TryGetCanvasRenderBounds(photo, canvas, this._bitmap, out var offsetX, out var offsetY, out var renderedW, out var renderedH))
      return;

    DrawMarker(canvas, this._landmark1, 1, Colors.DeepSkyBlue, offsetX, offsetY, renderedW, renderedH, this._bitmap);
    DrawMarker(canvas, this._landmark2, 2, Colors.OrangeRed, offsetX, offsetY, renderedW, renderedH, this._bitmap);
  }

  private static void DrawMarker(
    Canvas canvas, LandmarkState landmark, int number, Avalonia.Media.Color color,
    double offsetX, double offsetY, double renderedW, double renderedH, Bitmap bitmap
  ) {
    if (landmark.PixelX is not { } px || landmark.PixelY is not { } py)
      return;

    var displayX = offsetX + px / bitmap.PixelSize.Width * renderedW;
    var displayY = offsetY + py / bitmap.PixelSize.Height * renderedH;

    var brush = new SolidColorBrush(color);
    var dot = new Ellipse {
      Width = 18, Height = 18,
      Fill = brush,
      Stroke = Brushes.White,
      StrokeThickness = 2,
      IsHitTestVisible = false
    };
    Canvas.SetLeft(dot, displayX - 9);
    Canvas.SetTop(dot, displayY - 9);
    canvas.Children.Add(dot);

    var label = new TextBlock {
      Text = number.ToString(),
      Foreground = Brushes.White,
      FontSize = 12,
      FontWeight = FontWeight.Bold,
      IsHitTestVisible = false
    };
    Canvas.SetLeft(label, displayX - 4);
    Canvas.SetTop(label, displayY - 9);
    canvas.Children.Add(label);
  }

  // ---- Map pins + result cone ----

  private void RefreshMapPins() {
    var features = new List<IFeature>();
    AddPin(this._landmark1, Mapsui.Styles.Color.FromArgb(230, 46, 134, 222));   // blue
    AddPin(this._landmark2, Mapsui.Styles.Color.FromArgb(230, 231, 76, 60));    // red

    // Direct target pin (when the user dropped a target without landmarks).
    if (this._directTarget is { } tgt) {
      var (tx, ty) = SphericalMercator.FromLonLat(tgt.Longitude, tgt.Latitude);
      features.Add(new GeometryFeature {
        Geometry = new NetTopologySuite.Geometries.Point(tx, ty),
        Styles = new[] {
          new SymbolStyle {
            SymbolScale = 0.9,
            Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(240, 231, 76, 60)),
            Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 2)
          }
        }
      });
    }

    this._landmarkLayer.Features = features;
    if (this.FindControl<MapControl>("MapControl") is { } mc)
      mc.RefreshGraphics();

    void AddPin(LandmarkState l, Mapsui.Styles.Color color) {
      if (l.Gps is not { } g) return;
      var (mx, my) = SphericalMercator.FromLonLat(g.Longitude, g.Latitude);
      features.Add(new GeometryFeature {
        Geometry = new NetTopologySuite.Geometries.Point(mx, my),
        Styles = new[] {
          new SymbolStyle {
            SymbolScale = 0.7,
            Fill = new Mapsui.Styles.Brush(color),
            Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 2)
          }
        }
      });
    }
  }

  private void RenderResultOnMap(GpsCoordinate camera, GpsCoordinate target, double headingDeg) {
    // Cone half-angle = half the actual horizontal FOV from the FovBox
    // (which is either user-entered or auto-derived from the two
    // landmarks). This way the wedge on the map matches the real image
    // field of view — a 90° lens produces a 90° cone, a 35° telephoto
    // produces a thin 35° sliver.
    var fovDeg = this.TryGetFovDegrees(out var fov) ? fov : 60.0;
    var coneHalfAngleDeg = fovDeg / 2.0;

    var range = GreatCircle.DistanceMeters(camera, target);
    var leftEdge = GreatCircle.Destination(camera, headingDeg - coneHalfAngleDeg, range);
    var rightEdge = GreatCircle.Destination(camera, headingDeg + coneHalfAngleDeg, range);

    var camPt = ToMercatorCoord(camera);
    var tgtPt = ToMercatorCoord(target);
    var leftPt = ToMercatorCoord(leftEdge);
    var rightPt = ToMercatorCoord(rightEdge);

    var cone = new NetTopologySuite.Geometries.Polygon(new LinearRing(new[] { camPt, leftPt, tgtPt, rightPt, camPt }));
    var centreLine = new LineString(new[] { camPt, tgtPt });

    var camStyle = new SymbolStyle {
      SymbolScale = 0.9,
      Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(240, 46, 134, 222)),
      Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 2)
    };

    this._resultLayer.Features = new IFeature[] {
      new GeometryFeature { Geometry = cone },
      new GeometryFeature { Geometry = centreLine },
      new GeometryFeature {
        Geometry = new NetTopologySuite.Geometries.Point(camPt.X, camPt.Y),
        Styles = new[] { camStyle }
      }
    };

    if (this.FindControl<MapControl>("MapControl") is { } mc)
      mc.RefreshGraphics();
  }

  private static NetTopologySuite.Geometries.Coordinate ToMercatorCoord(GpsCoordinate c) {
    var (mx, my) = SphericalMercator.FromLonLat(c.Longitude, c.Latitude);
    return new NetTopologySuite.Geometries.Coordinate(mx, my);
  }

  // ---- Geometry helpers (photo) ----

  private static bool TryGetImageRenderBounds(
    Image image, Bitmap bitmap,
    out double offsetX, out double offsetY, out double renderedW, out double renderedH
  ) {
    offsetX = offsetY = renderedW = renderedH = 0;
    var bounds = image.Bounds;
    if (bounds.Width <= 0 || bounds.Height <= 0)
      return false;
    var sourceW = bitmap.PixelSize.Width;
    var sourceH = bitmap.PixelSize.Height;
    if (sourceW <= 0 || sourceH <= 0)
      return false;

    var scale = Math.Min(bounds.Width / sourceW, bounds.Height / sourceH);
    if (scale > 1) scale = 1;
    renderedW = sourceW * scale;
    renderedH = sourceH * scale;
    offsetX = (bounds.Width - renderedW) / 2;
    offsetY = (bounds.Height - renderedH) / 2;
    return true;
  }

  private static bool TryGetCanvasRenderBounds(
    Image image, Canvas canvas, Bitmap bitmap,
    out double offsetX, out double offsetY, out double renderedW, out double renderedH
  ) {
    if (!TryGetImageRenderBounds(image, bitmap, out var localX, out var localY, out renderedW, out renderedH)) {
      offsetX = offsetY = 0;
      return false;
    }
    var localOrigin = new Avalonia.Point(localX, localY);
    var inCanvas = image.TranslatePoint(localOrigin, canvas) ?? localOrigin;
    offsetX = inCanvas.X;
    offsetY = inCanvas.Y;
    return true;
  }
}
