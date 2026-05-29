using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PhotoManager.Core.Develop;

namespace PhotoManager.UI.Controls;

/// <summary>
/// Transparent control overlaid on the develop preview while a local
/// adjustment is selected. Draws the active mask (linear gradient line +
/// 2 endpoint dots, radial gradient ellipse + centre / corner dots, or
/// brush dabs as faint discs), and translates pointer drags into mask
/// geometry updates / new brush dabs.
///
/// The host (<c>EditImageWindow</c>) is responsible for showing / hiding
/// us via <c>IsVisible</c> + <c>IsHitTestVisible</c> so the eyedropper
/// flow on the underlying image isn't intercepted when no mask is active.
///
/// Coordinates are normalised image-space (0..1). Conversion uses the
/// same Stretch=Uniform / DownOnly logic the preview <see cref="Image"/>
/// applies, taken from <see cref="ImageWidth"/> / <see cref="ImageHeight"/>.
/// </summary>
public sealed class MaskOverlayCanvas : Control {
  private const double HandleHitRadius = 14;
  private const double HandleVisualRadius = 5;

  /// <summary>Active adjustment to render / edit. Null hides the control's drawing.</summary>
  public LocalAdjustment? ActiveAdjustment { get; private set; }

  /// <summary>Source image dimensions in pixels — used to compute the displayed rect.</summary>
  public int ImageWidth { get; set; }
  public int ImageHeight { get; set; }

  /// <summary>True while the user is in brush-paint mode — pointer drag adds dabs.</summary>
  public bool BrushMode { get; set; }
  public double BrushRadius { get; set; } = 0.05;
  public double BrushFlow { get; set; } = 1.0;
  public bool EraseMode { get; set; }

  /// <summary>Fired when the user finishes dragging a handle / completes a stroke.</summary>
  public event EventHandler<LocalMask>? MaskChanged;
  /// <summary>Live event during a paint stroke — the host should append dab to the active mask.</summary>
  public event EventHandler<BrushDab>? BrushDabAdded;
  /// <summary>Fired when the scroll wheel resizes the brush — the host should update its brush-size slider.</summary>
  public event EventHandler<double>? BrushRadiusChanged;

  private int _draggingHandle = -1;
  private bool _painting;
  /// <summary>Last pointer position over the canvas, used to render the brush-cursor preview.</summary>
  private Point? _hoverPos;

  public void SetActiveAdjustment(LocalAdjustment? adjustment) {
    this.ActiveAdjustment = adjustment;
    this.InvalidateVisual();
  }

