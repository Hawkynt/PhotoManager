using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Hawkynt.PhotoManager.Core.Library;

namespace Hawkynt.PhotoManager.UI.Controls;

/// <summary>
/// Horizontal scrubber strip showing a histogram of how many photos sit in
/// each day / week / month bucket of the currently-scanned library. Click
/// any bar to filter the main grid to that bucket; hover to see date +
/// count. Granularity (Day / Week / Month) is auto-picked from the data
/// span so the bar count stays readable.
/// </summary>
public partial class TimelineScrubber : UserControl {
  /// <summary>Fired when the user clicks a bar. Event args carries the bar's bucket start + granularity.</summary>
  public event EventHandler<TimelineBar>? BucketClicked;

  private IReadOnlyList<TimelineBar> _bars = Array.Empty<TimelineBar>();
  private const double BarSpacingPx = 2;

  public TimelineScrubber() {
    this.InitializeComponent();
    this.SizeChanged += (_, _) => this.RenderBars();
  }

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

  /// <summary>
  /// Replace the histogram from a fresh photo list. Granularity is
  /// auto-picked from the data span. The strip re-renders synchronously
  /// — caller is responsible for switching to the UI thread.
  /// </summary>
  public void SetPhotos(IEnumerable<(DateTime, FileInfo)> photos) {
    this._bars = TimelineHistogram.Build(photos);
    this.RenderBars();
  }

  private void RenderBars() {
    if (this.FindControl<Canvas>("BarsCanvas") is not { } canvas)
      return;
    canvas.Children.Clear();

    var startLabel = this.FindControl<TextBlock>("StartLabel");
    var endLabel = this.FindControl<TextBlock>("EndLabel");
    var granularityLabel = this.FindControl<TextBlock>("GranularityLabel");

    if (this._bars.Count == 0) {
      if (startLabel is not null) startLabel.Text = string.Empty;
      if (endLabel is not null) endLabel.Text = string.Empty;
      if (granularityLabel is not null) granularityLabel.Text = "(no photos scanned)";
      return;
    }

    var width = Math.Max(1, canvas.Bounds.Width);
    var height = Math.Max(1, canvas.Bounds.Height);
    var maxCount = this._bars.Max(b => b.Count);
    if (maxCount <= 0) maxCount = 1;
    var maxLog = Math.Log10(maxCount + 1);
    var barWidth = Math.Max(1, (width - this._bars.Count * BarSpacingPx) / this._bars.Count);

    for (var i = 0; i < this._bars.Count; i++) {
      var bar = this._bars[i];
      var heightFraction = bar.Count == 0 ? 0 : Math.Log10(bar.Count + 1) / maxLog;
      var h = Math.Max(2, heightFraction * (height - 4));
      var x = i * (barWidth + BarSpacingPx);
      var y = height - h;

      var rect = new Rectangle {
        Width = barWidth,
        Height = h,
        Fill = bar.Count == 0
          ? new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6))
          : new SolidColorBrush(Color.FromRgb(0x4B, 0x8B, 0xBE))
      };
      Canvas.SetLeft(rect, x);
      Canvas.SetTop(rect, y);
      ToolTip.SetTip(rect, $"{FormatBucketLabel(bar)} — {bar.Count} photo(s)");
      var capturedBar = bar;
      rect.PointerPressed += (_, _) => this.BucketClicked?.Invoke(this, capturedBar);
      rect.Cursor = new Cursor(StandardCursorType.Hand);
      canvas.Children.Add(rect);
    }

    var first = this._bars[0];
    var last = this._bars[^1];
    if (startLabel is not null) startLabel.Text = first.BucketStart.ToString("yyyy-MM-dd");
    if (endLabel is not null) endLabel.Text = last.BucketStart.ToString("yyyy-MM-dd");
    if (granularityLabel is not null) granularityLabel.Text = $"by {first.Granularity.ToString().ToLowerInvariant()}";
  }

  private static string FormatBucketLabel(TimelineBar bar) => bar.Granularity switch {
    TimelineGranularity.Day => bar.BucketStart.ToString("yyyy-MM-dd ddd"),
    TimelineGranularity.Week => $"week of {bar.BucketStart:yyyy-MM-dd}",
    TimelineGranularity.Month => bar.BucketStart.ToString("yyyy-MM"),
    _ => bar.BucketStart.ToString("yyyy-MM-dd")
  };

  /// <summary>
  /// Compute the inclusive date range covered by a bar so callers can apply
  /// the same window when filtering the main grid.
  /// </summary>
  public static (DateTime From, DateTime ToExclusive) BucketRange(TimelineBar bar) {
    var to = bar.Granularity switch {
      TimelineGranularity.Day => bar.BucketStart.AddDays(1),
      TimelineGranularity.Week => bar.BucketStart.AddDays(7),
      TimelineGranularity.Month => bar.BucketStart.AddMonths(1),
      _ => bar.BucketStart.AddDays(1)
    };
    return (bar.BucketStart, to);
  }
}
