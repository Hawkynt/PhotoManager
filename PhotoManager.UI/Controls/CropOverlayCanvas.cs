using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Hawkynt.PhotoManager.UI.Controls;

/// <summary>
/// Lightroom-style crop overlay drawn on top of the develop preview.
/// Renders a dimmed mask outside the current crop rectangle, the four
/// corner / edge handles, and translates pointer drags into normalised
/// (0..1) crop edge updates. Geometry events fire while dragging so the
/// host can re-render the preview live.
///
/// Coordinates mirror <see cref="MaskOverlayCanvas"/>: the same
/// Stretch=Uniform / DownOnly logic the underlying preview <see cref="Image"/>
/// uses, taken from <see cref="ImageWidth"/> / <see cref="ImageHeight"/>.
/// </summary>
public sealed class CropOverlayCanvas : Control {
  private const double HandleHitRadius = 14;
  private const double HandleVisualRadius = 5;
  private const double MinSeparation = 0.02;

  /// <summary>Source image dimensions in pixels — used to compute the displayed rect.</summary>
  public int ImageWidth { get; set; }
  public int ImageHeight { get; set; }

  public double CropLeft { get; private set; }
  public double CropTop { get; private set; }
  public double CropRight { get; private set; } = 1;
  public double CropBottom { get; private set; } = 1;

  /// <summary>Fires while the user is dragging a handle so the host can re-render live.</summary>
  public event EventHandler? CropChanged;

  private int _draggingHandle = -1;

  public void SetCrop(double left, double top, double right, double bottom) {
    this.CropLeft   = Math.Clamp(left, 0, 1);
    this.CropTop    = Math.Clamp(top, 0, 1);
    this.CropRight  = Math.Clamp(right, 0, 1);
    this.CropBottom = Math.Clamp(bottom, 0, 1);
    this.InvalidateVisual();
  }

