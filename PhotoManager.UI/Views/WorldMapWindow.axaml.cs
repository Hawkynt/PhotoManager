using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.UI.Avalonia;
using NetTopologySuite.Geometries;
using PhotoManager.Core.Geocoding;
using PhotoManager.Core.Gpx;
using PhotoManager.Core.Metadata;

namespace PhotoManager.UI.Views;

/// <summary>
/// Plots every geotagged photo from the caller-supplied file list onto an
/// OpenStreetMap tile layer. Auto-zooms to fit all pins after the
/// background metadata pass finishes. Clicking a pin highlights the file
/// in the status bar; the Open button closes the window and returns the
/// chosen <see cref="FileInfo"/> so the parent can select that row in its
/// grid.
///
/// Two extra modes layered on top:
///   * Load GPX — overlays a GPX track as a contrasting polyline.
///   * Nearby  — clicking the map drops a draggable radius circle and
///               recolors pins so those inside stand out, those outside dim.
///               Distances use the haversine formula on raw lat/lon.
/// </summary>
public partial class WorldMapWindow : Window {
  private readonly IReadOnlyList<FileInfo> _files;
  private readonly MetadataReader _reader = new();

  // Pin layers — split so Nearby mode can swap styles per-pin without
  // rebuilding everything. Plain pins live in _pinsLayer; when Nearby is on
  // we additionally fill _highlightLayer with the in-radius subset using a
  // larger / brighter style.
  private readonly MemoryLayer _pinsLayer;
  private readonly MemoryLayer _highlightLayer;
  private readonly MemoryLayer _trackLayer;
  private readonly MemoryLayer _radiusLayer;

  private FileInfo? _pickedFile;
  // Maps an in-Mercator coordinate back to its source file. Pin click hits
  // the closest entry within a small pixel radius (Mapsui's Info event
  // gives us the world-space click location).
  private readonly List<(MPoint Point, GpsCoordinate Gps, FileInfo File)> _pinIndex = new();

  // Nearby state.
  private bool _nearbyMode;
  private GpsCoordinate? _nearbyCenter;
  private double _nearbyRadiusMeters = 1_000;

  public WorldMapWindow() : this(Array.Empty<FileInfo>()) { }

