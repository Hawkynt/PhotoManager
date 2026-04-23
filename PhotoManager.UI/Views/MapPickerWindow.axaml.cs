using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.UI;
using Mapsui.UI.Avalonia;
using NetTopologySuite.Geometries;
using PhotoManager.Core.Geocoding;
using PhotoManager.Core.Metadata;

namespace PhotoManager.UI.Views;

/// <summary>
/// Map picker with two pins — a camera position and a target (subject) —
/// plus a direction line + field-of-view cone connecting the two. Modeled on
/// GeoSetter's map workflow: the user sees what the camera was pointing at,
/// and clicking moves whichever pin is in "pick" mode. Returns a
/// <see cref="Result"/> carrying both coordinates and the derived bearing.
/// </summary>
public partial class MapPickerWindow : Window {
  private readonly MemoryLayer _cameraPinLayer;
  private readonly MemoryLayer _targetPinLayer;
  private readonly MemoryLayer _beamLayer;

  private GpsCoordinate? _cameraCoordinate;
  private GpsCoordinate? _targetCoordinate;
  private PickMode _mode = PickMode.Camera;
  private readonly double _coneHalfAngleDeg;
  private readonly double? _directionHint;

  public MapPickerWindow() : this(null) { }

  /// <summary>
  /// When <paramref name="initialDirectionDegrees"/> is supplied, a direction
  /// cone is drawn from the camera pin in that compass bearing — useful for
  /// the "show me what GeoSetter metadata is set" overview even before the
  /// user has picked a target. If both direction and target are set, the
  /// target wins and the cone runs to it.
  /// </summary>
  public MapPickerWindow(
    GpsCoordinate? initialCamera = null,
    GpsCoordinate? initialTarget = null,
    double coneHalfAngleDeg = 22.5,
    double? initialDirectionDegrees = null
  ) {
    this.InitializeComponent();

    this._coneHalfAngleDeg = coneHalfAngleDeg;
    this._directionHint = initialDirectionDegrees;

    this._cameraPinLayer = new MemoryLayer {
      Name = "Camera",
      Style = new SymbolStyle {
        SymbolScale = 0.8,
        Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(230, 46, 134, 222)),  // blue
        Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 2)
      }
    };
    this._targetPinLayer = new MemoryLayer {
      Name = "Target",
      Style = new SymbolStyle {
        SymbolScale = 0.7,
        Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(230, 231, 76, 60)),   // red
        Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 2)
      }
    };
    this._beamLayer = new MemoryLayer {
      Name = "Beam",
      Style = new VectorStyle {
        Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(60, 46, 134, 222)),
        Line = new Mapsui.Styles.Pen(Mapsui.Styles.Color.FromArgb(220, 46, 134, 222), 2)
      }
    };

    if (this.FindControl<MapControl>("MapControl") is { } mc) {
      mc.Map.Layers.Add(OpenStreetMap.CreateTileLayer("PhotoManager"));
      mc.Map.Layers.Add(this._beamLayer);
      mc.Map.Layers.Add(this._cameraPinLayer);
      mc.Map.Layers.Add(this._targetPinLayer);
      // Single-click pans (Mapsui default). Double-click drops the active
      // pin. Right-click clears it. Implemented with PointerPressed +
      // ClickCount plus manual screen→world math via the viewport because
      // Mapsui's Info event wasn't reliably firing on the second press of
      // a double-click sequence.
      mc.PointerPressed += this.OnMapPointerPressed;

      // Center on whichever pin we have, else a reasonable default. Assign
      // Home (used on first render) AND explicitly navigate the navigator
      // — assigning Home alone misses the initial paint on some platforms.
      var (centerLon, centerLat, zoom) = ComputeInitialView(initialCamera, initialTarget);
      var (mx, my) = SphericalMercator.FromLonLat(centerLon, centerLat);
      var centerPoint = new MPoint(mx, my);
      mc.Map.Home = n => n.CenterOnAndZoomTo(centerPoint, zoom);
      mc.Map.Navigator.CenterOnAndZoomTo(centerPoint, zoom);

      if (initialCamera is { } cam) {
        this._cameraCoordinate = cam;
        this.SetPin(this._cameraPinLayer, cam);
      }
      if (initialTarget is { } tgt) {
        this._targetCoordinate = tgt;
        this.SetPin(this._targetPinLayer, tgt);
      }

      this.RefreshBeam();
      this.UpdateSummary();
      this.UpdateOkState();
    }
  }

  public Result? PickedResult { get; private set; }

  public sealed record Result(GpsCoordinate Camera, GpsCoordinate? Target, double? BearingDegrees);

  private enum PickMode { Camera, Target }

  private void OnPickModeChanged(object? sender, RoutedEventArgs e) {
    if (this.FindControl<RadioButton>("TargetModeRadio")?.IsChecked == true)
      this._mode = PickMode.Target;
    else
      this._mode = PickMode.Camera;
  }

  private void OnMapPointerPressed(object? sender, PointerPressedEventArgs e) {
    if (sender is not MapControl mc)
      return;

    var properties = e.GetCurrentPoint(mc).Properties;

    // Right-click clears the pin for the active mode.
    if (properties.IsRightButtonPressed) {
      if (this._mode == PickMode.Camera) {
        this._cameraCoordinate = null;
        this._cameraPinLayer.Features = Array.Empty<IFeature>();
      } else {
        this._targetCoordinate = null;
        this._targetPinLayer.Features = Array.Empty<IFeature>();
      }
      this.RefreshBeam();
      this.UpdateSummary();
      this.UpdateOkState();
      mc.RefreshGraphics();
      e.Handled = true;
      return;
    }

    // Double-click places a pin. Single-click falls through to Mapsui's
    // built-in pan handler — e.Handled stays false so the gesture isn't
    // interrupted.
    if (!(properties.IsLeftButtonPressed && e.ClickCount >= 2))
      return;

    var screen = e.GetPosition(mc);
    if (!TryScreenToWorld(mc, screen.X, screen.Y, out var worldX, out var worldY))
      return;

    var (lon, lat) = SphericalMercator.ToLonLat(worldX, worldY);
    var coord = new GpsCoordinate(lat, lon);
    if (!coord.IsValid)
      return;

    if (this._mode == PickMode.Camera) {
      this._cameraCoordinate = coord;
      this.SetPin(this._cameraPinLayer, coord);
    } else {
      this._targetCoordinate = coord;
      this.SetPin(this._targetPinLayer, coord);
    }

    this.RefreshBeam();
    this.UpdateSummary();
    this.UpdateOkState();
    mc.RefreshGraphics();
    e.Handled = true;
  }

  /// <summary>
  /// Converts the pointer's screen position (in DIPs relative to the
  /// MapControl) into the viewport's world coordinates. Mapsui 4.1.8's
  /// public Viewport exposes CenterX/Y + Resolution + Width/Height but no
  /// built-in ScreenToWorld, so we do the linear math ourselves. Rotation
  /// is ignored because the picker never rotates the map.
  /// </summary>
  private static bool TryScreenToWorld(MapControl mc, double screenX, double screenY, out double worldX, out double worldY) {
    worldX = worldY = 0;
    var viewport = mc.Map.Navigator.Viewport;
    if (viewport.Width <= 0 || viewport.Height <= 0 || viewport.Resolution <= 0)
      return false;

    worldX = viewport.CenterX + (screenX - viewport.Width / 2.0) * viewport.Resolution;
    worldY = viewport.CenterY - (screenY - viewport.Height / 2.0) * viewport.Resolution;
    return true;
  }

  private void SetPin(MemoryLayer layer, GpsCoordinate coord) {
    var (mx, my) = SphericalMercator.FromLonLat(coord.Longitude, coord.Latitude);
    layer.Features = new[] {
      new GeometryFeature { Geometry = new Point(mx, my) }
    };
  }

  /// <summary>
  /// Rebuilds the cone + centre-line geometry. The cone is a polygon made of
  /// three great-circle rays from the camera: the centre line to the target
  /// and the two FOV edges ±<see cref="_coneHalfAngleDeg"/> wide. Converting
  /// via <see cref="GreatCircle.Destination"/> keeps the cone geometry
  /// accurate even at higher latitudes where simple linear extrapolation
  /// would skew.
  /// </summary>
  private void RefreshBeam() {
    // Three render modes:
    //   camera + target       → cone + line, range = camera→target distance
    //   camera + direction    → cone + line at a synthetic range, so the
    //                           user can SEE where the camera is pointing
    //                           even when no target has been set yet
    //   anything else         → nothing
    if (this._cameraCoordinate is not { } cam) {
      this._beamLayer.Features = Array.Empty<IFeature>();
      return;
    }

    GpsCoordinate targetForBeam;
    double bearing;
    double range;

    if (this._targetCoordinate is { } tgt) {
      bearing = GreatCircle.BearingDegrees(cam, tgt);
      range = GreatCircle.DistanceMeters(cam, tgt);
      targetForBeam = tgt;
    } else if (this._directionHint is { } directionDeg) {
      bearing = directionDeg;
      range = 500;  // synthetic 500 m so the cone is visible at street scale
      targetForBeam = GreatCircle.Destination(cam, bearing, range);
    } else {
      this._beamLayer.Features = Array.Empty<IFeature>();
      return;
    }

    var leftEdge = GreatCircle.Destination(cam, bearing - this._coneHalfAngleDeg, range);
    var rightEdge = GreatCircle.Destination(cam, bearing + this._coneHalfAngleDeg, range);

    var camPt = ToMercatorCoord(cam);
    var tgtPt = ToMercatorCoord(targetForBeam);
    var leftPt = ToMercatorCoord(leftEdge);
    var rightPt = ToMercatorCoord(rightEdge);

    var cone = new NetTopologySuite.Geometries.Polygon(
      new LinearRing(new[] { camPt, leftPt, tgtPt, rightPt, camPt })
    );
    var centreLine = new LineString(new[] { camPt, tgtPt });

    this._beamLayer.Features = new IFeature[] {
      new GeometryFeature { Geometry = cone },
      new GeometryFeature { Geometry = centreLine }
    };
  }

  private static Coordinate ToMercatorCoord(GpsCoordinate c) {
    var (mx, my) = SphericalMercator.FromLonLat(c.Longitude, c.Latitude);
    return new Coordinate(mx, my);
  }

  private void UpdateSummary() {
    if (this.FindControl<TextBlock>("CameraText") is { } camText)
      camText.Text = FormatCoord(this._cameraCoordinate);
    if (this.FindControl<TextBlock>("TargetText") is { } tgtText)
      tgtText.Text = FormatCoord(this._targetCoordinate);

    if (this.FindControl<TextBlock>("BearingText") is { } bearingText) {
      if (this._cameraCoordinate is { } cam && this._targetCoordinate is { } tgt) {
        var bearing = GreatCircle.BearingDegrees(cam, tgt);
        var distance = GreatCircle.DistanceMeters(cam, tgt);
        var inv = CultureInfo.InvariantCulture;
        bearingText.Text = string.Create(inv,
          $"Bearing: {bearing:0.0}°  Distance: {FormatDistance(distance)}");
      } else {
        bearingText.Text = string.Empty;
      }
    }
  }

  private static string FormatCoord(GpsCoordinate? c) {
    if (c is null)
      return "(not set)";

    var inv = CultureInfo.InvariantCulture;
    return string.Create(inv, $"{c.Value.Latitude:0.######}, {c.Value.Longitude:0.######}");
  }

  private static string FormatDistance(double meters) {
    var inv = CultureInfo.InvariantCulture;
    return meters >= 1000
      ? string.Create(inv, $"{meters / 1000:0.##} km")
      : string.Create(inv, $"{meters:0} m");
  }

  private void UpdateOkState() {
    if (this.FindControl<Button>("OkButton") is { } ok)
      ok.IsEnabled = this._cameraCoordinate is not null;
  }

  private void OnOkClick(object? sender, RoutedEventArgs e) {
    if (this._cameraCoordinate is not { } cam) {
      this.Close(null);
      return;
    }

    double? bearing = null;
    if (this._targetCoordinate is { } tgt)
      bearing = GreatCircle.BearingDegrees(cam, tgt);

    this.PickedResult = new Result(cam, this._targetCoordinate, bearing);
    this.Close(this.PickedResult);
  }

  private void OnCancelClick(object? sender, RoutedEventArgs e) => this.Close(null);

  /// <summary>
  /// Returns (lon, lat, zoom). Zoom is a Mapsui resolution — metres per
  /// pixel, smaller = more zoomed-in. 10000 covers a continent, 50 shows a
  /// few city blocks, 5 is street-level. When either coordinate is set we
  /// drop in at street-level so the user can immediately nudge the pin;
  /// otherwise we open wide-angled on Europe.
  /// </summary>
  private static (double Lon, double Lat, double Zoom) ComputeInitialView(
    GpsCoordinate? camera, GpsCoordinate? target
  ) {
    var reference = camera ?? target;
    if (reference is { IsValid: true } c)
      return (c.Longitude, c.Latitude, 20);

    return (10.0, 50.0, 10000);
  }
}
