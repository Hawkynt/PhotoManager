using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.UI.Views;

/// <summary>
/// Simple image developer — live-preview sliders for exposure / contrast /
/// saturation / white-balance plus rotate buttons, then Save-as renders a
/// full-resolution JPEG via <see cref="ImageDeveloper"/>. Non-destructive by
/// design: the original file is never overwritten.
///
/// Preview uses a downscaled copy so slider drags stay responsive on
/// 50-megapixel RAWs; save renders off the full-resolution source image.
/// </summary>
public partial class EditImageWindow : Window {
  private const int PreviewMaxEdgePixels = 1024;

  private FileInfo? _sourceFile;
  private Image<Rgba32>? _previewSource;  // downscaled copy for live preview
  private DispatcherTimer? _updateTimer;
  private DevelopSettings _settings = new();

  public EditImageWindow() {
    this.InitializeComponent();
  }

  public EditImageWindow(FileInfo sourceFile) : this() {
    this._sourceFile = sourceFile;
    _ = this.LoadPreviewAsync();
  }

  private async Task LoadPreviewAsync() {
    if (this._sourceFile is not { Exists: true } file) {
      this.SetStatus("No file selected.");
      return;
    }

    this.SetStatus("Loading...");
    try {
      var image = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(file.FullName);
      var longest = Math.Max(image.Width, image.Height);
      if (longest > PreviewMaxEdgePixels) {
        var scale = (double)PreviewMaxEdgePixels / longest;
        image.Mutate(c => c.Resize((int)(image.Width * scale), (int)(image.Height * scale)));
      }
      this._previewSource = image;
    } catch (Exception ex) {
      this.SetStatus($"Load failed: {ex.Message}");
      return;
    }

    this.SetStatus($"{this._sourceFile.Name} ({this._previewSource.Width}×{this._previewSource.Height} preview)");
    this.UpdatePreview();
  }

  private void OnSettingsChanged(object? sender, RangeBaseValueChangedEventArgs e) => this.SchedulePreviewUpdate();

  private void OnRotateLeftClick(object? sender, RoutedEventArgs e) => this.BumpRotation(-90);
  private void OnRotateRightClick(object? sender, RoutedEventArgs e) => this.BumpRotation(90);
  private void OnRotate180Click(object? sender, RoutedEventArgs e) => this.BumpRotation(180);

  private void BumpRotation(int degrees) {
    var current = this._settings.RotationDegrees;
    var next = ((current + degrees) % 360 + 360) % 360;
    this._settings = this._settings with { RotationDegrees = next };
    this.UpdatePreview();
  }

  private void OnResetRotationClick(object? sender, RoutedEventArgs e) {
    this._settings = this._settings with { RotationDegrees = 0 };
    this.UpdatePreview();
  }

  private void OnResetAllClick(object? sender, RoutedEventArgs e) {
    this._settings = new DevelopSettings();
    if (this.FindControl<Slider>("ExposureSlider") is { } ex) ex.Value = 0;
    if (this.FindControl<Slider>("ContrastSlider") is { } co) co.Value = 0;
    if (this.FindControl<Slider>("SaturationSlider") is { } sa) sa.Value = 0;
    if (this.FindControl<Slider>("TemperatureSlider") is { } te) te.Value = 0;
    this.UpdatePreview();
  }

  private void SchedulePreviewUpdate() {
    // Debounce so dragging a slider doesn't queue a preview per tick.
    this._updateTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, OnTimerTick);
    this._updateTimer.Stop();
    this._updateTimer.Start();
  }

  private void OnTimerTick(object? sender, EventArgs e) {
    this._updateTimer?.Stop();
    this.UpdatePreview();
  }

  private void UpdatePreview() {
    if (this._previewSource is null)
      return;

    this._settings = this._settings with {
      ExposureStops   = this.FindControl<Slider>("ExposureSlider")?.Value ?? 0,
      ContrastPercent = this.FindControl<Slider>("ContrastSlider")?.Value ?? 0,
      SaturationPercent = this.FindControl<Slider>("SaturationSlider")?.Value ?? 0,
      TemperatureShift = this.FindControl<Slider>("TemperatureSlider")?.Value ?? 0
    };

    this.RefreshValueLabels();

    try {
      using var developed = ImageDeveloper.Apply(this._previewSource, this._settings);
      using var ms = new MemoryStream();
      developed.SaveAsJpeg(ms);
      ms.Position = 0;
      var bitmap = new Bitmap(ms);
      if (this.FindControl<Avalonia.Controls.Image>("PreviewImage") is { } img)
        img.Source = bitmap;
    } catch (Exception ex) {
      this.SetStatus($"Preview failed: {ex.Message}");
    }
  }

  private void RefreshValueLabels() {
    var inv = CultureInfo.InvariantCulture;
    if (this.FindControl<TextBlock>("ExposureValue") is { } ex)
      ex.Text = this._settings.ExposureStops.ToString("+0.0;-0.0;0.0", inv) + " EV";
    if (this.FindControl<TextBlock>("ContrastValue") is { } co)
      co.Text = this._settings.ContrastPercent.ToString("+0;-0;0", inv);
    if (this.FindControl<TextBlock>("SaturationValue") is { } sa)
      sa.Text = this._settings.SaturationPercent.ToString("+0;-0;0", inv);
    if (this.FindControl<TextBlock>("TemperatureValue") is { } te)
      te.Text = this._settings.TemperatureShift.ToString("+0;-0;0", inv);
  }

  private async void OnSaveAsClick(object? sender, RoutedEventArgs e) {
    if (this._sourceFile is not { Exists: true } source) {
      this.SetStatus("Nothing to save.");
      return;
    }

    var topLevel = TopLevel.GetTopLevel(this);
    if (topLevel?.StorageProvider is not { CanSave: true } storage) {
      this.SetStatus("Save picker unavailable.");
      return;
    }

    var suggested = Path.GetFileNameWithoutExtension(source.Name) + "_developed.jpg";
    var result = await storage.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save developed JPEG",
      SuggestedFileName = suggested,
      DefaultExtension = "jpg",
      FileTypeChoices = [new FilePickerFileType("JPEG") { Patterns = ["*.jpg"] }]
    });
    if (result is null)
      return;

    var destination = result.TryGetLocalPath();
    if (string.IsNullOrWhiteSpace(destination)) {
      this.SetStatus("Couldn't resolve save path.");
      return;
    }

    this.SetStatus("Rendering...");
    try {
      await ImageDeveloper.RenderToJpegAsync(source, new FileInfo(destination), this._settings);
      this.SetStatus($"Saved {Path.GetFileName(destination)}.");
    } catch (Exception ex) {
      this.SetStatus($"Save failed: {ex.Message}");
    }
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) {
    this._previewSource?.Dispose();
    this._previewSource = null;
    this.Close();
  }

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = message;
  }
}
