using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using PhotoManager.Core;
using PhotoManager.Core.Detection;
using PhotoManager.Core.Enums;
using PhotoManager.Core.Faces;
using PhotoManager.Core.Geocoding;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Models;
using PhotoManager.Core.Previews;
using PhotoManager.Core.Regions;
using PhotoManager.Core.Services;
using PhotoManager.UI.Controllers;
using PhotoManager.UI.Models;
using PhotoManager.UI.Resources;
using PhotoManager.UI.Services;

namespace PhotoManager.UI.Views;

public partial class MainWindow : Window {
  private const string UiDateFormat = "dd.MM.yyyy HH:mm:ss";
  private readonly MainController _controller;
  private readonly AboutController _aboutController;
  private readonly IMetadataReader _metadataReader = new MetadataReader();
  private readonly IMetadataWriter _metadataWriter = new CompositeMetadataWriter();
  private readonly DetectionService _detectionService = BuildDetectionService();
  private readonly RegionService _regionService = new(new MetadataReader(), new CompositeMetadataWriter());

  private static DetectionService BuildDetectionService() {
    // YOLO first; if the model isn't present it returns empty and the
    // composite falls through to the path-derived heuristic.
    var yolo = new YoloObjectDetector();
    var composite = new CompositeDetector(yolo, new PathDerivedDetector());
    return new DetectionService(composite, new MetadataReader(), new CompositeMetadataWriter());
  }
  private ObservableCollection<FileItemModel>? _currentFileItems;
  private FileInfo? _currentFile;
  private FullMetadata? _currentMetadata;
  private CancellationTokenSource? _previewCts;

  // Click-drag region creation state.
  private bool _isDrawingRegion;
  private Avalonia.Point? _dragStart;
  private Rectangle? _activeDragRect;

  // Mini-map layers. Created once and reused so edits swap features instead
  // of rebuilding the map on every refresh.
  private Mapsui.Layers.MemoryLayer? _miniMapCameraLayer;
  private Mapsui.Layers.MemoryLayer? _miniMapTargetLayer;
  private Mapsui.Layers.MemoryLayer? _miniMapBeamLayer;

  // Cache of what the region overlay last rendered so we can skip redundant
  // layout-driven rebuilds. Critical guard: LayoutUpdated fires after our
  // own Children.Clear/Add, which would otherwise cascade into an infinite
  // loop and crash on images with regions.
  private object? _lastRenderedMetadataRef;
  private double _lastRenderBoundsSignature = double.NaN;

  public MainWindow() : this(null!, null!) { }

  public MainWindow(MainController controller, AboutController aboutController) {
    this._controller = controller;
    this._aboutController = aboutController;

    this.InitializeComponent();

    if (controller == null)
      return;

    this.DataContext = controller.ViewModel;

    var combo = this.FindControl<ComboBox>("DuplicateHandlingCombo");
    if (combo != null)
      combo.ItemsSource = Enum.GetValues<DuplicateHandling>();

    var tree = this.FindControl<TreeView>("SourceTree");
    if (tree != null)
      tree.ItemsSource = controller.SourceTreeRoots;

    this.InitializeEditorOptions();
    this.InitializeMiniMap();

    // Redraw the region overlay whenever layout updates. LayoutUpdated is
    // fired after every arrange pass on the associated control, so it
    // reliably catches splitter drags / window resizes where BoundsProperty
    // changes can fire unevenly between the Image and the Canvas.
    void RedrawOverlay(object? _, EventArgs __) {
      if (this._currentMetadata is { } md)
        this.RenderRegionOverlay(md);
    }

    if (this.FindControl<Image>("PreviewImage") is { } preview)
      preview.LayoutUpdated += RedrawOverlay;

    // Hook up click-drag on the overlay canvas for manual region creation.
    // The canvas's Background="Transparent" (set in XAML) makes the empty
    // image area hit-testable, which is what allows Draw-Box to receive
    // presses at all.
    if (this.FindControl<Canvas>("RegionOverlay") is { } overlay) {
      overlay.IsHitTestVisible = true;
      overlay.PointerPressed += this.OnOverlayPointerPressed;
      overlay.PointerMoved += this.OnOverlayPointerMoved;
      overlay.PointerReleased += this.OnOverlayPointerReleased;
      overlay.LayoutUpdated += RedrawOverlay;
    }

    this.Opened += this.OnWindowOpened;
  }

  private void InitializeEditorOptions() {
    if (this.FindControl<ComboBox>("RatingCombo") is { } ratingCombo) {
      ratingCombo.ItemsSource = new object[] { "—", -1, 0, 1, 2, 3, 4, 5 };
      ratingCombo.SelectedIndex = 0;
    }

    if (this.FindControl<ComboBox>("LabelCombo") is { } labelCombo) {
      labelCombo.ItemsSource = new[] { "—", "Red", "Yellow", "Green", "Blue", "Purple" };
      labelCombo.SelectedIndex = 0;
    }
  }

