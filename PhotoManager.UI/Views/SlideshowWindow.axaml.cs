using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Hawkynt.PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Point = Avalonia.Point;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Fullscreen slideshow / kiosk mode. Cross-fades between two overlapping
/// Image controls with optional Ken Burns (subtle pan-zoom) animation.
/// Arrow keys advance manually; a DispatcherTimer auto-advances; ESC exits.
/// Only the current and previous bitmaps are kept alive to cap memory.
/// </summary>
public partial class SlideshowWindow : Window {
  private const int CrossFadeDurationMs = 800;
  private const int CrossFadeStepMs = 16;   // ~60 fps
  private const int ControlHideDelayMs = 3000;
  private const int MaxPreviewEdge = 1920;

  private readonly IReadOnlyList<FileInfo> _originalOrder;
  private List<FileInfo> _photos;
  private int _currentIndex = -1;
  private bool _isPlaying = true;
  private bool _isShuffled;

  // Cross-fade state
  private bool _showingOnB;       // which Image is currently "front"
  private Bitmap? _bitmapA;
  private Bitmap? _bitmapB;
  private DispatcherTimer? _fadeTimer;
  private double _fadeProgress;
  private bool _isFading;

  // Ken Burns state
  private static readonly Random _kenBurnsRng = new();

  // Auto-advance timer
  private DispatcherTimer? _advanceTimer;
  private int _intervalSeconds = 5;

  // Control overlay auto-hide
  private DispatcherTimer? _cursorHideTimer;
  private bool _overlayVisible = true;
  private Avalonia.Point _lastMousePos;

  public SlideshowWindow() : this(Array.Empty<FileInfo>()) { }

  public SlideshowWindow(IReadOnlyList<FileInfo> photos) {
    this.InitializeComponent();

    this._originalOrder = photos;
    this._photos = new List<FileInfo>(photos);

    this.Opened += this.OnWindowOpened;
    this.KeyDown += this.OnSlideshowKeyDown;
    this.PointerMoved += this.OnSlideshowPointerMoved;
  }

  private async void OnWindowOpened(object? sender, EventArgs e) {
    this.EnterFullscreen();
    this.SetupTimers();

    if (this._photos.Count > 0)
      await this.ShowSlideAsync(0);
  }

  // ── Fullscreen ──────────────────────────────────────────────────

  private void EnterFullscreen() {
    this.SystemDecorations = SystemDecorations.None;
    this.WindowState = WindowState.Maximized;
    this.Topmost = true;
  }

  private void ExitFullscreen() {
    this.Topmost = false;
    this.WindowState = WindowState.Normal;
    this.SystemDecorations = SystemDecorations.Full;
  }

  // ── Timers ──────────────────────────────────────────────────────

  private void SetupTimers() {
    this._advanceTimer = new DispatcherTimer {
      Interval = TimeSpan.FromSeconds(this._intervalSeconds)
    };
    this._advanceTimer.Tick += this.OnAdvanceTick;
    if (this._isPlaying)
      this._advanceTimer.Start();

    this._cursorHideTimer = new DispatcherTimer {
      Interval = TimeSpan.FromMilliseconds(ControlHideDelayMs)
    };
    this._cursorHideTimer.Tick += this.OnCursorHideTick;
    this._cursorHideTimer.Start();
  }

  private async void OnAdvanceTick(object? sender, EventArgs e) {
    if (this._isFading)
      return;
    await this.AdvanceAsync(+1);
  }

  private void OnCursorHideTick(object? sender, EventArgs e) {
    if (!this._overlayVisible)
      return;
    this._overlayVisible = false;
    if (this.FindControl<Border>("ControlOverlay") is { } overlay)
      overlay.Opacity = 0;
    this.Cursor = new Cursor(StandardCursorType.None);
    this._cursorHideTimer?.Stop();
  }

  // ── Slide loading ───────────────────────────────────────────────

  private async Task ShowSlideAsync(int index) {
    if (this._photos.Count == 0)
      return;
    index = Math.Clamp(index, 0, this._photos.Count - 1);
    if (index == this._currentIndex)
      return;

    this._currentIndex = index;
    this.UpdateCounter();

    var file = this._photos[index];
    Bitmap? newBitmap;
    try {
      newBitmap = await LoadBitmapAsync(file);
    } catch {
      // Skip unloadable files silently.
      return;
    }

    if (newBitmap is null)
      return;

    // Determine which Image control receives the new bitmap.
    var incoming = this._showingOnB
      ? this.FindControl<Avalonia.Controls.Image>("ImageA")
      : this.FindControl<Avalonia.Controls.Image>("ImageB");
    var outgoing = this._showingOnB
      ? this.FindControl<Avalonia.Controls.Image>("ImageB")
      : this.FindControl<Avalonia.Controls.Image>("ImageA");

    if (incoming is null || outgoing is null)
      return;

    // Set the new bitmap on the incoming control (invisible at opacity 0).
    incoming.Source = newBitmap;
    incoming.Opacity = 0;

    // Apply Ken Burns on the incoming image.
    ApplyKenBurns(incoming);

    // Cross-fade
    await this.CrossFadeAsync(incoming, outgoing);

    // Dispose the OLD bitmap that was on the now-hidden control.
    var oldBitmap = this._showingOnB ? this._bitmapB : this._bitmapA;
    if (this._showingOnB)
      this._bitmapA = newBitmap;
    else
      this._bitmapB = newBitmap;
    oldBitmap?.Dispose();

    this._showingOnB = !this._showingOnB;

    // Reset the advance timer so the interval counts from the new slide.
    this.ResetAdvanceTimer();
  }

