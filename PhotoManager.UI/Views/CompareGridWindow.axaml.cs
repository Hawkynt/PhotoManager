using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PhotoManager.Core.Metadata;

namespace PhotoManager.UI.Views;

/// <summary>
/// Compare Grid N-pane view: displays 2-9 photos simultaneously in a
/// uniform grid with synchronized zoom/pan so the user can pick the
/// sharpest / best-composed shot from a set of similar photos.
/// </summary>
public partial class CompareGridWindow : Window {
  private readonly IReadOnlyList<FileInfo> _photos;
  private readonly IMetadataReader _metadataReader = new MetadataReader();

  // Synchronized zoom + pan state — mirrors RestoreWindow's pattern.
  private double _zoom = 1.0;
  private double _panX;
  private double _panY;
  private bool _isPanning;
  private Point _panStart;

  // Selection state.
  private int _selectedIndex = -1;
  private readonly List<Border> _cellBorders = new();
  private readonly List<Image> _cellImages = new();

  /// <summary>
  /// The index of the photo the user picked (0-based), or -1 if none.
  /// Set when the user clicks "Pick selected".
  /// </summary>
  public int PickedIndex { get; private set; } = -1;

  /// <summary>
  /// The <see cref="FileInfo"/> the user picked, or null if they closed
  /// without picking.
  /// </summary>
  public FileInfo? PickedFile => this.PickedIndex >= 0 && this.PickedIndex < this._photos.Count
    ? this._photos[this.PickedIndex]
    : null;

  public CompareGridWindow() : this(Array.Empty<FileInfo>()) { }

  public CompareGridWindow(IReadOnlyList<FileInfo> photos) {
    this._photos = photos;
    this.InitializeComponent();
    this.Title = $"🔲 Compare Grid — {photos.Count} photos";
    this.Opened += async (_, _) => await this.LoadAsync();
  }

  private async Task LoadAsync() {
    if (this._photos.Count < 2) {
      this.SetStatus("Need at least 2 photos to compare.");
      return;
    }

    var (cols, rows) = CompareGridLayout.ComputeGridSize(this._photos.Count);
    var grid = this.FindControl<Grid>("PhotoGrid")!;

    // Build row and column definitions.
    grid.RowDefinitions.Clear();
    grid.ColumnDefinitions.Clear();
    for (var r = 0; r < rows; r++)
      grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
    for (var c = 0; c < cols; c++)
      grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

    // Load each photo and create cells.
    for (var i = 0; i < this._photos.Count; i++) {
      var file = this._photos[i];
      var col = i % cols;
      var row = i / cols;

      var image = new Image {
        Stretch = Stretch.Uniform,
        RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute),
      };
      image.PointerWheelChanged += this.OnCellWheel;
      image.PointerPressed += this.OnCellPointerPressed;
      image.PointerMoved += this.OnCellPointerMoved;
      image.PointerReleased += this.OnCellPointerReleased;
      image.DoubleTapped += this.OnCellDoubleTap;

      // Star rating overlay in the top-left corner of each cell.
      var starLabel = new TextBlock {
        FontSize = 14,
        Foreground = new SolidColorBrush(Avalonia.Media.Color.FromRgb(184, 134, 11)), // dark goldenrod
        Margin = new Thickness(6, 4, 0, 0),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        IsHitTestVisible = false,
      };

      // File name label at the bottom of each cell.
      var nameLabel = new TextBlock {
        Text = file.Name,
        FontSize = 11,
        Opacity = 0.8,
        Margin = new Thickness(6, 0, 6, 4),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
        IsHitTestVisible = false,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(160, 0, 0, 0)),
        Foreground = Brushes.White,
        Padding = new Thickness(4, 2),
      };

      var innerGrid = new Grid();
      innerGrid.Children.Add(image);
      innerGrid.Children.Add(starLabel);
      innerGrid.Children.Add(nameLabel);

      var border = new Border {
        BorderThickness = new Thickness(3),
        BorderBrush = Brushes.Transparent,
        Margin = new Thickness(2),
        ClipToBounds = true,
        Child = innerGrid,
        Tag = i, // store index for click identification
      };

      Grid.SetRow(border, row);
      Grid.SetColumn(border, col);
      grid.Children.Add(border);

      this._cellBorders.Add(border);
      this._cellImages.Add(image);