  public WorldMapWindow(IReadOnlyList<FileInfo> files) {
    this.InitializeComponent();
    this._files = files;

    this._pinsLayer = new MemoryLayer {
      Name = "Photos",
      Style = new SymbolStyle {
        SymbolScale = 0.6,
        Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(220, 46, 134, 222)),
        Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 1.5)
      }
    };
    this._highlightLayer = new MemoryLayer {
      Name = "Highlighted",
      Style = new SymbolStyle {
        SymbolScale = 1.0,
        Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(240, 241, 196, 15)),
        Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 2)
      }
    };
    this._trackLayer = new MemoryLayer {
      Name = "GPX track",
      Style = new VectorStyle {
        Line = new Mapsui.Styles.Pen(Mapsui.Styles.Color.FromArgb(220, 230, 80, 30), 3) {
          PenStyle = PenStyle.Solid
        }
      }
    };
    this._radiusLayer = new MemoryLayer {
      Name = "Radius",
      Style = new VectorStyle {
        Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(40, 241, 196, 15)),
        Line = new Mapsui.Styles.Pen(Mapsui.Styles.Color.FromArgb(220, 241, 196, 15), 2)
      }
    };

    if (this.FindControl<MapControl>("MapControl") is { } mc) {
      mc.Map.Layers.Add(OpenStreetMap.CreateTileLayer("PhotoManager"));
      // Layer order: track underneath, then pins, then highlight + radius
      // on top so the user always sees the current selection clearly.
      mc.Map.Layers.Add(this._trackLayer);
      mc.Map.Layers.Add(this._radiusLayer);
      mc.Map.Layers.Add(this._pinsLayer);
      mc.Map.Layers.Add(this._highlightLayer);

      // Default Home: wide-angle Europe; auto-fit kicks in once we know
      // where the pins land.
      var (mx, my) = SphericalMercator.FromLonLat(10.0, 50.0);
      mc.Map.Home = n => n.CenterOnAndZoomTo(new MPoint(mx, my), 30000);

      mc.PointerPressed += this.OnMapPointerPressed;
    }

    this.UpdateRadiusText();
    this.Opened += async (_, _) => await this.LoadPinsAsync();
  }

  private async Task LoadPinsAsync() {
    if (this._files.Count == 0) {
      this.SetStatus("No files loaded — scan your library in the main window first.");
      return;
    }

    this.SetStatus($"Reading metadata for {this._files.Count} file(s)...");

    var pins = new ConcurrentBag<(GpsCoordinate Gps, FileInfo File)>();
    var processed = 0;
    var total = this._files.Count;

    try {
      await Parallel.ForEachAsync(this._files, new ParallelOptions {
        MaxDegreeOfParallelism = Environment.ProcessorCount * 2
      }, async (file, ct) => {
        if (!file.Exists)
          return;
        try {
          var md = await this._reader.ReadAsync(file, ct);
          if (md.Gps is { IsValid: true } gps)
            pins.Add((gps, file));
        } catch {
          // One bad file shouldn't abort the whole map.
        }

        var done = Interlocked.Increment(ref processed);
        if (done % 100 == 0)
          Dispatcher.UIThread.Post(() => this.SetStatus($"Reading metadata: {done}/{total}..."));
      });
    } catch (Exception ex) {
      this.SetStatus($"Scan failed: {ex.Message}");
      return;
    }

    var pinList = pins.ToList();
    this.RenderPins(pinList);

    this.SetStatus(pinList.Count == 0
      ? $"No GPS data found in {total} file(s)."
      : $"{pinList.Count} of {total} file(s) have GPS coordinates. Click a pin to select.");
  }

  private void RenderPins(IReadOnlyList<(GpsCoordinate Gps, FileInfo File)> pins) {
    this._pinIndex.Clear();
    var features = new List<IFeature>(pins.Count);
    foreach (var (gps, file) in pins) {
      var (mx, my) = SphericalMercator.FromLonLat(gps.Longitude, gps.Latitude);
      var point = new MPoint(mx, my);
      this._pinIndex.Add((point, gps, file));
      features.Add(new GeometryFeature {
        Geometry = new NetTopologySuite.Geometries.Point(mx, my)
      });
    }
    this._pinsLayer.Features = features;
    this.RefreshHighlightLayer();

    if (this.FindControl<MapControl>("MapControl") is { } mc) {
      this.AutoFit(mc, pins);
      mc.RefreshGraphics();
    }
  }

  /// <summary>
  /// Center + zoom the map to encompass every pin with a small margin.
  /// Single-pin scenarios drop to street-level so the pin isn't a lonely
  /// dot on a continent-wide view.
  /// </summary>
  private void AutoFit(MapControl mc, IReadOnlyList<(GpsCoordinate Gps, FileInfo File)> pins) {
    if (pins.Count == 0)
      return;

    if (pins.Count == 1) {
      var (mx, my) = SphericalMercator.FromLonLat(pins[0].Gps.Longitude, pins[0].Gps.Latitude);
      mc.Map.Navigator.CenterOnAndZoomTo(new MPoint(mx, my), 5);
      return;
    }

    double minMx = double.MaxValue, minMy = double.MaxValue, maxMx = double.MinValue, maxMy = double.MinValue;
    foreach (var (gps, _) in pins) {
      var (mx, my) = SphericalMercator.FromLonLat(gps.Longitude, gps.Latitude);
      if (mx < minMx) minMx = mx;
      if (my < minMy) minMy = my;
      if (mx > maxMx) maxMx = mx;
      if (my > maxMy) maxMy = my;
    }

    var center = new MPoint((minMx + maxMx) / 2, (minMy + maxMy) / 2);
    var width = Math.Max(1, maxMx - minMx);
    var height = Math.Max(1, maxMy - minMy);
    var viewportW = mc.Bounds.Width > 0 ? mc.Bounds.Width : 1000;
    var viewportH = mc.Bounds.Height > 0 ? mc.Bounds.Height : 600;
    var resolution = Math.Max(width / viewportW, height / viewportH) * 1.25;
    mc.Map.Navigator.CenterOnAndZoomTo(center, Math.Max(resolution, 2));
  }

  private void OnMapPointerPressed(object? sender, PointerPressedEventArgs e) {
    if (sender is not MapControl mc)
      return;

    var properties = e.GetCurrentPoint(mc).Properties;
    if (!properties.IsLeftButtonPressed)
      return;

    var screen = e.GetPosition(mc);
    if (!TryScreenToWorld(mc, screen.X, screen.Y, out var worldX, out var worldY))
      return;

    if (this._nearbyMode) {
      // Project click back to lat/lon for haversine math.
      var (lon, lat) = SphericalMercator.ToLonLat(worldX, worldY);
      this._nearbyCenter = new GpsCoordinate(lat, lon);
      this.RefreshHighlightLayer();
      mc.RefreshGraphics();
      e.Handled = true;
      return;
    }

    if (this._pinIndex.Count == 0)
      return;

    // Hit test in pixel space: convert each pin to screen, pick the
    // closest within ~14 px.
    var resolution = mc.Map.Navigator.Viewport.Resolution;
    if (resolution <= 0)
      return;

    const double pickRadiusPx = 14;
    var pickRadiusWorld = pickRadiusPx * resolution;

    FileInfo? closest = null;
    var bestDist = double.MaxValue;
    foreach (var (point, _, file) in this._pinIndex) {
      var dx = point.X - worldX;
      var dy = point.Y - worldY;
      var dist = Math.Sqrt(dx * dx + dy * dy);
      if (dist < pickRadiusWorld && dist < bestDist) {
        bestDist = dist;
        closest = file;
      }
    }

    if (closest is null)
      return;

    this._pickedFile = closest;
    this.SetStatus($"📷 {closest.Name} — {closest.DirectoryName}");
    if (this.FindControl<Button>("OpenButton") is { } btn)
      btn.IsEnabled = true;
    e.Handled = true;
  }

  /// <summary>
  /// Mapsui 4.1.8 doesn't expose a public ScreenToWorld helper, so do the
  /// linear viewport math ourselves — same trick used by MapPickerWindow.
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

  private void OnOpenClick(object? sender, RoutedEventArgs e) => this.Close(this._pickedFile);
  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close(null);

  private async void OnLoadGpxClick(object? sender, RoutedEventArgs e) {
    var topLevel = TopLevel.GetTopLevel(this);
    if (topLevel?.StorageProvider is not { CanOpen: true } storage) {
      this.SetStatus("File picker unavailable on this platform.");
      return;
    }
    var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Select GPX file",
      AllowMultiple = false,
      FileTypeFilter = [new FilePickerFileType("GPX tracks") { Patterns = ["*.gpx"] }]
    });
    if (files.Count == 0)
      return;
    var path = files[0].TryGetLocalPath();
    if (string.IsNullOrWhiteSpace(path)) {
      this.SetStatus("Couldn't resolve a local path for that file.");
      return;
    }

    GpxTrack track;
    try {
      track = await GpxParser.ParseFileAsync(new FileInfo(path));
    } catch (Exception ex) {
      this.SetStatus($"GPX parse failed: {ex.Message}");
      return;
    }

    if (track.PointCount < 2) {
      this.SetStatus("GPX file has fewer than 2 trackpoints — nothing to draw.");
      this._trackLayer.Features = Array.Empty<IFeature>();
      return;
    }

    this.RenderTrack(track);
    if (this.FindControl<ToggleButton>("TrackToggle") is { } toggle) {
      toggle.IsEnabled = true;
      toggle.IsChecked = true;
    }
    this.SetStatus($"Loaded GPX track ({track.PointCount} points).");
  }

  private void RenderTrack(GpxTrack track) {
    var coords = new Coordinate[track.PointCount];
    for (var i = 0; i < track.PointCount; i++) {
      var p = track.Points[i].Coordinate;
      var (mx, my) = SphericalMercator.FromLonLat(p.Longitude, p.Latitude);
      coords[i] = new Coordinate(mx, my);
    }
    var line = new LineString(coords);
    this._trackLayer.Features = new IFeature[] {
      new GeometryFeature { Geometry = line }
    };
    if (this.FindControl<MapControl>("MapControl") is { } mc)
      mc.RefreshGraphics();
  }

  private void OnTrackToggleChanged(object? sender, RoutedEventArgs e) {
    if (sender is not ToggleButton toggle)
      return;
    var visible = toggle.IsChecked ?? false;
    this._trackLayer.Enabled = visible;
    toggle.Content = visible ? "🛤 Hide track" : "🛤 Show track";
    if (this.FindControl<MapControl>("MapControl") is { } mc)
      mc.RefreshGraphics();
  }

  private void OnNearbyToggleChanged(object? sender, RoutedEventArgs e) {
    if (sender is not ToggleButton toggle)
      return;
    this._nearbyMode = toggle.IsChecked ?? false;
    if (!this._nearbyMode) {
      this._nearbyCenter = null;
      this._radiusLayer.Features = Array.Empty<IFeature>();
    }
    this.RefreshHighlightLayer();
    if (this.FindControl<MapControl>("MapControl") is { } mc)
      mc.RefreshGraphics();
    this.SetStatus(this._nearbyMode
      ? "Nearby mode: click the map to set the search center."
      : $"{this._pinIndex.Count} pin(s) loaded.");
  }

  private void OnRadiusSliderChanged(object? sender, RangeBaseValueChangedEventArgs e) {
    this.UpdateRadiusFromSlider();
    this.RefreshHighlightLayer();
    if (this.FindControl<MapControl>("MapControl") is { } mc)
      mc.RefreshGraphics();
  }

  private void UpdateRadiusFromSlider() {
    if (this.FindControl<Slider>("RadiusSlider") is not { } slider)
      return;
    // Log scale 100 m … 50 km. Slider 0..100 maps linearly to log10
    // [2..log10(50000)] ≈ [2..4.699].
    const double minLog = 2.0;
    const double maxLog = 4.69897;
    var t = slider.Value / 100.0;
    var log = minLog + (maxLog - minLog) * t;
    this._nearbyRadiusMeters = Math.Pow(10, log);
    this.UpdateRadiusText();
  }

  private void UpdateRadiusText() {
    if (this.FindControl<TextBlock>("RadiusText") is not { } text)
      return;
    text.Text = this._nearbyRadiusMeters >= 1_000
      ? string.Create(CultureInfo.InvariantCulture, $"{this._nearbyRadiusMeters / 1_000:0.0} km")
      : string.Create(CultureInfo.InvariantCulture, $"{this._nearbyRadiusMeters:0} m");
  }

  /// <summary>
  /// Repaints the highlight + radius layers based on Nearby mode + center +
  /// radius. When Nearby is off the highlight layer is empty (so plain pins
  /// show through) and the pin layer reverts to its full opacity.
  /// </summary>
  private void RefreshHighlightLayer() {
    if (!this._nearbyMode || this._nearbyCenter is not { } center) {
      this._highlightLayer.Features = Array.Empty<IFeature>();
      this._radiusLayer.Features = Array.Empty<IFeature>();
      // Restore full pin style.
      this._pinsLayer.Style = new SymbolStyle {
        SymbolScale = 0.6,
        Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(220, 46, 134, 222)),
        Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 1.5)
      };
      return;
    }

    // Dim the base pin layer; the in-radius pins get repainted bright on the
    // highlight layer above. SymbolStyle is shared across features so a
    // single style swap covers every pin.
    this._pinsLayer.Style = new SymbolStyle {
      SymbolScale = 0.5,
      Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(80, 46, 134, 222)),
      Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.FromArgb(120, 255, 255, 255), 1)
    };

    var highlights = new List<IFeature>();
    foreach (var (point, gps, _) in this._pinIndex) {
      var d = GeoDistance.HaversineMeters(center.Latitude, center.Longitude, gps.Latitude, gps.Longitude);
      if (d > this._nearbyRadiusMeters)
        continue;
      highlights.Add(new GeometryFeature {
        Geometry = new NetTopologySuite.Geometries.Point(point.X, point.Y)
      });
    }
    this._highlightLayer.Features = highlights;
    this._radiusLayer.Features = new IFeature[] { BuildRadiusFeature(center, this._nearbyRadiusMeters) };

    this.SetStatus($"Nearby: {highlights.Count} pin(s) within {this._nearbyRadiusMeters:0} m of click.");
  }

  /// <summary>
  /// Approximates a great-circle radius circle as a 64-vertex polygon in
  /// Mercator coordinates. Precision is fine at the zoom levels users will
  /// see for radii up to ~50 km — well within the slider range.
  /// </summary>
  private static IFeature BuildRadiusFeature(GpsCoordinate center, double radiusMeters) {
    const int segments = 64;
    var coords = new Coordinate[segments + 1];
    for (var i = 0; i < segments; i++) {
      var bearing = i * 360.0 / segments;
      var edge = GreatCircle.Destination(center, bearing, radiusMeters);
      var (mx, my) = SphericalMercator.FromLonLat(edge.Longitude, edge.Latitude);
      coords[i] = new Coordinate(mx, my);
    }
    coords[segments] = coords[0];
    var ring = new LinearRing(coords);
    return new GeometryFeature { Geometry = new NetTopologySuite.Geometries.Polygon(ring) };
  }

  private void SetStatus(string text) {
    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = text;
  }
}