  public override void Render(DrawingContext context) {
    base.Render(context);
    // Transparent fill so pointer events fire across the full bounds.
    context.FillRectangle(Brushes.Transparent, new Rect(this.Bounds.Size));

    var rect = this.ComputeImageRect();
    if (rect.Width <= 1 || rect.Height <= 1)
      return;

    var crop = this.CurrentCropRect(rect);
    if (crop.Width <= 0 || crop.Height <= 0)
      return;

    var dimBrush = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));
    // Four dim quads outside the crop: top, bottom, left, right strips.
    context.FillRectangle(dimBrush, new Rect(rect.X, rect.Y, rect.Width, crop.Y - rect.Y));
    context.FillRectangle(dimBrush, new Rect(rect.X, crop.Bottom, rect.Width, rect.Bottom - crop.Bottom));
    context.FillRectangle(dimBrush, new Rect(rect.X, crop.Y, crop.X - rect.X, crop.Height));
    context.FillRectangle(dimBrush, new Rect(crop.Right, crop.Y, rect.Right - crop.Right, crop.Height));

    var pen = new Pen(new SolidColorBrush(Colors.Yellow), 2);
    var dimPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), 3);
    context.DrawRectangle(null, dimPen, crop);
    context.DrawRectangle(null, pen, crop);

    // Rule-of-thirds guides while the user is composing.
    var guidePen = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1);
    var thirdW = crop.Width / 3.0;
    var thirdH = crop.Height / 3.0;
    context.DrawLine(guidePen, new Point(crop.X + thirdW, crop.Y),       new Point(crop.X + thirdW, crop.Bottom));
    context.DrawLine(guidePen, new Point(crop.X + thirdW * 2, crop.Y),   new Point(crop.X + thirdW * 2, crop.Bottom));
    context.DrawLine(guidePen, new Point(crop.X, crop.Y + thirdH),       new Point(crop.Right, crop.Y + thirdH));
    context.DrawLine(guidePen, new Point(crop.X, crop.Y + thirdH * 2),   new Point(crop.Right, crop.Y + thirdH * 2));

    var fill = new SolidColorBrush(Colors.Yellow);
    var dotEdge = new Pen(new SolidColorBrush(Colors.Black), 1);
    foreach (var handle in EnumerateHandlePoints(crop))
      DrawHandle(context, handle, fill, dotEdge);
  }

  protected override void OnPointerPressed(PointerPressedEventArgs e) {
    base.OnPointerPressed(e);
    var pos = e.GetPosition(this);
    var rect = this.ComputeImageRect();
    if (rect.Width <= 1 || rect.Height <= 1)
      return;
    var crop = this.CurrentCropRect(rect);
    var hit = HitTestHandle(pos, crop);
    if (hit < 0)
      return;
    this._draggingHandle = hit;
    e.Pointer.Capture(this);
    e.Handled = true;
  }

  protected override void OnPointerMoved(PointerEventArgs e) {
    base.OnPointerMoved(e);
    if (this._draggingHandle < 0)
      return;
    var rect = this.ComputeImageRect();
    if (rect.Width <= 1 || rect.Height <= 1)
      return;
    var pos = e.GetPosition(this);
    var (nx, ny) = this.CanvasToNorm(pos, rect);
    this.UpdateHandle(this._draggingHandle, nx, ny);
    this.CropChanged?.Invoke(this, EventArgs.Empty);
    this.InvalidateVisual();
  }

  protected override void OnPointerReleased(PointerReleasedEventArgs e) {
    base.OnPointerReleased(e);
    if (this._draggingHandle < 0)
      return;
    this._draggingHandle = -1;
    e.Pointer.Capture(null);
    e.Handled = true;
  }

  private void UpdateHandle(int handle, double nx, double ny) {
    // Layout: 0..3 corners (TL, TR, BL, BR), 4..7 edges (T, R, B, L).
    switch (handle) {
      case 0: this.CropLeft   = Math.Min(nx, this.CropRight  - MinSeparation); this.CropTop    = Math.Min(ny, this.CropBottom - MinSeparation); break;
      case 1: this.CropRight  = Math.Max(nx, this.CropLeft   + MinSeparation); this.CropTop    = Math.Min(ny, this.CropBottom - MinSeparation); break;
      case 2: this.CropLeft   = Math.Min(nx, this.CropRight  - MinSeparation); this.CropBottom = Math.Max(ny, this.CropTop    + MinSeparation); break;
      case 3: this.CropRight  = Math.Max(nx, this.CropLeft   + MinSeparation); this.CropBottom = Math.Max(ny, this.CropTop    + MinSeparation); break;
      case 4: this.CropTop    = Math.Min(ny, this.CropBottom - MinSeparation); break;
      case 5: this.CropRight  = Math.Max(nx, this.CropLeft   + MinSeparation); break;
      case 6: this.CropBottom = Math.Max(ny, this.CropTop    + MinSeparation); break;
      case 7: this.CropLeft   = Math.Min(nx, this.CropRight  - MinSeparation); break;
    }
    this.CropLeft   = Math.Clamp(this.CropLeft,   0, 1);
    this.CropTop    = Math.Clamp(this.CropTop,    0, 1);
    this.CropRight  = Math.Clamp(this.CropRight,  0, 1);
    this.CropBottom = Math.Clamp(this.CropBottom, 0, 1);
  }

  private Rect CurrentCropRect(Rect imageRect)
    => new(
      imageRect.X + this.CropLeft   * imageRect.Width,
      imageRect.Y + this.CropTop    * imageRect.Height,
      Math.Max(0, (this.CropRight  - this.CropLeft) * imageRect.Width),
      Math.Max(0, (this.CropBottom - this.CropTop)  * imageRect.Height));

  private static IEnumerable<Point> EnumerateHandlePoints(Rect crop) {
    yield return new Point(crop.X,       crop.Y);
    yield return new Point(crop.Right,   crop.Y);
    yield return new Point(crop.X,       crop.Bottom);
    yield return new Point(crop.Right,   crop.Bottom);
    yield return new Point(crop.Center.X, crop.Y);
    yield return new Point(crop.Right,    crop.Center.Y);
    yield return new Point(crop.Center.X, crop.Bottom);
    yield return new Point(crop.X,        crop.Center.Y);
  }

  private static int HitTestHandle(Point pos, Rect crop) {
    var i = 0;
    foreach (var handle in EnumerateHandlePoints(crop)) {
      if (Distance(pos, handle) <= HandleHitRadius)
        return i;
      i++;
    }
    return -1;
  }

  private static double Distance(Point a, Point b) {
    var dx = a.X - b.X;
    var dy = a.Y - b.Y;
    return Math.Sqrt(dx * dx + dy * dy);
  }

  private static void DrawHandle(DrawingContext context, Point centre, IBrush fill, IPen edge)
    => context.DrawEllipse(fill, edge, centre, HandleVisualRadius, HandleVisualRadius);

  private (double nx, double ny) CanvasToNorm(Point p, Rect rect) {
    var nx = (p.X - rect.X) / rect.Width;
    var ny = (p.Y - rect.Y) / rect.Height;
    return (Math.Clamp(nx, 0, 1), Math.Clamp(ny, 0, 1));
  }

  private Rect ComputeImageRect() {
    if (this.ImageWidth <= 0 || this.ImageHeight <= 0)
      return new Rect(0, 0, this.Bounds.Width, this.Bounds.Height);
    var scale = Math.Min(this.Bounds.Width / this.ImageWidth, this.Bounds.Height / this.ImageHeight);
    if (scale > 1) scale = 1;
    var w = this.ImageWidth * scale;
    var h = this.ImageHeight * scale;
    var x = (this.Bounds.Width - w) * 0.5;
    var y = (this.Bounds.Height - h) * 0.5;
    return new Rect(x, y, w, h);
  }
}