      // Load bitmap asynchronously.
      var capturedImage = image;
      var capturedStar = starLabel;
      var capturedFile = file;
      _ = Task.Run(async () => {
        try {
          var bmp = await LoadPreviewBitmapAsync(capturedFile);
          var rating = await this.ReadRatingAsync(capturedFile);
          Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            capturedImage.Source = bmp;
            if (rating is > 0)
              capturedStar.Text = new string('★', rating.Value); // filled stars
          });
        } catch {
          // Silently skip unreadable files — the cell stays empty.
        }
      });
    }

    this.SetStatus($"Loaded {this._photos.Count} photos. Click a photo to select it as the pick. Wheel = zoom, right-drag = pan, double-click = reset.");
  }

  /// <summary>
  /// Load a photo at preview resolution as an Avalonia <see cref="Bitmap"/>.
  /// Uses the built-in Avalonia decoder which handles JPEG, PNG, BMP, TIFF, etc.
  /// </summary>
  private static async Task<Bitmap?> LoadPreviewBitmapAsync(FileInfo file) {
    return await Task.Run(() => {
      using var stream = file.OpenRead();
      var bmp = new Bitmap(stream);
      // Avalonia's Bitmap doesn't expose pixel-level resize, but we can
      // use DecodeToWidth/DecodeToHeight at load time. Since we already
      // have the full bitmap, we'll just use it. The OS / Avalonia will
      // scale it down for display in the Uniform-stretch Image control.
      return bmp;
    });
  }

  private async Task<int?> ReadRatingAsync(FileInfo file) {
    try {
      var meta = await this._metadataReader.ReadAsync(file);
      return meta.Rating;
    } catch {
      return null;
    }
  }

  // ---------- Selection ----------

  private void SelectCell(int index) {
    if (index < 0 || index >= this._cellBorders.Count)
      return;

    // Deselect previous.
    if (this._selectedIndex >= 0 && this._selectedIndex < this._cellBorders.Count)
      this._cellBorders[this._selectedIndex].BorderBrush = Brushes.Transparent;

    this._selectedIndex = index;
    this._cellBorders[index].BorderBrush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(46, 204, 113)); // green
    this._cellBorders[index].BorderThickness = new Thickness(3);

    if (this.FindControl<Button>("PickButton") is { } btn)
      btn.IsEnabled = true;

    this.SetStatus($"Selected: {this._photos[index].Name}");
  }

  // ---------- Synchronized zoom + pan ----------

  private void OnCellWheel(object? sender, PointerWheelEventArgs e) {
    if (sender is not Visual visual)
      return;
    var pos = e.GetPosition(VisualParentOrSelf(visual));
    var oldZoom = this._zoom;
    var step = e.Delta.Y > 0 ? 1.2 : 1.0 / 1.2;
    var newZoom = Math.Clamp(oldZoom * step, 0.1, 32.0);
    if (Math.Abs(newZoom - oldZoom) < 1e-6)
      return;

    this._panX = pos.X - (pos.X - this._panX) * (newZoom / oldZoom);
    this._panY = pos.Y - (pos.Y - this._panY) * (newZoom / oldZoom);
    this._zoom = newZoom;
    this.ApplyTransform();
    e.Handled = true;
  }

  private void OnCellPointerPressed(object? sender, PointerPressedEventArgs e) {
    if (sender is not Image image)
      return;
    var props = e.GetCurrentPoint(image).Properties;

    // Left-click: select cell.
    if (props.IsLeftButtonPressed) {
      // Walk up to the Border parent to find the cell index.
      var border = image.GetVisualParent()?.GetVisualParent() as Border;
      if (border?.Tag is int idx)
        this.SelectCell(idx);
      e.Handled = true;
      return;
    }

    // Right-click: start pan.
    if (props.IsRightButtonPressed) {
      this._isPanning = true;
      this._panStart = e.GetPosition(VisualParentOrSelf(image));
      e.Pointer.Capture(image);
      e.Handled = true;
    }
  }

  private void OnCellPointerMoved(object? sender, PointerEventArgs e) {
    if (!this._isPanning || sender is not Visual visual)
      return;
    var pos = e.GetPosition(VisualParentOrSelf(visual));
    this._panX += pos.X - this._panStart.X;
    this._panY += pos.Y - this._panStart.Y;
    this._panStart = pos;
    this.ApplyTransform();
    e.Handled = true;
  }

  private void OnCellPointerReleased(object? sender, PointerReleasedEventArgs e) {
    if (!this._isPanning)
      return;
    this._isPanning = false;
    e.Pointer.Capture(null);
    e.Handled = true;
  }

  private void OnCellDoubleTap(object? sender, TappedEventArgs e) {
    this._zoom = 1.0;
    this._panX = 0;
    this._panY = 0;
    this.ApplyTransform();
    e.Handled = true;
  }

  /// <summary>
  /// Apply the same scale + translate transform to ALL image cells so
  /// they pan/zoom in lock-step — identical pattern to RestoreWindow.
  /// </summary>
  private void ApplyTransform() {
    var transform = new TransformGroup();
    transform.Children.Add(new ScaleTransform(this._zoom, this._zoom));
    transform.Children.Add(new TranslateTransform(this._panX, this._panY));

    foreach (var image in this._cellImages)
      image.RenderTransform = transform;
  }

  private static Visual VisualParentOrSelf(Visual visual)
    => visual.GetVisualParent() as Visual ?? visual;

  // ---------- Bottom bar ----------

  private void OnPickClick(object? sender, RoutedEventArgs e) {
    if (this._selectedIndex < 0 || this._selectedIndex >= this._photos.Count) {
      this.SetStatus("Select a photo first.");
      return;
    }
    this.PickedIndex = this._selectedIndex;
    this.SetStatus($"Picked: {this._photos[this._selectedIndex].Name}");
    this.Close();
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) {
    this.Close();
  }

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = message;
  }
}
