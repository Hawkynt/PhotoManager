using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using Mapsui.UI.Avalonia;
using NetTopologySuite.Geometries;
using PhotoManager.Core.Metadata;

namespace PhotoManager.UI.Views;

/// <summary>
/// Plots every geotagged photo from the caller-supplied file list onto an
/// OpenStreetMap tile layer. Auto-zooms to fit all pins after the
/// background metadata pass finishes. Clicking a pin highlights the file
/// in the status bar; the Open button closes the window and returns the
/// chosen <see cref="FileInfo"/> so the parent can select that row in its
/// grid.
/// </summary>
public partial class WorldMapWindow : Window {
  private readonly IReadOnlyList<FileInfo> _files;
  private readonly MetadataReader _reader = new();
  private readonly MemoryLayer _pinsLayer;
  private FileInfo? _pickedFile;
  // Maps an in-Mercator coordinate back to its source file. Pin click hits
  // the closest entry within a small pixel radius (Mapsui's Info event
  // gives us the world-space click location).
  private readonly List<(MPoint Point, FileInfo File)> _pinIndex = new();

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

    if (this.FindControl<MapControl>("MapControl") is { } mc) {
      mc.Map.Layers.Add(OpenStreetMap.CreateTileLayer("PhotoManager"));
      mc.Map.Layers.Add(this._pinsLayer);

      // Default Home: wide-angle Europe; auto-fit kicks in once we know
      // where the pins land.
      var (mx, my) = SphericalMercator.FromLonLat(10.0, 50.0);
      mc.Map.Home = n => n.CenterOnAndZoomTo(new MPoint(mx, my), 30000);

      mc.PointerPressed += this.OnMapPointerPressed;
    }

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
      this._pinIndex.Add((point, file));
      features.Add(new GeometryFeature {
        Geometry = new NetTopologySuite.Geometries.Point(mx, my)
      });
    }
    this._pinsLayer.Features = features;

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
    if (this._pinIndex.Count == 0)
      return;

    var properties = e.GetCurrentPoint(mc).Properties;
    if (!properties.IsLeftButtonPressed)
      return;

    var screen = e.GetPosition(mc);
    if (!TryScreenToWorld(mc, screen.X, screen.Y, out var worldX, out var worldY))
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
    foreach (var (point, file) in this._pinIndex) {
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

  private void SetStatus(string text) {
    if (this.FindControl<TextBlock>("StatusText") is { } status)
      status.Text = text;
  }
}