  /// <summary>
  /// Wire up the Edit-metadata mini-map: OSM tile layer + three feature
  /// layers (camera pin, target pin, direction beam). Hook TextChanged on
  /// the GPS / target / direction boxes so the map tracks edits live.
  /// </summary>
  private void InitializeMiniMap() {
    if (this.FindControl<Mapsui.UI.Avalonia.MapControl>("MiniMapControl") is not { } mc)
      return;

    this._miniMapCameraLayer = new Mapsui.Layers.MemoryLayer {
      Name = "Camera",
      Style = new Mapsui.Styles.SymbolStyle {
        SymbolScale = 0.6,
        Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(230, 46, 134, 222)),
        Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 2)
      }
    };
    this._miniMapTargetLayer = new Mapsui.Layers.MemoryLayer {
      Name = "Target",
      Style = new Mapsui.Styles.SymbolStyle {
        SymbolScale = 0.55,
        Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(230, 231, 76, 60)),
        Outline = new Mapsui.Styles.Pen(Mapsui.Styles.Color.White, 2)
      }
    };
    this._miniMapBeamLayer = new Mapsui.Layers.MemoryLayer {
      Name = "Beam",
      Style = new Mapsui.Styles.VectorStyle {
        Fill = new Mapsui.Styles.Brush(Mapsui.Styles.Color.FromArgb(60, 46, 134, 222)),
        Line = new Mapsui.Styles.Pen(Mapsui.Styles.Color.FromArgb(220, 46, 134, 222), 2)
      }
    };

    mc.Map.Layers.Add(Mapsui.Tiling.OpenStreetMap.CreateTileLayer("PhotoManager"));
    mc.Map.Layers.Add(this._miniMapBeamLayer);
    mc.Map.Layers.Add(this._miniMapCameraLayer);
    mc.Map.Layers.Add(this._miniMapTargetLayer);

    // Sensible initial view over Europe so the first render isn't a grey slab.
    var (mx, my) = Mapsui.Projections.SphericalMercator.FromLonLat(10.0, 50.0);
    mc.Map.Home = n => n.CenterOnAndZoomTo(new Mapsui.MPoint(mx, my), 10000);

    foreach (var name in new[] { "GpsLatBox", "GpsLonBox", "TargetLatBox", "TargetLonBox", "DirectionBox" }) {
      if (this.FindControl<TextBox>(name) is { } tb)
        tb.TextChanged += (_, _) => this.RefreshMiniMap();
    }
  }

  /// <summary>
  /// Re-project the current text-box values onto the mini-map. Parses GPS,
  /// Target, and Direction from the editable fields so live edits show up;
  /// skips the pin/beam when values are missing or invalid. When
  /// <paramref name="forceCenter"/> is true, recenters + zooms in to
  /// street-level regardless of the current zoom — used after picker /
  /// triangulation / geocode / elevation actions so the user sees the
  /// effect of their choice. Idle text-edit refreshes leave the view alone
  /// unless currently showing the wide default.
  /// </summary>
  private void RefreshMiniMap(bool forceCenter = false) {
    if (this.FindControl<Mapsui.UI.Avalonia.MapControl>("MiniMapControl") is not { } mc)
      return;
    if (this._miniMapCameraLayer is null || this._miniMapTargetLayer is null || this._miniMapBeamLayer is null)
      return;

    var camera = TryParseCoordinate(this.FindControl<TextBox>("GpsLatBox")?.Text, this.FindControl<TextBox>("GpsLonBox")?.Text);
    var target = TryParseCoordinate(this.FindControl<TextBox>("TargetLatBox")?.Text, this.FindControl<TextBox>("TargetLonBox")?.Text);
    var direction = TryParseDouble(this.FindControl<TextBox>("DirectionBox")?.Text);

    this._miniMapCameraLayer.Features = camera is { } cam
      ? new[] { new Mapsui.Nts.GeometryFeature { Geometry = ToMercatorPoint(cam) } }
      : Array.Empty<Mapsui.IFeature>();

    this._miniMapTargetLayer.Features = target is { } tgt
      ? new[] { new Mapsui.Nts.GeometryFeature { Geometry = ToMercatorPoint(tgt) } }
      : Array.Empty<Mapsui.IFeature>();

    this._miniMapBeamLayer.Features = BuildBeamFeatures(camera, target, direction);

    // Target resolution of 2 is street-level detail so the user actually
    // sees the pin landing on the right block, not just in the right city.
    // Gate auto-center on "camera is set" when forceCenter=true — the
    // caller explicitly requested a recenter after an action and the user
    // only cares once the camera position is known.
    var focus = camera ?? target;
    var shouldCenter = forceCenter
      ? camera is { IsValid: true }
      : focus is { IsValid: true } && mc.Map.Navigator.Viewport.Resolution > 50;
    if (shouldCenter && focus is { IsValid: true } f) {
      var (mx, my) = Mapsui.Projections.SphericalMercator.FromLonLat(f.Longitude, f.Latitude);
      mc.Map.Navigator.CenterOnAndZoomTo(new Mapsui.MPoint(mx, my), 2);
    }

    mc.RefreshGraphics();
  }

  private static Mapsui.IFeature[] BuildBeamFeatures(GpsCoordinate? camera, GpsCoordinate? target, double? directionDegrees) {
    if (camera is not { IsValid: true } cam)
      return Array.Empty<Mapsui.IFeature>();

    double bearing;
    double range;
    GpsCoordinate endpoint;

    if (target is { IsValid: true } tgt) {
      bearing = PhotoManager.Core.Geocoding.GreatCircle.BearingDegrees(cam, tgt);
      range = PhotoManager.Core.Geocoding.GreatCircle.DistanceMeters(cam, tgt);
      endpoint = tgt;
    } else if (directionDegrees is { } dd) {
      bearing = dd;
      range = 500;
      endpoint = PhotoManager.Core.Geocoding.GreatCircle.Destination(cam, bearing, range);
    } else {
      return Array.Empty<Mapsui.IFeature>();
    }

    const double coneHalfAngleDeg = 22.5;
    var leftEdge = PhotoManager.Core.Geocoding.GreatCircle.Destination(cam, bearing - coneHalfAngleDeg, range);
    var rightEdge = PhotoManager.Core.Geocoding.GreatCircle.Destination(cam, bearing + coneHalfAngleDeg, range);

    var camPt = ToMercatorCoord(cam);
    var endPt = ToMercatorCoord(endpoint);
    var leftPt = ToMercatorCoord(leftEdge);
    var rightPt = ToMercatorCoord(rightEdge);

    var cone = new NetTopologySuite.Geometries.Polygon(
      new NetTopologySuite.Geometries.LinearRing(new[] { camPt, leftPt, endPt, rightPt, camPt })
    );
    var centreLine = new NetTopologySuite.Geometries.LineString(new[] { camPt, endPt });

    return new Mapsui.IFeature[] {
      new Mapsui.Nts.GeometryFeature { Geometry = cone },
      new Mapsui.Nts.GeometryFeature { Geometry = centreLine }
    };
  }

  private static NetTopologySuite.Geometries.Point ToMercatorPoint(GpsCoordinate c) {
    var (mx, my) = Mapsui.Projections.SphericalMercator.FromLonLat(c.Longitude, c.Latitude);
    return new NetTopologySuite.Geometries.Point(mx, my);
  }

  private static NetTopologySuite.Geometries.Coordinate ToMercatorCoord(GpsCoordinate c) {
    var (mx, my) = Mapsui.Projections.SphericalMercator.FromLonLat(c.Longitude, c.Latitude);
    return new NetTopologySuite.Geometries.Coordinate(mx, my);
  }

  private static GpsCoordinate? TryParseCoordinate(string? lat, string? lon) {
    if (!TryParseDouble(lat).HasValue || !TryParseDouble(lon).HasValue)
      return null;
    var coord = new GpsCoordinate(TryParseDouble(lat)!.Value, TryParseDouble(lon)!.Value);
    return coord.IsValid ? coord : null;
  }

  private static double? TryParseDouble(string? text) {
    if (string.IsNullOrWhiteSpace(text))
      return null;
    // Accept both "." and "," as decimal separators so the boxes work for
    // users with locale-localized inputs without hunting for the right key.
    var normalized = text.Trim().Replace(',', '.');
    return double.TryParse(normalized, System.Globalization.NumberStyles.Float,
      System.Globalization.CultureInfo.InvariantCulture, out var value)
      ? value
      : null;
  }

  private async void OnWindowOpened(object? sender, EventArgs e) {
    await this._controller.LoadSettingsAsync();

    // Restoring a session with saved checked source paths should land the
    // user on a populated grid — otherwise every launch requires a manual
    // Scan click before anything else (face gallery, search, edit) works.
    if (this._controller.GetCheckedSourcePaths().Count > 0)
      await this.ScanCheckedSourcesAsync();
  }

  private async Task ScanCheckedSourcesAsync() {
    var grid = this.FindControl<DataGrid>("FilesGrid");
    if (grid != null)
      grid.ItemsSource = null;

    var items = await this._controller.ScanCheckedSourcesAsync();
    this._currentFileItems = items;

    if (grid != null)
      grid.ItemsSource = items;
  }

  private async void OnBrowseDestinationClick(object? sender, RoutedEventArgs e)
    => await this._controller.SelectDestinationDirectoryAsync();

  private async void OnAddPathClick(object? sender, RoutedEventArgs e)
    => await this._controller.AddSourcePathInteractiveAsync();

  private void OnRemovePathClick(object? sender, RoutedEventArgs e) {
    if (this.GetSelectedRoot() is { } root)
      this._controller.RemoveSourceTreeRoot(root);
  }

  private void OnToggleRecursiveClick(object? sender, RoutedEventArgs e) {
    if (this.GetSelectedRoot() is { } root)
      this._controller.ToggleRecursive(root);
  }

  private SourceTreeNode? GetSelectedRoot() {
    var tree = this.FindControl<TreeView>("SourceTree");
    if (tree?.SelectedItem is not SourceTreeNode node)
      return null;

    return node.IsRoot ? node : null;
  }

  private async void OnScanClick(object? sender, RoutedEventArgs e) => await this.ScanCheckedSourcesAsync();

  private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => this.ApplySearchFilter();

  private void OnClearSearchClick(object? sender, RoutedEventArgs e) {
    if (this.FindControl<TextBox>("SearchBox") is { } box)
      box.Text = string.Empty;
    this.ApplySearchFilter();
  }

  /// <summary>
  /// Re-filter the grid against the current search box contents. Space splits
  /// the input into AND tokens; each token must be a substring of some row's
  /// <see cref="FileItemModel.SearchIndex"/>. No disk IO — the index is built
  /// in the background during scan.
  /// </summary>
  private void ApplySearchFilter() {
    if (this.FindControl<DataGrid>("FilesGrid") is not { } grid)
      return;
    if (this._currentFileItems is null)
      return;

    var query = this.FindControl<TextBox>("SearchBox")?.Text;
    var tokens = (query ?? string.Empty)
      .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(t => t.ToLowerInvariant())
      .ToArray();

    if (tokens.Length == 0) {
      grid.ItemsSource = this._currentFileItems;
      return;
    }

    var filtered = new ObservableCollection<FileItemModel>(
      this._currentFileItems.Where(item => MatchesAllTokens(item, tokens))
    );
    grid.ItemsSource = filtered;
  }

  private static bool MatchesAllTokens(FileItemModel item, string[] tokensLower) {
    // SearchIndex is already lower-cased by the indexer; combining it with
    // the filename and source path defends against a search-before-index
    // race — filter still hits something sensible even if the background
    // metadata read hasn't finished yet.
    var hay = item.SearchIndex;
    if (hay.Length == 0)
      hay = (item.FileName + " " + item.SourcePath).ToLowerInvariant();

    foreach (var token in tokensLower)
      if (!hay.Contains(token, StringComparison.Ordinal))
        return false;
    return true;
  }

  private async void OnRunClick(object? sender, RoutedEventArgs e) {
    if (this._currentFileItems == null || this._currentFileItems.Count == 0)
      return;

    await this._controller.ProcessSelectedFilesAsync(this._currentFileItems);
  }

  private async void OnRunDirectoryClick(object? sender, RoutedEventArgs e)
    => await this._controller.ProcessDirectoryAsync();

  private void OnCancelClick(object? sender, RoutedEventArgs e)
    => this._controller.CancelProcessing();

  private async void OnAboutClick(object? sender, RoutedEventArgs e) {
    var window = new AboutWindow(this._aboutController);
    await window.ShowDialog(this);
  }

  private async void OnOpenModelsDialogClick(object? sender, RoutedEventArgs e) {
    var window = new ModelDownloadWindow();
    await window.ShowDialog(this);
  }

  private async void OnOpenFaceGalleryClick(object? sender, RoutedEventArgs e) {
    // The face gallery always runs against the currently-scanned file list.
    // Empty list = empty gallery with a hint to scan in the main window.
    var scanned = this._currentFileItems?
      .Select(i => i.FileInfo)
      .Where(f => f != null && f.Exists)
      .Cast<FileInfo>()
      .ToList() ?? new List<FileInfo>();

    var window = new FaceGalleryWindow(scanned);
    await window.ShowDialog(this);
  }

  private async void OnOpenSearchClick(object? sender, RoutedEventArgs e) {
    var seedFolder = this.SeedFolderFromTree();
    var window = seedFolder == null ? new SearchWindow() : new SearchWindow(seedFolder);
    await window.ShowDialog(this);
  }

  private async void OnOpenGpxClick(object? sender, RoutedEventArgs e) {
    var seedFolder = this.SeedFolderFromTree();
    var window = seedFolder == null ? new GpxGeotagWindow() : new GpxGeotagWindow(seedFolder);
    await window.ShowDialog(this);
  }

  private async void OnFilePropertiesClick(object? sender, RoutedEventArgs e) {
    var target = this.PickContextFile();
    if (target is null) {
      this._controller.ViewModel.StatusMessage = "Right-click a file first.";
      return;
    }

    await this.ShowPropertiesDialogAsync(target);
  }

  private async void OnShowPropertiesForCurrentFileClick(object? sender, RoutedEventArgs e) {
    if (this._currentFile is not { Exists: true } file)
      return;
    await this.ShowPropertiesDialogAsync(file);
  }

  private async Task ShowPropertiesDialogAsync(FileInfo target) {
    var props = new PropertiesWindow(target);
    var saved = await props.ShowDialog<bool>(this);
    if (saved && this._currentFile is { } current
        && string.Equals(current.FullName, target.FullName, StringComparison.OrdinalIgnoreCase)) {
      // If the file under edit is still the selected preview, refresh.
      await this.ReloadCurrentMetadataAsync(current);
    }
  }

  private void OnOpenInViewerClick(object? sender, RoutedEventArgs e) {
    var target = this.PickContextFile();
    if (target is null)
      return;
    try {
      ShellLauncher.OpenInDefaultViewer(target.FullName);
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = $"Open failed: {ex.Message}";
    }
  }

  private void OnRevealInFolderClick(object? sender, RoutedEventArgs e) {
    var target = this.PickContextFile();
    if (target?.Directory is null)
      return;
    try {
      ShellLauncher.OpenInDefaultViewer(target.Directory.FullName);
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = $"Open folder failed: {ex.Message}";
    }
  }

  /// <summary>
  /// The file that a context-menu action should operate on: the first
  /// selected grid row, falling back to the currently previewed file if the
  /// grid has nothing selected (e.g. user right-clicked whitespace).
  /// </summary>
  private FileInfo? PickContextFile() {
    if (this.FindControl<DataGrid>("FilesGrid") is { } grid
        && grid.SelectedItem is FileItemModel { FileInfo: { Exists: true } f })
      return f;
    return this._currentFile is { Exists: true } ? this._currentFile : null;
  }

  private async void OnOpenDevelopClick(object? sender, RoutedEventArgs e) {
    if (this._currentFile is not { Exists: true } file) {
      this._controller.ViewModel.StatusMessage = "Select a photo first.";
      return;
    }
    var window = new EditImageWindow(file);
    await window.ShowDialog(this);
  }

  private void OnExitClick(object? sender, RoutedEventArgs e) => this.Close();

  private string? SeedFolderFromTree() => this._controller.SourceTreeRoots
    .Select(r => r.Path.FullName)
    .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p));

  private async void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e) {
    if (sender is not DataGrid grid)
      return;

    // Selection gone entirely (user cleared or filtered the grid): wipe the
    // current-file state and flip the edit gate off so Save/Apply/Draw etc.
    // can't fire against a stale _currentFile.
    if (grid.SelectedItem is not FileItemModel { FileInfo.Exists: true } fileItem) {
      this._currentFile = null;
      this._currentMetadata = null;
      this._controller.ViewModel.HasSelectedFile = false;
      return;
    }

    this._previewCts?.Cancel();
    this._previewCts = new CancellationTokenSource();
    var token = this._previewCts.Token;

    await this.UpdatePreviewAsync(fileItem.FileInfo!, token);
    if (token.IsCancellationRequested)
      return;

    await this.UpdateMetadataAsync(fileItem.FileInfo!, token);
    if (token.IsCancellationRequested)
      return;

    this._controller.ViewModel.HasSelectedFile = true;
  }

  private async Task UpdatePreviewAsync(FileInfo file, CancellationToken token) {
    var preview = this.FindControl<Image>("PreviewImage");
    if (preview == null)
      return;

    var bitmap = await ImagePreviewLoader.LoadAsync(file, token);
    if (token.IsCancellationRequested)
      return;

    preview.Source = bitmap;
  }

  private async Task UpdateMetadataAsync(FileInfo file, CancellationToken token) {
    var fileToImport = new FileToImport(file);

    var (winningDate, winningSource) = await this._controller.GetMostLogicalDateWithSourceAsync(fileToImport);
    if (token.IsCancellationRequested)
      return;

    FullMetadata md;
    try {
      md = await this._metadataReader.ReadAsync(file, token);
    } catch {
      md = new FullMetadata();
    }
    if (token.IsCancellationRequested)
      return;

    this._currentFile = file;
    this._currentMetadata = md;

    this.SyncEditorFromMetadata(md, file, winningDate, winningSource);
    this.RefreshRegionsUi(md);
  }

  private void RefreshRegionsUi(FullMetadata md) {
    var list = this.FindControl<ItemsControl>("RegionsList");
    var rows = md.Regions
      .Select((r, i) => new RegionRowViewModel(i, r))
      .ToArray();
    var tiles = rows
      .Select(r => new RegionThumbnailViewModel(r))
      .ToArray();

    if (list != null)
      list.ItemsSource = tiles;

    var summary = this.FindControl<TextBlock>("RegionsSummary");
    if (summary != null) {
      var proposed = md.Regions.Count(r => r.Status == RegionStatus.Proposed);
      var accepted = md.Regions.Count(r => r.Status == RegionStatus.Accepted);
      summary.Text = md.Regions.Count == 0
        ? "No regions tagged."
        : $"{md.Regions.Count} region(s) — {accepted} accepted, {proposed} proposed.";
    }

    this.RenderRegionOverlay(md);

    // Kick off thumbnail extraction in the background; UI updates as each
    // tile resolves via its ViewModel's INotifyPropertyChanged.
    if (this._currentFile is { } file)
      _ = this.PopulateThumbnailsAsync(file, tiles);
  }

  private async Task PopulateThumbnailsAsync(FileInfo file, IReadOnlyList<RegionThumbnailViewModel> tiles) {
    foreach (var tile in tiles) {
      var box = tile.Row.Region.Box;
      byte[]? bytes;
      try {
        bytes = await RegionThumbnailExtractor.CropAsync(file, box);
      } catch {
        bytes = null;
      }

      if (bytes == null)
        continue;

      try {
        using var ms = new MemoryStream(bytes, writable: false);
        var bitmap = new Avalonia.Media.Imaging.Bitmap(ms);
        tile.Thumbnail = bitmap;
      } catch {
        // Bitmap construction failed — leave the tile without a thumbnail.
      }
    }
  }

  private void RenderRegionOverlay(FullMetadata md) {
    var canvas = this.FindControl<Canvas>("RegionOverlay");
    if (canvas == null)
      return;

    // Don't rebuild the overlay while the user is mid-drag — Children.Clear
    // would wipe the preview rectangle they're drawing.
    if (this._isDrawingRegion && this._activeDragRect != null)
      return;

    if (!this.TryGetImageRenderBounds(out var offsetX, out var offsetY, out var renderedW, out var renderedH))
      return;

    // Signature-based early-out: if the bounds haven't changed AND we're
    // rendering the exact same FullMetadata object we already drew, there's
    // nothing to do. LayoutUpdated fires once per frame during layout
    // passes and our own Children mutations re-trigger it, so without this
    // guard we recurse into a stack overflow on images with regions.
    var signature = offsetX * 31 + offsetY * 97 + renderedW * 179 + renderedH * 257 + md.Regions.Count * 1009;
    if (ReferenceEquals(md, this._lastRenderedMetadataRef) && signature == this._lastRenderBoundsSignature)
      return;
    this._lastRenderedMetadataRef = md;
    this._lastRenderBoundsSignature = signature;

    canvas.Children.Clear();

    for (var regionIndex = 0; regionIndex < md.Regions.Count; regionIndex++) {
      var region = md.Regions[regionIndex];
      var color = Color.Parse(region.Category.ToHexColor());
      var strokeBrush = new SolidColorBrush(color);
      var dashStyle = region.Status == RegionStatus.Proposed
        ? new DashStyle(new double[] { 4, 3 }, 0)
        : null;

      // Tag carries the region index so OnOverlayPointerPressed can scroll
      // the thumbnail bar when the user clicks a box. Fill has to be a real
      // brush (not null) for the rectangle's interior to be hit-testable.
      var rect = new Rectangle {
        Width = region.Box.Width * renderedW,
        Height = region.Box.Height * renderedH,
        Stroke = strokeBrush,
        StrokeThickness = 2,
        StrokeDashArray = dashStyle?.Dashes,
        Fill = Brushes.Transparent,
        Tag = regionIndex
      };
      Canvas.SetLeft(rect, offsetX + region.Box.X * renderedW);
      Canvas.SetTop(rect, offsetY + region.Box.Y * renderedH);
      canvas.Children.Add(rect);

      if (!string.IsNullOrWhiteSpace(region.Label)) {
        var label = new Border {
          Background = strokeBrush,
          Padding = new Thickness(4, 1),
          Child = new TextBlock {
            Text = region.Label,
            Foreground = Brushes.White,
            FontSize = 11
          },
          IsHitTestVisible = false
        };
        Canvas.SetLeft(label, offsetX + region.Box.X * renderedW);
        Canvas.SetTop(label, offsetY + region.Box.Y * renderedH - 18);
        canvas.Children.Add(label);
      }
    }
  }

  private void OnDrawRegionToggled(object? sender, RoutedEventArgs e) {
    if (sender is not ToggleButton toggle)
      return;

    this._isDrawingRegion = toggle.IsChecked == true;

    if (this.FindControl<Canvas>("RegionOverlay") is { } canvas) {
      // Canvas itself always catches pointer events so clicks on rectangles
      // can scroll the thumbnail bar. The cursor changes to a crosshair
      // while in draw mode so the user knows they can drag a new box.
      canvas.Cursor = this._isDrawingRegion
        ? new Cursor(StandardCursorType.Cross)
        : Cursor.Default;
    }
  }

  private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e) {
    if (sender is not Canvas canvas)
      return;

    var properties = e.GetCurrentPoint(canvas).Properties;
    if (!properties.IsLeftButtonPressed)
      return;

    // If the click landed on one of the region rectangles (carries an int
    // region index in its Tag), scroll the thumbnail list to that region.
    // Skipped while in draw-mode so the user can start a new box on top of
    // an existing one — the toggle switches the gesture from "select" to
    // "draw" and the existing box shouldn't hijack the press.
    if (!this._isDrawingRegion && e.Source is Control { Tag: int regionIndex }) {
      this.ScrollRegionIntoView(regionIndex);
      return;
    }

    if (!this._isDrawingRegion)
      return;

    e.Pointer.Capture(canvas);
    this._dragStart = e.GetPosition(canvas);
    this._activeDragRect = new Rectangle {
      Stroke = new SolidColorBrush(Color.Parse(RegionCategory.Other.ToHexColor())),
      StrokeThickness = 2,
      StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 },
      Fill = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
      IsHitTestVisible = false
    };
    canvas.Children.Add(this._activeDragRect);
  }

  private void ScrollRegionIntoView(int index) {
    if (this.FindControl<ItemsControl>("RegionsList") is not { } list)
      return;
    var container = list.ContainerFromIndex(index);
    container?.BringIntoView();
  }

  private void OnOverlayPointerMoved(object? sender, PointerEventArgs e) {
    if (!this._isDrawingRegion || this._dragStart is not { } start
        || this._activeDragRect is null || sender is not Canvas canvas)
      return;

    var current = e.GetPosition(canvas);
    var x = Math.Min(start.X, current.X);
    var y = Math.Min(start.Y, current.Y);
    var w = Math.Abs(current.X - start.X);
    var h = Math.Abs(current.Y - start.Y);

    Canvas.SetLeft(this._activeDragRect, x);
    Canvas.SetTop(this._activeDragRect, y);
    this._activeDragRect.Width = w;
    this._activeDragRect.Height = h;
  }

  private async void OnOverlayPointerReleased(object? sender, PointerReleasedEventArgs e) {
    if (!this._isDrawingRegion || this._dragStart is not { } start
        || this._activeDragRect is null || sender is not Canvas canvas) {
      this.ResetDragState();
      return;
    }

    var end = e.GetPosition(canvas);
    var rect = this._activeDragRect;
    this.ResetDragState();
    e.Pointer.Capture(null);

    // Ignore accidental clicks that didn't drag far enough to be a region.
    if (Math.Abs(end.X - start.X) < 8 || Math.Abs(end.Y - start.Y) < 8) {
      canvas.Children.Remove(rect);
      return;
    }

    if (this._currentFile is not { } file || !file.Exists) {
      canvas.Children.Remove(rect);
      return;
    }

    // Translate overlay pixel coordinates to the image's normalized box.
    if (!this.TryGetImageRenderBounds(out var offsetX, out var offsetY, out var renderedW, out var renderedH)
        || renderedW <= 0 || renderedH <= 0) {
      canvas.Children.Remove(rect);
      return;
    }

    var x = Math.Min(start.X, end.X);
    var y = Math.Min(start.Y, end.Y);
    var w = Math.Abs(end.X - start.X);
    var h = Math.Abs(end.Y - start.Y);

    var nx = (float)Math.Clamp((x - offsetX) / renderedW, 0, 1);
    var ny = (float)Math.Clamp((y - offsetY) / renderedH, 0, 1);
    var nw = (float)Math.Clamp(w / renderedW, 0, 1 - nx);
    var nh = (float)Math.Clamp(h / renderedH, 0, 1 - ny);

    // Always leave draw mode right away so the user isn't stuck capturing
    // every mouse click while the category dialog is up.
    if (this.FindControl<ToggleButton>("DrawRegionToggle") is { } toggle)
      toggle.IsChecked = false;
    canvas.Children.Remove(rect);

    // Ask the user what's in the box. Cancelling drops the drawn region
    // entirely — we commit nothing unless they pick a category.
    var picker = new RegionCategoryPickerWindow();
    var pick = await picker.ShowDialog<RegionCategoryPickerWindow.Result?>(this);
    if (pick is null)
      return;

    // Label present + user confirmed the category → store as Accepted so
    // the region contributes to keywords right away. Unlabelled boxes stay
    // Proposed so they're visually distinct as "not tagged yet".
    var status = string.IsNullOrWhiteSpace(pick.Label) ? RegionStatus.Proposed : RegionStatus.Accepted;
    var region = new TaggedRegion(
      new NormalizedBoundingBox(nx, ny, nw, nh),
      pick.Category,
      Label: pick.Label,
      Status: status,
      Source: TaggedRegion.ManualSource
    );

    try {
      await this._regionService.AppendAsync(file, new[] { region });
      await this.ReloadCurrentMetadataAsync(file);
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = $"Draw box failed: {ex.Message}";
    }
  }

  private void ResetDragState() {
    this._dragStart = null;
    this._activeDragRect = null;
  }

  private bool TryGetImageRenderBounds(out double offsetX, out double offsetY, out double renderedW, out double renderedH) {
    offsetX = offsetY = renderedW = renderedH = 0;

    if (this.FindControl<Image>("PreviewImage") is not { } preview
        || preview.Source is not Avalonia.Media.Imaging.Bitmap bitmap
        || this.FindControl<Canvas>("RegionOverlay") is not { } canvas)
      return false;

    var bounds = preview.Bounds;
    if (bounds.Width <= 0 || bounds.Height <= 0)
      return false;

    var sourceW = bitmap.PixelSize.Width;
    var sourceH = bitmap.PixelSize.Height;
    if (sourceW <= 0 || sourceH <= 0)
      return false;

    // The preview uses Stretch="Uniform" with StretchDirection="DownOnly":
    // small images render at their natural pixel size centered inside the
    // Image's bounds rather than upscaled. Clamp scale to 1 to match that.
    var scale = Math.Min(bounds.Width / sourceW, bounds.Height / sourceH);
    if (scale > 1)
      scale = 1;
    renderedW = sourceW * scale;
    renderedH = sourceH * scale;

    // Compute the rendered bitmap's top-left corner in the Image's own
    // coordinate space, then translate to the Canvas's space. Without the
    // translation, overlays drifted when the Image wasn't flush with the
    // Canvas inside the Grid cell (which happens during splitter drag
    // because of subpixel arrange differences).
    var imageLocalOrigin = new Avalonia.Point(
      (bounds.Width - renderedW) / 2,
      (bounds.Height - renderedH) / 2
    );
    var inCanvas = preview.TranslatePoint(imageLocalOrigin, canvas) ?? imageLocalOrigin;
    offsetX = inCanvas.X;
    offsetY = inCanvas.Y;
    return true;
  }

  private async void OnDetectFacesClick(object? sender, RoutedEventArgs e) {
    if (this._currentFile is not { } file || !file.Exists)
      return;

    if (!await this.EnsureModelAsync(ModelRegistry.UltraFaceRfb320, "face detector"))
      return;

    try {
      using var detector = new OnnxFaceDetector();
      using var embedder = new OnnxFaceEmbedder();
      var registry = new PeopleRegistry();
      var service = new FaceRecognitionService(detector, this._metadataReader, this._metadataWriter, registry, embedder);

      var result = await service.DetectAndWriteAsync(file);
      var named = result.Count(r => !string.IsNullOrWhiteSpace(r.PersonName));
      var hint = embedder.IsAvailable
        ? (registry.KnownNames.Any()
            ? $" ({named} auto-named from {registry.KnownNames.Count()} known person(s))"
            : " (tag a face to start training the recognizer)")
        : " — install the ArcFace model to enable automatic recognition";

      this._controller.ViewModel.StatusMessage = $"Detected {result.Count} face(s){hint}.";
      await this.ReloadCurrentMetadataAsync(file);
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = $"Face detection failed: {ex.Message}";
    }
  }

  private async void OnProposeRegionsClick(object? sender, RoutedEventArgs e) {
    if (this._currentFile is not { } file || !file.Exists)
      return;

    if (!await this.EnsureModelAsync(ModelRegistry.YoloV8n, "object detector"))
      return;

    try {
      using var yolo = new YoloObjectDetector();
      var proposer = new YoloRegionProposer(yolo);
      var proposed = await proposer.ProposeAsync(file);

      if (proposed.Count > 0)
        await this._regionService.AppendAsync(file, proposed);

      this._controller.ViewModel.StatusMessage = $"Proposed {proposed.Count} region(s).";
      await this.ReloadCurrentMetadataAsync(file);
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = $"Region proposal failed: {ex.Message}";
    }
  }

  /// <summary>
  /// Checks if the required ONNX model is on disk. When it isn't, pops up a
  /// small info dialog and — if the user accepts — opens the download window.
  /// Returns false if the user needs to download first (either the check
  /// failed or the user has to wait); true if it's safe to proceed.
  /// </summary>
  private async Task<bool> EnsureModelAsync(ModelInfo model, string friendlyName) {
    if (model.IsInstalled())
      return true;

    this._controller.ViewModel.StatusMessage =
      $"The {friendlyName} model isn't installed yet — opening the downloader.";

    var window = new ModelDownloadWindow();
    await window.ShowDialog(this);

    if (model.IsInstalled())
      return true;

    this._controller.ViewModel.StatusMessage =
      $"{friendlyName} model still not installed — click Download or Install from file and try again.";
    return false;
  }

  private async void OnAcceptRegionClick(object? sender, RoutedEventArgs e)
    => await this.RegionAction(sender, async (file, index) => await this._regionService.AcceptAsync(file, index));

  private async void OnDiscardRegionClick(object? sender, RoutedEventArgs e)
    => await this.RegionAction(sender, async (file, index) => await this._regionService.DiscardAsync(file, index));

  private async void OnTagRegionClick(object? sender, RoutedEventArgs e) {
    if (sender is not Button { Tag: int index })
      return;
    if (this._currentFile is not { } file || !file.Exists)
      return;
    if (this._currentMetadata is not { } md || index < 0 || index >= md.Regions.Count)
      return;

    var region = md.Regions[index];
    var registry = new PeopleRegistry();
    var canAddToRegistry = region.Embedding is { Length: > 0 };

    var picker = new PersonPickerWindow(registry.KnownNames, region.Label, canAddToRegistry);
    var result = await picker.ShowDialog<PersonPickerWindow.Result?>(this);
    if (result is null)
      return;

    try {
      // Write the relabeled region (or null label if the user cleared it),
      // accepting the region at the same time — tagging implies confirmation.
      // ALSO strip the new name from any OTHER Person region in this photo:
      // one person can't appear twice on the same photo, so re-assigning
      // a name moves it cleanly instead of spreading it.
      var updated = md.Regions.Select((r, i) => {
        if (i == index)
          return r with { Label = result.Name, Status = RegionStatus.Accepted };
        if (!string.IsNullOrWhiteSpace(result.Name)
            && r.Category == RegionCategory.Person
            && string.Equals(r.Label, result.Name, StringComparison.OrdinalIgnoreCase))
          return r with { Label = null };
        return r;
      }).ToArray();

      // Promote the new label into dc:subject keywords so the photo turns up
      // in library search by that name/word too. Blank name (user cleared
      // the label) leaves keywords alone.
      var edit = new MetadataEdit {
        Regions = Optional<IReadOnlyList<TaggedRegion>>.Set(updated)
      };
      if (!string.IsNullOrWhiteSpace(result.Name)) {
        var mergedKeywords = DetectionService.MergeKeywords(md.Keywords, new[] { result.Name });
        edit = edit with { Keywords = Optional<IReadOnlyList<string>>.Set(mergedKeywords) };
      }

      await this._metadataWriter.ApplyAsync(file, edit);

      if (result.AddToRegistry && result.Name is { } name && region.Embedding is { Length: > 0 } emb) {
        if (registry.EmbeddingCount(name) == 0)
          registry.AddReference(name, emb);
      }

      await this.ReloadCurrentMetadataAsync(file);
      this._controller.ViewModel.StatusMessage = result.Name is null
        ? "Cleared region label."
        : $"Tagged region as \"{result.Name}\".";
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = $"Tag failed: {ex.Message}";
    }
  }

  private async Task RegionAction(object? sender, Func<FileInfo, int, Task> action) {
    if (sender is not Button { Tag: int index })
      return;
    if (this._currentFile is not { } file || !file.Exists)
      return;

    try {
      await action(file, index);
      await this.ReloadCurrentMetadataAsync(file);
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = $"Region action failed: {ex.Message}";
    }
  }

  private async Task ReloadCurrentMetadataAsync(FileInfo file) {
    this._currentMetadata = await this._metadataReader.ReadAsync(file);
    await this.UpdateMetadataAsync(file, CancellationToken.None);
  }

  private static string FormatFileSize(long bytes) {
    string[] sizes = [Strings.FileSize_Bytes, Strings.FileSize_Kilobytes, Strings.FileSize_Megabytes, Strings.FileSize_Gigabytes];
    double len = bytes;
    var order = 0;
    while (len >= 1024 && order < sizes.Length - 1) {
      ++order;
      len /= 1024;
    }
    return $"{len:0.##} {sizes[order]}";
  }

  /// <summary>
  /// Legacy overload used by Revert — rebuild the editor against the
  /// currently-loaded file, preserving the date source/hint we already
  /// computed last time. When there is no _currentFile we don't have a
  /// useful source, so the hint field just clears.
  /// </summary>
  private void SyncEditorFromMetadata(FullMetadata md)
    => this.SyncEditorFromMetadata(md, this._currentFile, winningDate: null, winningSource: DateTimeSource.Unknown);

  private void SyncEditorFromMetadata(FullMetadata md, FileInfo? file, DateTime? winningDate, DateTimeSource winningSource) {
    var inv = System.Globalization.CultureInfo.InvariantCulture;

    // Filename, path, size — read-only labels + the rename-on-save box at
    // the top of the editor.
    if (this.FindControl<TextBox>("FileNameBox") is { } nameBox)
      nameBox.Text = file?.Name ?? string.Empty;
    if (this.FindControl<TextBlock>("PathText") is { } pathText) {
      pathText.Text = file?.FullName ?? string.Empty;
      ToolTip.SetTip(pathText, file?.FullName);
    }
    if (this.FindControl<TextBlock>("SizeText") is { } sizeText)
      sizeText.Text = file is { Exists: true } ? FormatFileSize(file.Length) : string.Empty;

    // Filesystem timestamps. Local time is friendlier than UTC for the UI.
    if (this.FindControl<TextBlock>("CreatedText") is { } createdText)
      createdText.Text = file is { Exists: true } ? file.CreationTime.ToString(UiDateFormat) : string.Empty;
    if (this.FindControl<TextBlock>("ModifiedText") is { } modifiedText)
      modifiedText.Text = file is { Exists: true } ? file.LastWriteTime.ToString(UiDateFormat) : string.Empty;
    if (this.FindControl<TextBlock>("AccessedText") is { } accessedText)
      accessedText.Text = file is { Exists: true } ? file.LastAccessTime.ToString(UiDateFormat) : string.Empty;

    // Capture date: XMP DateCreated wins; otherwise whichever source the
    // importer selected as most logical for the file. The hint string tells
    // the user where the picker's seed value came from so they know whether
    // to overwrite it.
    var captureDate = md.DateCreated ?? winningDate;
    if (this.FindControl<DatePicker>("CaptureDatePicker") is { } dateBox)
      dateBox.SelectedDate = captureDate is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d.Date, DateTimeKind.Unspecified)) : null;
    if (this.FindControl<TimePicker>("CaptureTimePicker") is { } timeBox)
      timeBox.SelectedTime = captureDate is { } t ? t.TimeOfDay : null;
    if (this.FindControl<TextBlock>("CaptureDateHint") is { } hint) {
      hint.Text = md.DateCreated != null
        ? "(from XMP)"
        : winningSource switch {
          DateTimeSource.ExifSubIfd => "(from EXIF Original)",
          DateTimeSource.ExifIfd0 => "(from EXIF Modified)",
          DateTimeSource.Gps => "(from GPS)",
          DateTimeSource.FileName => "(from filename)",
          DateTimeSource.FileModifiedAt => "(from file mtime)",
          DateTimeSource.FileCreatedAt => "(from file ctime)",
          _ => string.Empty,
        };
    }

    if (this.FindControl<TextBox>("GpsLatBox") is { } latBox)
      latBox.Text = md.Gps?.Latitude.ToString("0.######", inv) ?? string.Empty;
    if (this.FindControl<TextBox>("GpsLonBox") is { } lonBox)
      lonBox.Text = md.Gps?.Longitude.ToString("0.######", inv) ?? string.Empty;
    if (this.FindControl<TextBox>("GpsAltBox") is { } altBox)
      altBox.Text = md.Gps?.AltitudeMeters?.ToString("0.##", inv) ?? string.Empty;

    if (this.FindControl<TextBox>("DirectionBox") is { } dirBox)
      dirBox.Text = md.ImageDirection?.Degrees.ToString("0.##", inv) ?? string.Empty;
    if (this.FindControl<CheckBox>("DirectionMagneticCheck") is { } magCheck)
      magCheck.IsChecked = md.ImageDirection?.Reference == PhotoManager.Core.Metadata.DirectionReference.Magnetic;

    if (this.FindControl<TextBox>("TargetLatBox") is { } tgtLat)
      tgtLat.Text = md.TargetGps?.Latitude.ToString("0.######", inv) ?? string.Empty;
    if (this.FindControl<TextBox>("TargetLonBox") is { } tgtLon)
      tgtLon.Text = md.TargetGps?.Longitude.ToString("0.######", inv) ?? string.Empty;

    if (this.FindControl<TextBox>("LocationBox") is { } locBox)
      locBox.Text = md.Location ?? string.Empty;
    if (this.FindControl<TextBox>("CityBox") is { } cityBox)
      cityBox.Text = md.City ?? string.Empty;
    if (this.FindControl<TextBox>("StateBox") is { } stateBox)
      stateBox.Text = md.State ?? string.Empty;
    if (this.FindControl<TextBox>("CountryBox") is { } countryBox)
      countryBox.Text = md.Country ?? string.Empty;
    if (this.FindControl<TextBox>("CountryCodeBox") is { } ccBox)
      ccBox.Text = md.CountryCode ?? string.Empty;

    if (this.FindControl<TextBlock>("ResolvedAddressText") is { } addrText) {
      var parts = new[] { md.Location, md.City, md.State, md.Country }.Where(s => !string.IsNullOrEmpty(s));
      addrText.Text = parts.Any() ? string.Join(", ", parts) : string.Empty;
    }

    if (this.FindControl<ComboBox>("RatingCombo") is { } ratingCombo) {
      object target = md.Rating.HasValue ? md.Rating.Value : "—";
      ratingCombo.SelectedItem = target;
    }

    if (this.FindControl<ComboBox>("LabelCombo") is { } labelCombo)
      labelCombo.SelectedItem = string.IsNullOrEmpty(md.ColorLabel) ? "—" : md.ColorLabel;

    if (this.FindControl<TextBox>("KeywordsBox") is { } keywordsBox)
      keywordsBox.Text = md.Keywords.Count == 0 ? string.Empty : string.Join(", ", md.Keywords);

    if (this.FindControl<TextBox>("TitleBox") is { } titleBox)
      titleBox.Text = md.Title ?? string.Empty;
    if (this.FindControl<TextBox>("CaptionBox") is { } captionBox)
      captionBox.Text = md.Caption ?? string.Empty;
    if (this.FindControl<TextBox>("CreatorBox") is { } creatorBox)
      creatorBox.Text = md.Creator ?? string.Empty;
    if (this.FindControl<TextBox>("CopyrightBox") is { } copyrightBox)
      copyrightBox.Text = md.Copyright ?? string.Empty;
  }

  private void OnRevertMetadataClick(object? sender, RoutedEventArgs e) {
    if (this._currentMetadata is { } md)
      this.SyncEditorFromMetadata(md);
  }

  private async void OnPickOnMapClick(object? sender, RoutedEventArgs e) => await this.OpenMapPickerAsync(startInTargetMode: false);

  private async void OnPickTargetOnMapClick(object? sender, RoutedEventArgs e) => await this.OpenMapPickerAsync(startInTargetMode: true);

  private async Task OpenMapPickerAsync(bool startInTargetMode) {
    var picker = new MapPickerWindow(
      this._currentMetadata?.Gps,
      this._currentMetadata?.TargetGps,
      initialDirectionDegrees: this._currentMetadata?.ImageDirection?.Degrees,
      startInTargetMode: startInTargetMode);
    var result = await picker.ShowDialog<MapPickerWindow.Result?>(this);
    if (result is null)
      return;

    var inv = System.Globalization.CultureInfo.InvariantCulture;

    // Only the pin the picker was opened for actually changed. Null the
    // other side — MainWindow must not stomp fields the picker didn't edit.
    if (result.Camera is { } camera) {
      if (this.FindControl<TextBox>("GpsLatBox") is { } lat)
        lat.Text = camera.Latitude.ToString("0.######", inv);
      if (this.FindControl<TextBox>("GpsLonBox") is { } lon)
        lon.Text = camera.Longitude.ToString("0.######", inv);
      if (camera.AltitudeMeters is { } alt && this.FindControl<TextBox>("GpsAltBox") is { } altBox)
        altBox.Text = alt.ToString("0.##", inv);
    }

    if (result.Target is { } target) {
      if (this.FindControl<TextBox>("TargetLatBox") is { } tgtLat)
        tgtLat.Text = target.Latitude.ToString("0.######", inv);
      if (this.FindControl<TextBox>("TargetLonBox") is { } tgtLon)
        tgtLon.Text = target.Longitude.ToString("0.######", inv);
    }

    if (result.BearingDegrees is { } bearing && this.FindControl<TextBox>("DirectionBox") is { } dirBox)
      dirBox.Text = bearing.ToString("0.##", inv);

    this.RefreshMiniMap(forceCenter: true);
  }

  private async void OnTriangulateClick(object? sender, RoutedEventArgs e) {
    if (this._currentFile is not { Exists: true } file) {
      this._controller.ViewModel.StatusMessage = "Select a photo first.";
      return;
    }

    // Camera GPS is only required for 2-point triangulation. The 3-point
    // resection solves for the camera position from the landmarks, so allow
    // opening the dialog without an existing GPS — we pass (0,0) as a
    // placeholder that the user simply won't use.
    var cameraGps = this._currentMetadata?.Gps ?? new GpsCoordinate(0, 0);

    // Load the photo into a Bitmap for the dialog. Reuse the preview loader
    // so RAW files resolve to their embedded JPEG.
    var bitmap = await ImagePreviewLoader.LoadAsync(file);
    if (bitmap == null) {
      this._controller.ViewModel.StatusMessage = "Couldn't load the photo for triangulation.";
      return;
    }

    var dialog = new TriangulateFromPhotoWindow(bitmap, cameraGps);
    var result = await dialog.ShowDialog<TriangulateFromPhotoWindow.Result?>(this);
    if (result is null)
      return;

    var inv = System.Globalization.CultureInfo.InvariantCulture;
    if (this.FindControl<TextBox>("DirectionBox") is { } dirBox)
      dirBox.Text = result.HeadingDegrees.ToString("0.##", inv);
    if (this.FindControl<CheckBox>("DirectionMagneticCheck") is { } magCheck)
      magCheck.IsChecked = false;
    if (this.FindControl<TextBox>("TargetLatBox") is { } tgtLat)
      tgtLat.Text = result.Target.Latitude.ToString("0.######", inv);
    if (this.FindControl<TextBox>("TargetLonBox") is { } tgtLon)
      tgtLon.Text = result.Target.Longitude.ToString("0.######", inv);

    // If the user solved for the camera position via 3-point resection, also
    // populate the GPS fields so Save Sidecar commits it alongside heading + target.
    if (result.ResectedCamera is { } solved) {
      if (this.FindControl<TextBox>("GpsLatBox") is { } latBox)
        latBox.Text = solved.Latitude.ToString("0.######", inv);
      if (this.FindControl<TextBox>("GpsLonBox") is { } lonBox)
        lonBox.Text = solved.Longitude.ToString("0.######", inv);
    }

    this._controller.ViewModel.StatusMessage = result.ResectedCamera is not null
      ? $"Resected camera + heading {result.HeadingDegrees:0.##}°. Click Save Sidecar to commit."
      : $"Triangulated heading {result.HeadingDegrees:0.##}°. Click Save Sidecar to commit.";

    this.RefreshMiniMap(forceCenter: true);
  }

  private async void OnAutoDetectClick(object? sender, RoutedEventArgs e) {
    if (this._currentFile is not { } file || !file.Exists)
      return;

    if (!await this.EnsureModelAsync(ModelRegistry.YoloV8n, "object detector"))
      return;

    try {
      var result = await this._detectionService.DetectAndWriteKeywordsAsync(file);

      if (result.Labels.Count == 0) {
        this._controller.ViewModel.StatusMessage = "No labels detected";
        return;
      }

      this._controller.ViewModel.StatusMessage =
        $"Detected {result.Labels.Count} label(s): {string.Join(", ", result.DistinctLabelNames())}";

      // Refresh the displayed metadata (keywords may have grown)
      this._currentMetadata = await this._metadataReader.ReadAsync(file);
      await this.UpdateMetadataAsync(file, CancellationToken.None);
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = $"Detection failed: {ex.Message}";
    }
  }

  private async void OnSaveMetadataClick(object? sender, RoutedEventArgs e) {
    if (this._currentFile is not { } file || !file.Exists)
      return;

    // File rename first. If the target already exists we abort everything —
    // silently overwriting someone else's file would be catastrophic, and
    // the user should pick a new name rather than having Save randomly
    // succeed/fail based on directory contents.
    var newName = this.FindControl<TextBox>("FileNameBox")?.Text?.Trim();
    if (!string.IsNullOrWhiteSpace(newName)
        && !string.Equals(newName, file.Name, StringComparison.Ordinal)) {
      var renamed = this.TryRenameCurrentFile(file, newName);
      if (renamed is null)
        return;
      file = renamed;
      this._currentFile = renamed;
    }

    var edit = this.BuildEditFromUi();
    try {
      await this._metadataWriter.ApplyAsync(file, edit);
      this._controller.ViewModel.StatusMessage = $"Saved metadata for {file.Name}";
      this._currentMetadata = await this._metadataReader.ReadAsync(file);
      await this.UpdateMetadataAsync(file, CancellationToken.None);
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = $"Failed to save: {ex.Message}";
    }
  }

  /// <summary>
  /// Attempt to rename <paramref name="file"/> to <paramref name="newName"/>.
  /// Refuses the operation if the new name contains invalid characters or a
  /// file with that name already exists in the same directory — silent
  /// overwrite is unacceptable. Returns the renamed FileInfo on success, or
  /// null if the rename was rejected (status bar already updated with
  /// explanation).
  /// </summary>
  private FileInfo? TryRenameCurrentFile(FileInfo file, string newName) {
    if (newName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) {
      this._controller.ViewModel.StatusMessage = $"Invalid characters in filename \"{newName}\".";
      return null;
    }

    var directory = file.Directory;
    if (directory is null) {
      this._controller.ViewModel.StatusMessage = "Can't determine target directory for rename.";
      return null;
    }

    var target = new FileInfo(System.IO.Path.Combine(directory.FullName, newName));
    if (target.Exists && !string.Equals(target.FullName, file.FullName, StringComparison.OrdinalIgnoreCase)) {
      this._controller.ViewModel.StatusMessage = $"\"{newName}\" already exists — pick a different name.";
      return null;
    }

    try {
      file.MoveTo(target.FullName);
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = $"Rename failed: {ex.Message}";
      return null;
    }

    // Mirror the rename in the grid so the UI doesn't point at a ghost path.
    if (this._currentFileItems is { } items) {
      foreach (var item in items) {
        if (item.FileInfo is { } fi && string.Equals(fi.FullName, file.FullName, StringComparison.OrdinalIgnoreCase)) {
          item.FileInfo = target;
          item.FileName = target.Name;
          break;
        }
      }
    }

    return target;
  }

  private async void OnApplyToSelectionClick(object? sender, RoutedEventArgs e) {
    if (this.FindControl<DataGrid>("FilesGrid") is not { } grid)
      return;

    var selected = grid.SelectedItems
      .OfType<FileItemModel>()
      .Where(f => f.FileInfo is { Exists: true })
      .ToList();

    if (selected.Count == 0) {
      this._controller.ViewModel.StatusMessage = "Select one or more files in the grid first.";
      return;
    }

    var edit = this.BuildEditFromUi();
    var errors = 0;
    var written = 0;
    foreach (var item in selected) {
      try {
        await this._metadataWriter.ApplyAsync(item.FileInfo!, edit);
        written++;
      } catch {
        errors++;
      }
    }

    this._controller.ViewModel.StatusMessage = errors == 0
      ? $"Applied edit to {written} file(s)."
      : $"Applied to {written} file(s); {errors} failed.";

    if (this._currentFile is { Exists: true } current) {
      this._currentMetadata = await this._metadataReader.ReadAsync(current);
      await this.UpdateMetadataAsync(current, CancellationToken.None);
    }
  }

  private MetadataEdit BuildEditFromUi() {
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    return new MetadataEdit {
      Gps            = ReadGpsFromEditor(inv),
      ImageDirection = this.ReadDirectionFromEditor(inv),
      TargetGps      = this.ReadTargetGpsFromEditor(inv),
      Location       = ReadTextFromEditor("LocationBox"),
      City           = ReadTextFromEditor("CityBox"),
      State          = ReadTextFromEditor("StateBox"),
      Country        = ReadTextFromEditor("CountryBox"),
      CountryCode    = ReadTextFromEditor("CountryCodeBox"),
      Rating         = ReadRatingFromEditor(),
      ColorLabel     = ReadLabelFromEditor(),
      Keywords       = ReadKeywordsFromEditor(),
      Title          = ReadTextFromEditor("TitleBox"),
      Caption        = ReadTextFromEditor("CaptionBox"),
      Creator        = ReadTextFromEditor("CreatorBox"),
      Copyright      = ReadTextFromEditor("CopyrightBox"),
      DateCreated    = this.ReadCaptureDateFromEditor()
    };
  }

  private Optional<DateTime?> ReadCaptureDateFromEditor() {
    var dateBox = this.FindControl<DatePicker>("CaptureDatePicker");
    var timeBox = this.FindControl<TimePicker>("CaptureTimePicker");
    var date = dateBox?.SelectedDate;
    var time = timeBox?.SelectedTime;

    // No date set → don't touch the field (leave as-is in the sidecar).
    if (date is null)
      return default;

    // Date without a time defaults to midnight — better than refusing to
    // write because the time picker is empty.
    var combined = date.Value.DateTime.Date + (time ?? TimeSpan.Zero);
    return Optional<DateTime?>.Set(DateTime.SpecifyKind(combined, DateTimeKind.Unspecified));
  }

  private Optional<GpsCoordinate?> ReadGpsFromEditor(System.Globalization.CultureInfo inv) {
    var latText = this.FindControl<TextBox>("GpsLatBox")?.Text ?? string.Empty;
    var lonText = this.FindControl<TextBox>("GpsLonBox")?.Text ?? string.Empty;
    var altText = this.FindControl<TextBox>("GpsAltBox")?.Text ?? string.Empty;

    var bothEmpty = string.IsNullOrWhiteSpace(latText) && string.IsNullOrWhiteSpace(lonText);
    if (bothEmpty)
      return Optional<GpsCoordinate?>.Set(null);

    if (!double.TryParse(latText, System.Globalization.NumberStyles.Float, inv, out var lat)
        || !double.TryParse(lonText, System.Globalization.NumberStyles.Float, inv, out var lon)) {
      // Invalid → don't touch GPS (leave field alone)
      return default;
    }

    double? alt = null;
    if (!string.IsNullOrWhiteSpace(altText)
        && double.TryParse(altText, System.Globalization.NumberStyles.Float, inv, out var a))
      alt = a;

    return Optional<GpsCoordinate?>.Set(new GpsCoordinate(lat, lon, alt));
  }

  private Optional<ImageDirection?> ReadDirectionFromEditor(System.Globalization.CultureInfo inv) {
    var text = this.FindControl<TextBox>("DirectionBox")?.Text ?? string.Empty;
    if (string.IsNullOrWhiteSpace(text))
      return Optional<ImageDirection?>.Set(null);

    if (!double.TryParse(text, System.Globalization.NumberStyles.Float, inv, out var degrees))
      return default;
    if (degrees is < 0 or > 360)
      return default;

    var magnetic = this.FindControl<CheckBox>("DirectionMagneticCheck")?.IsChecked == true;
    return new ImageDirection(degrees, magnetic ? DirectionReference.Magnetic : DirectionReference.True);
  }

  private Optional<GpsCoordinate?> ReadTargetGpsFromEditor(System.Globalization.CultureInfo inv) {
    var latText = this.FindControl<TextBox>("TargetLatBox")?.Text ?? string.Empty;
    var lonText = this.FindControl<TextBox>("TargetLonBox")?.Text ?? string.Empty;

    var bothEmpty = string.IsNullOrWhiteSpace(latText) && string.IsNullOrWhiteSpace(lonText);
    if (bothEmpty)
      return Optional<GpsCoordinate?>.Set(null);

    if (!double.TryParse(latText, System.Globalization.NumberStyles.Float, inv, out var lat)
        || !double.TryParse(lonText, System.Globalization.NumberStyles.Float, inv, out var lon))
      return default;

    return Optional<GpsCoordinate?>.Set(new GpsCoordinate(lat, lon));
  }

  private async void OnFillElevationClick(object? sender, RoutedEventArgs e) {
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var latText = this.FindControl<TextBox>("GpsLatBox")?.Text;
    var lonText = this.FindControl<TextBox>("GpsLonBox")?.Text;
    if (!double.TryParse(latText, System.Globalization.NumberStyles.Float, inv, out var lat)
        || !double.TryParse(lonText, System.Globalization.NumberStyles.Float, inv, out var lon)) {
      this._controller.ViewModel.StatusMessage = "Enter latitude + longitude before looking up altitude.";
      return;
    }

    this._controller.ViewModel.StatusMessage = "Looking up altitude...";
    try {
      using var elevation = new OpenTopoElevationService();
      var meters = await elevation.GetAltitudeMetersAsync(new GpsCoordinate(lat, lon));
      if (meters is null) {
        this._controller.ViewModel.StatusMessage = "No altitude returned for these coordinates.";
        return;
      }
      if (this.FindControl<TextBox>("GpsAltBox") is { } altBox)
        altBox.Text = meters.Value.ToString("0.#", inv);
      this._controller.ViewModel.StatusMessage = $"Altitude: {meters.Value:0.#} m. Click Save to commit.";
      this.RefreshMiniMap(forceCenter: true);
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = $"Altitude lookup failed: {ex.Message}";
    }
  }

  private async void OnResolveAddressClick(object? sender, RoutedEventArgs e) {
    if (this._currentFile is not { } file || !file.Exists || this._currentMetadata is not { Gps: { } gps })
      return;

    this._controller.ViewModel.StatusMessage = "Resolving address...";

    try {
      using var geocoder = new NominatimReverseGeocoder();
      var result = await geocoder.ResolveAsync(gps);
      if (result is not { HasAny: true }) {
        this._controller.ViewModel.StatusMessage = "No address found for these coordinates.";
        return;
      }

      // Only overwrite fields the user hasn't already filled.
      var edit = new MetadataEdit {
        Location    = PickResolveField(this._currentMetadata.Location,    result.Location),
        City        = PickResolveField(this._currentMetadata.City,        result.City),
        State       = PickResolveField(this._currentMetadata.State,       result.State),
        Country     = PickResolveField(this._currentMetadata.Country,     result.Country),
        CountryCode = PickResolveField(this._currentMetadata.CountryCode, result.CountryCode)
      };

      await this._metadataWriter.ApplyAsync(file, edit);
      await this.ReloadCurrentMetadataAsync(file);

      this._controller.ViewModel.StatusMessage =
        $"Resolved: {string.Join(", ", new[] { result.Location, result.City, result.State, result.Country }.Where(s => !string.IsNullOrEmpty(s)))}";
      this.RefreshMiniMap(forceCenter: true);
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = $"Resolve failed: {ex.Message}";
    }
  }

  private static Optional<string?> PickResolveField(string? existing, string? resolved) {
    if (string.IsNullOrEmpty(resolved))
      return default;
    if (!string.IsNullOrEmpty(existing))
      return default;
    return Optional<string?>.Set(resolved);
  }

  private Optional<int?> ReadRatingFromEditor() {
    if (this.FindControl<ComboBox>("RatingCombo")?.SelectedItem is int r)
      return r;
    return Optional<int?>.Set(null);
  }

  private Optional<string?> ReadLabelFromEditor() {
    var selected = this.FindControl<ComboBox>("LabelCombo")?.SelectedItem as string;
    if (string.IsNullOrEmpty(selected) || selected == "—")
      return Optional<string?>.Set(null);
    return selected;
  }

  private Optional<IReadOnlyList<string>> ReadKeywordsFromEditor() {
    var text = this.FindControl<TextBox>("KeywordsBox")?.Text ?? string.Empty;
    var parts = text
      .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    return Optional<IReadOnlyList<string>>.Set(parts);
  }

  private Optional<string?> ReadTextFromEditor(string controlName) {
    var text = this.FindControl<TextBox>(controlName)?.Text;
    if (string.IsNullOrWhiteSpace(text))
      return Optional<string?>.Set(null);
    return text;
  }

  private void OnPreviewDoubleTapped(object? sender, TappedEventArgs e) {
    var grid = this.FindControl<DataGrid>("FilesGrid");
    if (grid?.SelectedItem is not FileItemModel { FileInfo.Exists: true } fileItem)
      return;

    try {
      ShellLauncher.OpenInDefaultViewer(fileItem.FileInfo!.FullName);
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = string.Format(Strings.Error_CouldNotOpenImage, ex.Message);
    }
  }
}
