using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PhotoManager.Core.Develop;

namespace PhotoManager.UI.Controls;

/// <summary>
/// Lightweight tone-curve editor — a square Canvas-style control that
/// renders the active curve and a faint histogram backdrop, lets the user
/// drag control points, click empty space to insert a new point, and
/// right-click to remove. Linear interpolation; spline / Bezier modes are
/// a follow-up.
///
/// Does not own a DevelopSettings instance — it surfaces a CurveChanged
/// event so the host (EditImageWindow) can fold the new points back into
/// its working settings.
/// </summary>
public sealed class ToneCurveEditor : Control {
  private const double HitRadius = 8;     // px; click inside this radius selects a point
  private const double InsertRadius = 12; // px; click further than this from any point inserts a new one

  private List<CurvePoint> _points = new() { new(0, 0), new(1, 1) };
  private int _draggingIndex = -1;
  private int[]? _luminanceHistogram;
  private CurveInterpolation _interpolation = CurveInterpolation.Linear;

  public event EventHandler<IReadOnlyList<CurvePoint>>? CurveChanged;

  public ToneCurveEditor() {
    this.MinWidth = 180;
    this.MinHeight = 120;
    // Render() always draws a filled rectangle covering Bounds, so the
    // entire area is hit-testable without an explicit Background brush.
  }

