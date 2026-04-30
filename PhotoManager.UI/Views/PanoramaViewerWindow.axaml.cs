using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PhotoManager.Core.Panorama;
using PhotoManager.Core.Previews;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.UI.Views;

/// <summary>
/// Pan/zoom viewer for 360° equirectangular panoramas. Drag = look around,
/// wheel = zoom (FOV), sliders mirror the same state so users can also dial
/// exact angles. Renders go off-thread via Task.Run so dragging stays
/// responsive on multi-megapixel sources; renders are debounced behind a
/// 60 ms timer and use a low-resolution preview while the user is actively
/// dragging, then fall back to full window resolution on drag-end.
///
/// <para>Two ctors are kept: the <see cref="FileInfo"/> overload is the
/// "open file" path used by the main menu, while the <see cref="Image{TPixel}"/>
/// overload lets in-memory producers (the spherical stitcher) hand off a
/// just-built panorama without a save round-trip. The latter takes ownership
/// of the image and disposes it when the window closes.</para>
/// </summary>
public partial class PanoramaViewerWindow : Window {
  private const int DragPreviewWidth = 800;
  private const int DragPreviewHeight = 450;
  private static readonly TimeSpan RenderDebounce = TimeSpan.FromMilliseconds(60);
  private const double PixelsPerDegree = 3.3;

  private readonly FileInfo? _sourceFile;
  private readonly Image<Rgba32>? _seededSource;
  private Image<Rgba32>? _source;
  private DispatcherTimer? _renderTimer;
  private CancellationTokenSource? _renderCts;
  private bool _isDragging;
  private bool _suppressSliderEvents;
  private Avalonia.Point? _dragStart;
  private double _dragStartYaw;
  private double _dragStartPitch;

  private double _yaw;
  private double _pitch;
  private double _fov = Equirectangular.DefaultFovDegrees;

  public PanoramaViewerWindow() : this((FileInfo?)null) { }

  public PanoramaViewerWindow(FileInfo? sourceFile) {
    this.InitializeComponent();
    this._sourceFile = sourceFile;

    this.AttachSliderHandlers();
    this.UpdateSliderValues();
    this.UpdateLabels();

    this.Opened += this.OnOpened;
    this.Closed += this.OnClosed;
  }

  /// <summary>
  /// In-memory ctor: hands an already-decoded panorama directly to the
  /// viewer (no file picker, no disk round-trip). Ownership of
  /// <paramref name="source"/> transfers to the window — it disposes on
  /// close. Used by the spherical stitcher's "Open in 360° viewer" flow.
  /// </summary>
  public PanoramaViewerWindow(Image<Rgba32> source) : this((FileInfo?)null) {
    ArgumentNullException.ThrowIfNull(source);
    this._seededSource = source;
  }

  private void AttachSliderHandlers() {
    if (this.FindControl<Slider>("YawSlider") is { } y) {
      y.Minimum = 0; y.Maximum = 360;
      y.ValueChanged += (_, e) => {
        if (this._suppressSliderEvents) return;
        this._yaw = Equirectangular.WrapDegrees(e.NewValue);
        this.UpdateLabels();
        this.RequestRender(preview: false);
      };
    }
    if (this.FindControl<Slider>("PitchSlider") is { } p) {
      p.ValueChanged += (_, e) => {
        if (this._suppressSliderEvents) return;
        this._pitch = Math.Clamp(e.NewValue, -89, 89);
        this.UpdateLabels();
        this.RequestRender(preview: false);
      };
    }
    if (this.FindControl<Slider>("FovSlider") is { } f) {
      f.Minimum = Equirectangular.MinFovDegrees;
      f.Maximum = Equirectangular.MaxFovDegrees;
      f.ValueChanged += (_, e) => {
        if (this._suppressSliderEvents) return;
        this._fov = Math.Clamp(e.NewValue, Equirectangular.MinFovDegrees, Equirectangular.MaxFovDegrees);
        this.UpdateLabels();
        this.RequestRender(preview: false);
      };
    }
  }

  private async void OnOpened(object? sender, EventArgs e) {
    this._renderTimer = new DispatcherTimer { Interval = RenderDebounce };
    this._renderTimer.Tick += (_, __) => {
      this._renderTimer!.Stop();
      _ = this.RenderAsync(this._wantPreview);
    };

    if (this._seededSource is not null) {
      this._source = this._seededSource;
      this.SetStatus($"In-memory panorama — {this._seededSource.Width}×{this._seededSource.Height}");
      this.RequestRender(preview: false);
      return;
    }

    if (this._sourceFile is null) {
      var picked = await this.PickSourceFileAsync();
      if (picked is null) {
        this.SetStatus("No file selected.");
        this.Close();
        return;
      }
      await this.LoadSourceAsync(picked);
    } else {
      await this.LoadSourceAsync(this._sourceFile);
    }
  }