  private static async Task<Bitmap?> LoadBitmapAsync(FileInfo file) {
    var jpegBytes = await Task.Run(async () => {
      using var img = await RawImageLoader.LoadAsync(file);
      var longest = Math.Max(img.Width, img.Height);
      if (longest > MaxPreviewEdge) {
        var scale = (double)MaxPreviewEdge / longest;
        img.Mutate(c => c.Resize((int)(img.Width * scale), (int)(img.Height * scale)));
      }
      using var ms = new MemoryStream();
      img.SaveAsJpeg(ms);
      return ms.ToArray();
    });

    using var stream = new MemoryStream(jpegBytes, writable: false);
    return new Bitmap(stream);
  }

  // ── Cross-fade animation ────────────────────────────────────────

  private Task CrossFadeAsync(Avalonia.Controls.Image incoming, Avalonia.Controls.Image outgoing) {
    var tcs = new TaskCompletionSource();
    this._isFading = true;
    this._fadeProgress = 0;

    var steps = CrossFadeDurationMs / CrossFadeStepMs;
    var increment = 1.0 / steps;

    this._fadeTimer?.Stop();
    this._fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CrossFadeStepMs) };
    this._fadeTimer.Tick += (_, _) => {
      this._fadeProgress += increment;
      if (this._fadeProgress >= 1.0) {
        incoming.Opacity = 1;
        outgoing.Opacity = 0;
        this._fadeTimer.Stop();
        this._isFading = false;
        tcs.TrySetResult();
        return;
      }
      incoming.Opacity = this._fadeProgress;
      outgoing.Opacity = 1.0 - this._fadeProgress;
    };
    this._fadeTimer.Start();
    return tcs.Task;
  }

  // ── Ken Burns ───────────────────────────────────────────────────

  private static void ApplyKenBurns(Avalonia.Controls.Image image) {
    // Subtle random scale (1.0 - 1.15) and translate (-5% to +5%).
    var scale = 1.0 + _kenBurnsRng.NextDouble() * 0.15;
    var translateX = (_kenBurnsRng.NextDouble() - 0.5) * 0.10;
    var translateY = (_kenBurnsRng.NextDouble() - 0.5) * 0.10;

    var bounds = image.Bounds;
    var centerX = bounds.Width / 2.0;
    var centerY = bounds.Height / 2.0;

    var transform = new TransformGroup();
    transform.Children.Add(new ScaleTransform(scale, scale));
    transform.Children.Add(new TranslateTransform(
      centerX * translateX,
      centerY * translateY));

    image.RenderTransform = transform;
    image.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
  }

  // ── Navigation ──────────────────────────────────────────────────

  private async Task AdvanceAsync(int delta) {
    if (this._photos.Count == 0)
      return;
    var next = this._currentIndex + delta;
    // Wrap around.
    if (next >= this._photos.Count)
      next = 0;
    else if (next < 0)
      next = this._photos.Count - 1;
    await this.ShowSlideAsync(next);
  }

  private void UpdateCounter() {
    if (this.FindControl<TextBlock>("SlideCounter") is { } counter)
      counter.Text = $"{this._currentIndex + 1} / {this._photos.Count}";
  }

  private void ResetAdvanceTimer() {
    if (this._advanceTimer is null)
      return;
    this._advanceTimer.Stop();
    if (this._isPlaying)
      this._advanceTimer.Start();
  }

  // ── Shuffle (Fisher-Yates) ──────────────────────────────────────

  /// <summary>
  /// Fisher-Yates shuffle. Produces a uniformly random permutation.
  /// </summary>
  public static void FisherYatesShuffle<T>(IList<T> list, Random? rng = null) {
    rng ??= Random.Shared;
    for (var i = list.Count - 1; i > 0; i--) {
      var j = rng.Next(i + 1);
      (list[i], list[j]) = (list[j], list[i]);
    }
  }

  private void ToggleShuffle() {
    if (this._isShuffled) {
      // Restore original order, keep viewing the same file.
      var currentFile = this._currentIndex >= 0 && this._currentIndex < this._photos.Count
        ? this._photos[this._currentIndex]
        : null;
      this._photos = new List<FileInfo>(this._originalOrder);
      this._isShuffled = false;
      if (currentFile is not null) {
        var idx = this._photos.IndexOf(currentFile);
        if (idx >= 0)
          this._currentIndex = idx;
      }
    } else {
      var currentFile = this._currentIndex >= 0 && this._currentIndex < this._photos.Count
        ? this._photos[this._currentIndex]
        : null;
      FisherYatesShuffle(this._photos);
      this._isShuffled = true;
      if (currentFile is not null) {
        var idx = this._photos.IndexOf(currentFile);
        if (idx >= 0)
          this._currentIndex = idx;
      }
    }

    this.UpdateCounter();
    this.UpdateShuffleButton();
  }

  private void UpdateShuffleButton() {
    if (this.FindControl<Button>("ShuffleButton") is { } btn)
      btn.Foreground = this._isShuffled
        ? new SolidColorBrush(Colors.Gold)
        : new SolidColorBrush(Colors.White);
  }

  // ── Keyboard ────────────────────────────────────────────────────

  private async void OnSlideshowKeyDown(object? sender, KeyEventArgs e) {
    switch (e.Key) {
      case Key.Right:
      case Key.Space:
      case Key.PageDown:
        await this.AdvanceAsync(+1);
        e.Handled = true;
        break;
      case Key.Left:
      case Key.Back:
      case Key.PageUp:
        await this.AdvanceAsync(-1);
        e.Handled = true;
        break;
      case Key.Home:
        await this.ShowSlideAsync(0);
        e.Handled = true;
        break;
      case Key.End:
        await this.ShowSlideAsync(this._photos.Count - 1);
        e.Handled = true;
        break;
      case Key.Escape:
        this.ExitAndClose();
        e.Handled = true;
        break;
      case Key.P:
        this.TogglePlayPause();
        e.Handled = true;
        break;
      case Key.S:
        this.ToggleShuffle();
        e.Handled = true;
        break;
    }
  }

  // ── Mouse ───────────────────────────────────────────────────────

  private void OnSlideshowPointerMoved(object? sender, PointerEventArgs e) {
    var pos = e.GetPosition(this);
    // Ignore sub-pixel jitter that some pointer devices generate.
    if (Math.Abs(pos.X - this._lastMousePos.X) < 2 && Math.Abs(pos.Y - this._lastMousePos.Y) < 2)
      return;
    this._lastMousePos = pos;

    this.ShowOverlay();
  }

  private void ShowOverlay() {
    if (!this._overlayVisible) {
      this._overlayVisible = true;
      if (this.FindControl<Border>("ControlOverlay") is { } overlay)
        overlay.Opacity = 1;
      this.Cursor = Cursor.Default;
    }
    // Restart the hide countdown.
    this._cursorHideTimer?.Stop();
    this._cursorHideTimer?.Start();
  }

  // ── Button handlers ─────────────────────────────────────────────

  private async void OnFirstClick(object? sender, RoutedEventArgs e) => await this.ShowSlideAsync(0);
  private async void OnPrevClick(object? sender, RoutedEventArgs e) => await this.AdvanceAsync(-1);
  private async void OnNextClick(object? sender, RoutedEventArgs e) => await this.AdvanceAsync(+1);
  private async void OnLastClick(object? sender, RoutedEventArgs e) => await this.ShowSlideAsync(this._photos.Count - 1);

  private void OnPlayPauseClick(object? sender, RoutedEventArgs e) => this.TogglePlayPause();
  private void OnShuffleClick(object? sender, RoutedEventArgs e) => this.ToggleShuffle();

  private void OnExitClick(object? sender, RoutedEventArgs e) => this.ExitAndClose();

  private void OnIntervalChanged(object? sender, SelectionChangedEventArgs e) {
    if (this.FindControl<ComboBox>("IntervalCombo")?.SelectedItem is not ComboBoxItem item)
      return;
    if (item.Tag is not string tagStr || !int.TryParse(tagStr, out var seconds))
      return;
    this._intervalSeconds = seconds;
    if (this._advanceTimer is not null) {
      this._advanceTimer.Interval = TimeSpan.FromSeconds(seconds);
      this.ResetAdvanceTimer();
    }
  }

  private void TogglePlayPause() {
    this._isPlaying = !this._isPlaying;
    if (this._isPlaying) {
      this._advanceTimer?.Start();
      if (this.FindControl<Button>("PlayPauseButton") is { } btn)
        btn.Content = "⏯";
    } else {
      this._advanceTimer?.Stop();
      if (this.FindControl<Button>("PlayPauseButton") is { } btn)
        btn.Content = "▶";
    }
  }

  private void ExitAndClose() {
    this._advanceTimer?.Stop();
    this._fadeTimer?.Stop();
    this._cursorHideTimer?.Stop();
    this._bitmapA?.Dispose();
    this._bitmapB?.Dispose();
    this.ExitFullscreen();
    this.Close();
  }
}
