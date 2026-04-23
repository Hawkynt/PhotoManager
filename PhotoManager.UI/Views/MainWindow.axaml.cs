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

    // Redraw the region overlay whenever either the Image's OR the Canvas's
    // rendered bounds change. Both are siblings in the same Grid cell so
    // they typically resize together, but subscribing to both makes the
    // overlay immune to layout races during splitter drags.
    var redrawOverlay = new Avalonia.Reactive.AnonymousObserver<Avalonia.Rect>(_ => {
      if (this._currentMetadata is { } md)
        this.RenderRegionOverlay(md);
    });

    if (this.FindControl<Image>("PreviewImage") is { } preview) {
      preview.GetObservable(Avalonia.Layout.Layoutable.BoundsProperty).Subscribe(redrawOverlay);
    }

    // Hook up click-drag on the overlay canvas for manual region creation.
    // The canvas is always hit-testable now so rectangles can receive clicks
    // (used for the "click box → scroll thumbnail bar" behavior); draw-box
    // mode is handled by checking _isDrawingRegion inside the handler.
    if (this.FindControl<Canvas>("RegionOverlay") is { } overlay) {
      overlay.IsHitTestVisible = true;
      overlay.PointerPressed += this.OnOverlayPointerPressed;
      overlay.PointerMoved += this.OnOverlayPointerMoved;
      overlay.PointerReleased += this.OnOverlayPointerReleased;
      overlay.GetObservable(Avalonia.Layout.Layoutable.BoundsProperty).Subscribe(redrawOverlay);
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

  private async void OnWindowOpened(object? sender, EventArgs e)
    => await this._controller.LoadSettingsAsync();

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

  private async void OnScanClick(object? sender, RoutedEventArgs e) {
    var grid = this.FindControl<DataGrid>("FilesGrid");
    if (grid != null)
      grid.ItemsSource = null;

    var items = await this._controller.ScanCheckedSourcesAsync();
    this._currentFileItems = items;

    if (grid != null)
      grid.ItemsSource = items;
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
    var seedFolder = this.SeedFolderFromTree();
    var window = seedFolder == null ? new FaceGalleryWindow() : new FaceGalleryWindow(seedFolder);
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
    if (sender is not DataGrid grid || grid.SelectedItem is not FileItemModel { FileInfo.Exists: true } fileItem)
      return;

    this._previewCts?.Cancel();
    this._previewCts = new CancellationTokenSource();
    var token = this._previewCts.Token;

    await this.UpdatePreviewAsync(fileItem.FileInfo!, token);
    if (token.IsCancellationRequested)
      return;

    await this.UpdateMetadataAsync(fileItem.FileInfo!, token);
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
    var list = this.FindControl<ListBox>("MetadataList");
    if (list == null)
      return;

    var fileToImport = new FileToImport(file);

    DateTime? exifOriginal = null;
    DateTime? exifModified = null;
    DateTime? gps = null;
    DateTime? filename = null;

    try {
      await foreach (var d in fileToImport.GetExifSubIfdDateAsync()) { exifOriginal = d; break; }
      await foreach (var d in fileToImport.GetExifIfd0DateAsync())   { exifModified = d; break; }
      await foreach (var d in fileToImport.GetGpsDateAsync())        { gps = d; break; }

      var parser = new DateTimeParser();
      var settings = new ImportSettings();
      await foreach (var d in parser.ParseDateFromFileName(fileToImport, settings)) { filename = d; break; }
    } catch {
      // Ignore metadata extraction errors — the grid just shows "Not found"
    }

    if (token.IsCancellationRequested)
      return;

    var (_, winning) = await this._controller.GetMostLogicalDateWithSourceAsync(fileToImport);
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

    var rows = new List<MetadataRow> {
      Row("File Name",     file.Name, DateTimeSource.Unknown, winning),
      Row("File Path",     file.FullName, DateTimeSource.Unknown, winning),
      Row("File Size",     FormatFileSize(file.Length), DateTimeSource.Unknown, winning),
      Row("Date Created",  file.CreationTime.ToString(UiDateFormat), DateTimeSource.FileCreatedAt, winning),
      Row("Date Modified", file.LastWriteTime.ToString(UiDateFormat), DateTimeSource.FileModifiedAt, winning),
      Row("EXIF Original", exifOriginal?.ToString(UiDateFormat) ?? Strings.Metadata_NotFound, DateTimeSource.ExifSubIfd, winning),
      Row("EXIF Modified", exifModified?.ToString(UiDateFormat) ?? Strings.Metadata_NotFound, DateTimeSource.ExifIfd0, winning),
      Row("GPS Date",      gps?.ToString(UiDateFormat) ?? Strings.Metadata_NotFound, DateTimeSource.Gps, winning),
      Row("Filename Date", filename?.ToString(UiDateFormat) ?? Strings.Metadata_NotDetected, DateTimeSource.FileName, winning),
      new MetadataRow("GPS",         FormatGpsCoordinate(md.Gps),       false, false),
      new MetadataRow("Rating",      md.Rating?.ToString() ?? "—",      false, false),
      new MetadataRow("Color Label", md.ColorLabel ?? "—",              false, false),
      new MetadataRow("Keywords",    md.Keywords.Count == 0 ? "—" : string.Join(", ", md.Keywords), false, false),
      new MetadataRow("Title",       md.Title ?? "—",                   false, false),
      new MetadataRow("Caption",     md.Caption ?? "—",                 false, false),
    };

    list.ItemsSource = rows;
    this.ApplyMetadataTinting(list, rows);
    this.SyncEditorFromMetadata(md);
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

    canvas.Children.Clear();
    if (!this.TryGetImageRenderBounds(out var offsetX, out var offsetY, out var renderedW, out var renderedH))
      return;

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
    // This takes precedence over draw-mode — otherwise the user couldn't
    // click an existing box while drawing new ones.
    if (e.Source is Control { Tag: int regionIndex }) {
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

    var region = new TaggedRegion(
      new NormalizedBoundingBox(nx, ny, nw, nh),
      RegionCategory.Other,
      Label: null,
      Status: RegionStatus.Proposed,
      Source: TaggedRegion.ManualSource
    );

    try {
      await this._regionService.AppendAsync(file, new[] { region });
      await this.ReloadCurrentMetadataAsync(file);
    } catch (Exception ex) {
      this._controller.ViewModel.StatusMessage = $"Draw box failed: {ex.Message}";
    }

    // Leave draw mode off after each box so the user isn't stuck capturing
    // every mouse click — they can toggle back on for the next one.
    if (this.FindControl<ToggleButton>("DrawRegionToggle") is { } toggle)
      toggle.IsChecked = false;
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
      var updated = md.Regions.Select((r, i) =>
        i == index
          ? r with { Label = result.Name, Status = RegionStatus.Accepted }
          : r
      ).ToArray();

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

  private static string FormatGpsCoordinate(GpsCoordinate? gps) {
    if (gps is not { } g)
      return "—";

    var inv = System.Globalization.CultureInfo.InvariantCulture;
    return g.AltitudeMeters is { } alt
      ? string.Create(inv, $"{g.Latitude:0.######}, {g.Longitude:0.######}, {alt:0.##}m")
      : string.Create(inv, $"{g.Latitude:0.######}, {g.Longitude:0.######}");
  }

  private void ApplyMetadataTinting(ListBox list, IReadOnlyList<MetadataRow> rows) {
    list.ContainerPrepared -= this.OnMetadataContainerPrepared;
    list.ContainerPrepared += this.OnMetadataContainerPrepared;

    for (var i = 0; i < list.ItemCount; i++) {
      if (list.ContainerFromIndex(i) is ListBoxItem item)
        ApplyRowClasses(item, rows[i]);
    }
  }

  private void OnMetadataContainerPrepared(object? sender, ContainerPreparedEventArgs e) {
    if (e.Container is ListBoxItem item && item.DataContext is MetadataRow row)
      ApplyRowClasses(item, row);
  }

  private static void ApplyRowClasses(ListBoxItem item, MetadataRow row) {
    item.Classes.Remove("winner");
    item.Classes.Remove("missing");
    if (row.IsWinner)
      item.Classes.Add("winner");
    else if (row.IsMissing)
      item.Classes.Add("missing");
  }

  private static MetadataRow Row(string name, string value, DateTimeSource source, DateTimeSource winning) {
    var missing = value == Strings.Metadata_NotFound || value == Strings.Metadata_NotDetected;
    var isWinner = source != DateTimeSource.Unknown && source == winning;
    return new MetadataRow(name, value, isWinner, missing);
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

  private void SyncEditorFromMetadata(FullMetadata md) {
    var inv = System.Globalization.CultureInfo.InvariantCulture;

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
  }

  private void OnRevertMetadataClick(object? sender, RoutedEventArgs e) {
    if (this._currentMetadata is { } md)
      this.SyncEditorFromMetadata(md);
  }

  private async void OnPickOnMapClick(object? sender, RoutedEventArgs e) {
    var picker = new MapPickerWindow(
      this._currentMetadata?.Gps,
      this._currentMetadata?.TargetGps,
      initialDirectionDegrees: this._currentMetadata?.ImageDirection?.Degrees);
    var result = await picker.ShowDialog<MapPickerWindow.Result?>(this);
    if (result is null)
      return;

    var inv = System.Globalization.CultureInfo.InvariantCulture;
    if (this.FindControl<TextBox>("GpsLatBox") is { } lat)
      lat.Text = result.Camera.Latitude.ToString("0.######", inv);
    if (this.FindControl<TextBox>("GpsLonBox") is { } lon)
      lon.Text = result.Camera.Longitude.ToString("0.######", inv);
    if (result.Camera.AltitudeMeters is { } alt && this.FindControl<TextBox>("GpsAltBox") is { } altBox)
      altBox.Text = alt.ToString("0.##", inv);

    if (result.Target is { } target) {
      if (this.FindControl<TextBox>("TargetLatBox") is { } tgtLat)
        tgtLat.Text = target.Latitude.ToString("0.######", inv);
      if (this.FindControl<TextBox>("TargetLonBox") is { } tgtLon)
        tgtLon.Text = target.Longitude.ToString("0.######", inv);
    }

    if (result.BearingDegrees is { } bearing && this.FindControl<TextBox>("DirectionBox") is { } dirBox)
      dirBox.Text = bearing.ToString("0.##", inv);
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
      Caption        = ReadTextFromEditor("CaptionBox")
    };
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