  private void OnClosed(object? sender, EventArgs e) {
    this._renderCts?.Cancel();
    this._renderCts?.Dispose();
    this._renderCts = null;
    this._source?.Dispose();
    this._source = null;
  }

  private async Task<FileInfo?> PickSourceFileAsync() {
    var topLevel = TopLevel.GetTopLevel(this);
    if (topLevel?.StorageProvider is not { CanOpen: true } storage)
      return null;
    var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Pick a 360° panorama",
      AllowMultiple = false,
      FileTypeFilter = [
        new FilePickerFileType("Images") {
          Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.tif", "*.tiff"]
        }
      ]
    });
    if (files.Count == 0)
      return null;
    var path = files[0].TryGetLocalPath();
    return string.IsNullOrWhiteSpace(path) ? null : new FileInfo(path);
  }

  private async Task LoadSourceAsync(FileInfo file) {
    this.SetStatus($"Loading {file.Name}…");
    try {
      var image = await Task.Run(async () => await LoadImageAsync(file)).ConfigureAwait(true);
      if (image is null) {
        this.SetStatus("Couldn't decode that file.");
        return;
      }
      this._source?.Dispose();
      this._source = image;
      this.SetStatus($"{file.Name} — {image.Width}×{image.Height}");
      this.RequestRender(preview: false);
    } catch (Exception ex) {
      this.SetStatus($"Load failed: {ex.Message}");
    }
  }

  private static async Task<Image<Rgba32>?> LoadImageAsync(FileInfo file) {
    if (!file.Exists)
      return null;
    // Reuse the RAW-aware path we already have for thumbnails: prefer the
    // embedded JPEG, fall back to a generic decode.
    var raw = await RawPreviewExtractor.ExtractLargestJpegAsync(file, CancellationToken.None);
    if (raw != null) {
      try {
        using var ms = new MemoryStream(raw, writable: false);
        return await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(ms);
      } catch {
        // Fall through.
      }
    }
    try {
      return await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(file.FullName);
    } catch {
      return null;
    }
  }

  // ---------- Pointer (drag + wheel) ----------

  private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e) {
    if (this._source is null) return;
    var props = e.GetCurrentPoint(this).Properties;
    if (!props.IsLeftButtonPressed) return;
    this._isDragging = true;
    this._dragStart = e.GetPosition(this);
    this._dragStartYaw = this._yaw;
    this._dragStartPitch = this._pitch;
    e.Pointer.Capture(this.FindControl<Avalonia.Controls.Image>("ViewportImage"));
  }

  private void OnViewportPointerMoved(object? sender, PointerEventArgs e) {
    if (!this._isDragging || this._dragStart is not { } start) return;
    var now = e.GetPosition(this);
    var dx = now.X - start.X;
    var dy = now.Y - start.Y;

    var newYaw = Equirectangular.WrapDegrees(this._dragStartYaw - dx / PixelsPerDegree);
    var newPitch = Math.Clamp(this._dragStartPitch + dy / PixelsPerDegree, -89, 89);

    this._yaw = newYaw;
    this._pitch = newPitch;
    this.UpdateSliderValues();
    this.UpdateLabels();
    this.RequestRender(preview: true);
  }

  private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e) {
    if (!this._isDragging) return;
    this._isDragging = false;
    this._dragStart = null;
    e.Pointer.Capture(null);
    this.RequestRender(preview: false);
  }

  private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e) {
    if (this._source is null) return;
    // Wheel up zooms in (smaller FOV).
    var step = e.Delta.Y > 0 ? -5.0 : 5.0;
    this._fov = Math.Clamp(this._fov + step, Equirectangular.MinFovDegrees, Equirectangular.MaxFovDegrees);
    this.UpdateSliderValues();
    this.UpdateLabels();
    this.RequestRender(preview: true);
    e.Handled = true;
  }

  // ---------- Buttons ----------

  private void OnResetClick(object? sender, RoutedEventArgs e) {
    this._yaw = 0;
    this._pitch = 0;
    this._fov = Equirectangular.DefaultFovDegrees;
    this.UpdateSliderValues();
    this.UpdateLabels();
    this.RequestRender(preview: false);
  }

  private async void OnSaveAsClick(object? sender, RoutedEventArgs e) {
    if (this._source is null) {
      this.SetStatus("Nothing loaded.");
      return;
    }

    var topLevel = TopLevel.GetTopLevel(this);
    if (topLevel?.StorageProvider is not { CanSave: true } storage) {
      this.SetStatus("Save picker unavailable.");
      return;
    }

    var suggestedName = (this._sourceFile is { } src
        ? Path.GetFileNameWithoutExtension(src.Name)
        : "panorama") + "_view.jpg";

    var result = await storage.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save current view",
      SuggestedFileName = suggestedName,
      DefaultExtension = "jpg",
      FileTypeChoices = [new FilePickerFileType("JPEG") { Patterns = ["*.jpg"] }]
    });
    if (result is null) return;

    var destination = result.TryGetLocalPath();
    if (string.IsNullOrWhiteSpace(destination)) {
      this.SetStatus("Couldn't resolve save path.");
      return;
    }

    var (w, h) = this.PickSaveResolution();

    this.SetStatus("Rendering…");
    try {
      // Snapshot the source ref + state so the worker doesn't race with
      // a window-close that disposes the field underneath it.
      var src2 = this._source;
      var yaw = this._yaw; var pitch = this._pitch; var fov = this._fov;
      await Task.Run(() => {
        using var rendered = Equirectangular.Render(src2!, w, h, yaw, pitch, fov);
        rendered.SaveAsJpeg(destination);
      });
      this.SetStatus($"Saved {Path.GetFileName(destination)} ({w}×{h}).");
    } catch (Exception ex) {
      this.SetStatus($"Save failed: {ex.Message}");
    }
  }

  /// <summary>
  /// Decide the export resolution. We mirror the on-screen aspect ratio
  /// (so saved images match what the user is seeing) and clamp the long
  /// side at 4096 to keep file sizes sane.
  /// </summary>
  private (int W, int H) PickSaveResolution() {
    var img = this.FindControl<Avalonia.Controls.Image>("ViewportImage");
    var pixelW = (int)(img?.Bounds.Width ?? 1280);
    var pixelH = (int)(img?.Bounds.Height ?? 720);
    if (pixelW < 16) pixelW = 1280;
    if (pixelH < 16) pixelH = 720;

    const int MaxEdge = 4096;
    var longest = Math.Max(pixelW, pixelH);
    if (longest <= MaxEdge) return (pixelW, pixelH);
    var scale = (double)MaxEdge / longest;
    return ((int)Math.Round(pixelW * scale), (int)Math.Round(pixelH * scale));
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  // ---------- Render scheduling ----------

  private bool _wantPreview;

  private void RequestRender(bool preview) {
    if (this._source is null) return;
    this._wantPreview = preview;
    this._renderTimer?.Stop();
    this._renderTimer?.Start();
  }

  private async Task RenderAsync(bool preview) {
    if (this._source is not { } src) return;

    this._renderCts?.Cancel();
    this._renderCts = new CancellationTokenSource();
    var token = this._renderCts.Token;

    int targetW, targetH;
    if (preview) {
      targetW = DragPreviewWidth;
      targetH = DragPreviewHeight;
    } else {
      var img = this.FindControl<Avalonia.Controls.Image>("ViewportImage");
      targetW = (int)(img?.Bounds.Width ?? DragPreviewWidth);
      targetH = (int)(img?.Bounds.Height ?? DragPreviewHeight);
      if (targetW < 16) targetW = DragPreviewWidth;
      if (targetH < 16) targetH = DragPreviewHeight;
    }

    var yaw = this._yaw; var pitch = this._pitch; var fov = this._fov;

    try {
      var bytes = await Task.Run(() => {
        if (token.IsCancellationRequested) return null;
        using var rendered = Equirectangular.Render(src, targetW, targetH, yaw, pitch, fov);
        if (token.IsCancellationRequested) return null;
        using var ms = new MemoryStream();
        rendered.SaveAsJpeg(ms);
        return ms.ToArray();
      }, token).ConfigureAwait(true);

      if (token.IsCancellationRequested || bytes is null)
        return;

      await Dispatcher.UIThread.InvokeAsync(() => {
        if (token.IsCancellationRequested) return;
        var img = this.FindControl<Avalonia.Controls.Image>("ViewportImage");
        if (img is null) return;
        using var ms = new MemoryStream(bytes);
        img.Source = new Bitmap(ms);
      });
    } catch (OperationCanceledException) {
      // Expected — a newer render scheduled us out.
    } catch (Exception ex) {
      this.SetStatus($"Render failed: {ex.Message}");
    }
  }

  // ---------- UI sync helpers ----------

  private void UpdateSliderValues() {
    this._suppressSliderEvents = true;
    try {
      if (this.FindControl<Slider>("YawSlider") is { } y) y.Value = this._yaw;
      if (this.FindControl<Slider>("PitchSlider") is { } p) p.Value = this._pitch;
      if (this.FindControl<Slider>("FovSlider") is { } f) f.Value = this._fov;
    } finally {
      this._suppressSliderEvents = false;
    }
  }

  private void UpdateLabels() {
    var inv = CultureInfo.InvariantCulture;
    if (this.FindControl<TextBlock>("YawText") is { } y)
      y.Text = $"{this._yaw.ToString("0", inv)}°";
    if (this.FindControl<TextBlock>("PitchText") is { } p)
      p.Text = $"{this._pitch.ToString("0", inv)}°";
    if (this.FindControl<TextBlock>("FovText") is { } f)
      f.Text = $"{this._fov.ToString("0", inv)}°";
  }

  private void SetStatus(string msg) {
    if (this.FindControl<TextBlock>("StatusText") is { } t)
      t.Text = msg;
  }
}