  public override void Render(DrawingContext context) {
    base.Render(context);
    // Avalonia's Control base type isn't hit-test-visible unless something
    // is drawn over its bounds. Fill a transparent rect so PointerMoved /
    // PointerWheelChanged actually fire when the pointer is over us.
    context.FillRectangle(Brushes.Transparent, new Rect(this.Bounds.Size));

    if (this.ActiveAdjustment is null)
      return;
    var rect = this.ComputeImageRect();
    if (rect.Width <= 1 || rect.Height <= 1)
      return;

    var pen = new Pen(new SolidColorBrush(Colors.Yellow), 2);
    var dimPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), 3);
    var fill = new SolidColorBrush(Colors.Yellow);
    var dotEdge = new Pen(new SolidColorBrush(Colors.Black), 1);

    var mask = this.ActiveAdjustment.Mask;
    switch (mask.Type) {
      case LocalMaskType.Linear: {
        var p0 = this.NormToCanvas(mask.X0, mask.Y0, rect);
        var p1 = this.NormToCanvas(mask.X1, mask.Y1, rect);
        // Two-pass line: dark outline + yellow on top, so it stays
        // readable on bright + dark backgrounds.
        context.DrawLine(dimPen, p0, p1);
        context.DrawLine(pen, p0, p1);
        DrawHandle(context, p0, fill, dotEdge);
        DrawHandle(context, p1, fill, dotEdge);
        break;
      }
      case LocalMaskType.Radial: {
        var c = this.NormToCanvas(mask.CenterX, mask.CenterY, rect);
        var rx = mask.RadiusX * rect.Width;
        var ry = mask.RadiusY * rect.Height;
        context.DrawEllipse(null, dimPen, c, rx, ry);
        context.DrawEllipse(null, pen, c, rx, ry);
        DrawHandle(context, c, fill, dotEdge);
        DrawHandle(context, new Point(c.X + rx, c.Y), fill, dotEdge);
        DrawHandle(context, new Point(c.X, c.Y + ry), fill, dotEdge);
        break;
      }
      case LocalMaskType.Brush:
      case LocalMaskType.Inpaint: {
        // Dab overlays are an editing aid — only show while the pointer is
        // over the image. Hovering off lets the user see the actual develop
        // result underneath, unobscured by yellow circles.
        if (this._hoverPos.HasValue && mask.BrushDabs is { } dabs) {
          var isInpaint = mask.Type == LocalMaskType.Inpaint;
          var paintFill  = new SolidColorBrush(isInpaint
            ? Color.FromArgb(70, 240, 60, 60)  // reddish tint for inpaint mask
            : Color.FromArgb(70, 240, 220, 60));
          var eraserPen  = new Pen(new SolidColorBrush(Color.FromArgb(140, 240, 80, 80)), 1, DashStyle.Dash);
          foreach (var dab in dabs) {
            var p = this.NormToCanvas(dab.X, dab.Y, rect);
            var r = dab.Radius * rect.Width;
            if (dab.Flow >= 0)
              context.DrawEllipse(paintFill, null, p, r, r);
            else
              context.DrawEllipse(null, eraserPen, p, r, r);
          }
        }
        break;
      }
    }

    // Brush cursor — only when in paint mode and the pointer's over us.
    // Two pens so the circle stays visible on bright + dark backgrounds.
    if (this.BrushMode && this._hoverPos is { } hover) {
      var brushDispRadius = this.BrushRadius * rect.Width;
      var cursorOuter = new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), 2);
      var cursorInner = new Pen(new SolidColorBrush(this.EraseMode ? Color.FromArgb(220, 240, 80, 80) : Colors.White), 1.2);
      context.DrawEllipse(null, cursorOuter, hover, brushDispRadius + 0.5, brushDispRadius + 0.5);
      context.DrawEllipse(null, cursorInner, hover, brushDispRadius, brushDispRadius);
      // Tiny crosshair so the user sees the dab centre when the brush is large.
      var crossPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 1);
      context.DrawLine(crossPen, new Point(hover.X - 4, hover.Y), new Point(hover.X + 4, hover.Y));
      context.DrawLine(crossPen, new Point(hover.X, hover.Y - 4), new Point(hover.X, hover.Y + 4));
    }
  }

  protected override void OnPointerPressed(PointerPressedEventArgs e) {
    base.OnPointerPressed(e);
    if (this.ActiveAdjustment is null)
      return;
    var pos = e.GetPosition(this);
    var rect = this.ComputeImageRect();
    if (rect.Width <= 1 || rect.Height <= 1)
      return;

    if (this.BrushMode && this.ActiveAdjustment.Mask.Type is LocalMaskType.Brush or LocalMaskType.Inpaint) {
      this._painting = true;
      this.AddDab(pos, rect);
      e.Pointer.Capture(this);
      e.Handled = true;
      return;
    }

    var mask = this.ActiveAdjustment.Mask;
    var hit = this.HitTestHandle(pos, mask, rect);
    if (hit >= 0) {
      this._draggingHandle = hit;
      e.Pointer.Capture(this);
      e.Handled = true;
    }
  }

  protected override void OnPointerMoved(PointerEventArgs e) {
    base.OnPointerMoved(e);
    if (this.ActiveAdjustment is null)
      return;
    var pos = e.GetPosition(this);
    this._hoverPos = pos;
    var rect = this.ComputeImageRect();
    if (rect.Width <= 1 || rect.Height <= 1)
      return;

    if (this._painting && this.BrushMode) {
      var props = e.GetCurrentPoint(this).Properties;
      if (props.IsLeftButtonPressed)
        this.AddDab(pos, rect);
      else
        this._painting = false;
      return;
    }

    // Cursor-follow refresh while hovering in brush mode without painting.
    if (this.BrushMode && this._draggingHandle < 0)
      this.InvalidateVisual();

    if (this._draggingHandle < 0)
      return;
    var (nx, ny) = this.CanvasToNorm(pos, rect);
    var mask = this.ActiveAdjustment.Mask;
    LocalMask? updated = mask.Type switch {
      LocalMaskType.Linear => this._draggingHandle == 0
        ? mask with { X0 = nx, Y0 = ny }
        : mask with { X1 = nx, Y1 = ny },
      LocalMaskType.Radial => this._draggingHandle switch {
        0 => mask with { CenterX = nx, CenterY = ny },
        1 => mask with { RadiusX = Math.Max(0.01, Math.Abs(nx - mask.CenterX)) },
        2 => mask with { RadiusY = Math.Max(0.01, Math.Abs(ny - mask.CenterY)) },
        _ => null
      },
      _ => null
    };
    if (updated is null)
      return;
    this.ActiveAdjustment = this.ActiveAdjustment with { Mask = updated };
    this.MaskChanged?.Invoke(this, updated);
    this.InvalidateVisual();
  }

  protected override void OnPointerReleased(PointerReleasedEventArgs e) {
    base.OnPointerReleased(e);
    if (this._draggingHandle >= 0 || this._painting) {
      this._draggingHandle = -1;
      this._painting = false;
      e.Pointer.Capture(null);
      e.Handled = true;
    }
  }

  protected override void OnPointerExited(PointerEventArgs e) {
    base.OnPointerExited(e);
    this._hoverPos = null;
    if (this.BrushMode)
      this.InvalidateVisual();
  }

  protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
    base.OnPointerWheelChanged(e);
    // Only intercept scroll when actively painting; otherwise let the host
    // (preview scroll-zoom etc.) handle it.
    if (this.ActiveAdjustment is null || !this.BrushMode)
      return;
    var dy = e.Delta.Y;
    if (Math.Abs(dy) < 1e-6)
      return;
    var factor = dy > 0 ? 1.15 : 1 / 1.15;
    this.BrushRadius = Math.Clamp(this.BrushRadius * factor, 0.005, 0.5);
    this.BrushRadiusChanged?.Invoke(this, this.BrushRadius);
    this.InvalidateVisual();
    e.Handled = true;
  }

  private void AddDab(Point pos, Rect rect) {
    var (nx, ny) = this.CanvasToNorm(pos, rect);
    var dab = new BrushDab(nx, ny, this.BrushRadius, this.EraseMode ? -this.BrushFlow : this.BrushFlow);
    this.BrushDabAdded?.Invoke(this, dab);
    this.InvalidateVisual();
  }

  private static void DrawHandle(DrawingContext context, Point centre, IBrush fill, IPen edge)
    => context.DrawEllipse(fill, edge, centre, HandleVisualRadius, HandleVisualRadius);

  private int HitTestHandle(Point pos, LocalMask mask, Rect rect) {
    switch (mask.Type) {
      case LocalMaskType.Linear: {
        var p0 = this.NormToCanvas(mask.X0, mask.Y0, rect);
        var p1 = this.NormToCanvas(mask.X1, mask.Y1, rect);
        if (Distance(pos, p0) <= HandleHitRadius) return 0;
        if (Distance(pos, p1) <= HandleHitRadius) return 1;
        break;
      }
      case LocalMaskType.Radial: {
        var c = this.NormToCanvas(mask.CenterX, mask.CenterY, rect);
        var hx = new Point(c.X + mask.RadiusX * rect.Width, c.Y);
        var hy = new Point(c.X, c.Y + mask.RadiusY * rect.Height);
        if (Distance(pos, c) <= HandleHitRadius)  return 0;
        if (Distance(pos, hx) <= HandleHitRadius) return 1;
        if (Distance(pos, hy) <= HandleHitRadius) return 2;
        break;
      }
    }
    return -1;
  }

  private static double Distance(Point a, Point b) {
    var dx = a.X - b.X;
    var dy = a.Y - b.Y;
    return Math.Sqrt(dx * dx + dy * dy);
  }

  private Point NormToCanvas(double nx, double ny, Rect rect)
    => new(rect.X + nx * rect.Width, rect.Y + ny * rect.Height);

  private (double nx, double ny) CanvasToNorm(Point p, Rect rect) {
    var nx = (p.X - rect.X) / rect.Width;
    var ny = (p.Y - rect.Y) / rect.Height;
    return (Math.Clamp(nx, 0, 1), Math.Clamp(ny, 0, 1));
  }

  /// <summary>
  /// Compute the rect inside our bounds that the underlying preview
  /// <see cref="Image"/> actually fills given Stretch=Uniform / DownOnly.
  /// </summary>
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