  /// <summary>
  /// Replace the curve. The control suppresses its own CurveChanged event
  /// during this so a host can sync the editor without infinite loops.
  /// </summary>
  public void SetCurve(IReadOnlyList<CurvePoint>? points) {
    if (points is null || points.Count < 2)
      this._points = new() { new(0, 0), new(1, 1) };
    else
      this._points = points.OrderBy(p => p.X).Select(p => new CurvePoint(
        Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1))).ToList();
    this.InvalidateVisual();
  }

  public void SetHistogram(int[]? luminanceCounts) {
    this._luminanceHistogram = luminanceCounts;
    this.InvalidateVisual();
  }

  /// <summary>Switch the interpolation between user-placed control points.
  /// The host re-renders the preview separately; this only updates the on-screen curve.</summary>
  public void SetInterpolation(CurveInterpolation mode) {
    this._interpolation = mode;
    this.InvalidateVisual();
  }

  protected override void OnPointerPressed(PointerPressedEventArgs e) {
    base.OnPointerPressed(e);
    var pos = e.GetPosition(this);
    var (rect, _) = this.GetPlotRect();

    var properties = e.GetCurrentPoint(this).Properties;

    // Right-click → remove the closest point (but never the two end anchors).
    if (properties.IsRightButtonPressed) {
      var idx = this.NearestPointIndex(pos, rect);
      if (idx > 0 && idx < this._points.Count - 1) {
        this._points.RemoveAt(idx);
        this.RaiseCurveChanged();
        this.InvalidateVisual();
        e.Handled = true;
      }
      return;
    }

    if (!properties.IsLeftButtonPressed)
      return;

    var hit = this.NearestPointIndex(pos, rect);
    if (hit >= 0 && DistanceToPoint(this._points[hit], pos, rect) <= HitRadius) {
      this._draggingIndex = hit;
    } else {
      // Click in empty area → insert a new control point.
      var nx = Math.Clamp((pos.X - rect.X) / rect.Width, 0, 1);
      var ny = Math.Clamp(1 - (pos.Y - rect.Y) / rect.Height, 0, 1);
      this._points.Add(new CurvePoint(nx, ny));
      this._points = this._points.OrderBy(p => p.X).ToList();
      this._draggingIndex = this._points.FindIndex(p => Math.Abs(p.X - nx) < 1e-9 && Math.Abs(p.Y - ny) < 1e-9);
      this.RaiseCurveChanged();
    }
    e.Pointer.Capture(this);
    this.InvalidateVisual();
    e.Handled = true;
  }

  protected override void OnPointerMoved(PointerEventArgs e) {
    base.OnPointerMoved(e);
    if (this._draggingIndex < 0)
      return;
    var (rect, _) = this.GetPlotRect();
    var pos = e.GetPosition(this);
    var nx = Math.Clamp((pos.X - rect.X) / rect.Width, 0, 1);
    var ny = Math.Clamp(1 - (pos.Y - rect.Y) / rect.Height, 0, 1);

    // End anchors keep their X so the curve domain stays [0,1].
    if (this._draggingIndex == 0)
      nx = 0;
    else if (this._draggingIndex == this._points.Count - 1)
      nx = 1;

    this._points[this._draggingIndex] = new CurvePoint(nx, ny);
    // Keep the list X-sorted; if drag pushes past a neighbour we resort
    // and fix the dragging index.
    var current = this._points[this._draggingIndex];
    this._points = this._points.OrderBy(p => p.X).ToList();
    this._draggingIndex = this._points.IndexOf(current);

    this.RaiseCurveChanged();
    this.InvalidateVisual();
  }

  protected override void OnPointerReleased(PointerReleasedEventArgs e) {
    base.OnPointerReleased(e);
    if (this._draggingIndex >= 0) {
      this._draggingIndex = -1;
      e.Pointer.Capture(null);
      e.Handled = true;
    }
  }

  public override void Render(DrawingContext context) {
    base.Render(context);
    var (rect, _) = this.GetPlotRect();

    // Background plate with a 4×4 grid so the user has reference lines.
    context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)), rect);
    var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), 1);
    for (var i = 1; i < 4; i++) {
      var x = rect.X + rect.Width * i / 4.0;
      context.DrawLine(gridPen, new Point(x, rect.Y), new Point(x, rect.Bottom));
      var y = rect.Y + rect.Height * i / 4.0;
      context.DrawLine(gridPen, new Point(rect.X, y), new Point(rect.Right, y));
    }
    // Identity diagonal — visual reference for "no change".
    context.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 1, DashStyle.Dash),
      new Point(rect.X, rect.Bottom), new Point(rect.Right, rect.Y));

    // Histogram backdrop — luminance, drawn underneath the curve so the
    // user can shape responses to specific tonal regions.
    if (this._luminanceHistogram is { Length: > 0 } hist) {
      var max = 1;
      for (var i = 0; i < hist.Length; i++) if (hist[i] > max) max = hist[i];
      var brush = new SolidColorBrush(Color.FromArgb(0x40, 0x88, 0x88, 0x88));
      var sx = rect.Width / hist.Length;
      for (var i = 0; i < hist.Length; i++) {
        var h = hist[i] / (double)max * rect.Height;
        context.FillRectangle(brush, new Rect(rect.X + i * sx, rect.Bottom - h, sx, h));
      }
    }

    // The curve itself — built from the same LUT the developer uses, so
    // what the user sees here is exactly what gets applied.
    var lut = ImageDeveloper.BuildCurveLut(this._points, this._interpolation)
      ?? Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
    var curvePen = new Pen(new SolidColorBrush(Color.FromRgb(0xF1, 0xC4, 0x0F)), 2);
    for (var i = 1; i < 256; i++) {
      var x0 = rect.X + (i - 1) / 255.0 * rect.Width;
      var y0 = rect.Bottom - lut[i - 1] / 255.0 * rect.Height;
      var x1 = rect.X + i / 255.0 * rect.Width;
      var y1 = rect.Bottom - lut[i] / 255.0 * rect.Height;
      context.DrawLine(curvePen, new Point(x0, y0), new Point(x1, y1));
    }

    // Control points on top, end anchors smaller / dimmer so users see
    // they're meant to stay put.
    var pointFill = new SolidColorBrush(Colors.White);
    var pointStroke = new Pen(new SolidColorBrush(Color.FromRgb(0xF1, 0xC4, 0x0F)), 1.5);
    for (var i = 0; i < this._points.Count; i++) {
      var p = this._points[i];
      var px = rect.X + p.X * rect.Width;
      var py = rect.Bottom - p.Y * rect.Height;
      var radius = (i == 0 || i == this._points.Count - 1) ? 3.5 : 5;
      context.DrawEllipse(pointFill, pointStroke, new Point(px, py), radius, radius);
    }
  }

  private (Rect Rect, Size Padding) GetPlotRect() {
    const double pad = 4;
    var w = Math.Max(0, this.Bounds.Width - pad * 2);
    var h = Math.Max(0, this.Bounds.Height - pad * 2);
    return (new Rect(pad, pad, w, h), new Size(pad, pad));
  }

  private int NearestPointIndex(Point pos, Rect rect) {
    var bestI = -1;
    var bestD = double.MaxValue;
    for (var i = 0; i < this._points.Count; i++) {
      var d = DistanceToPoint(this._points[i], pos, rect);
      if (d < bestD) { bestD = d; bestI = i; }
    }
    return bestI;
  }

  private static double DistanceToPoint(CurvePoint p, Point screen, Rect rect) {
    var px = rect.X + p.X * rect.Width;
    var py = rect.Bottom - p.Y * rect.Height;
    var dx = px - screen.X;
    var dy = py - screen.Y;
    return Math.Sqrt(dx * dx + dy * dy);
  }

  private void RaiseCurveChanged()
    => this.CurveChanged?.Invoke(this, this._points.Select(p => p).ToList());
}
