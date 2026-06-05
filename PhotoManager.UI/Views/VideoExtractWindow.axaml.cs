using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hawkynt.PhotoManager.Core.Video;
using Hawkynt.PhotoManager.UI.Services;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Front-end for <see cref="VideoFrameExtractor"/>. Drives ffmpeg via
/// <see cref="FfmpegLocator"/> and pipes the resulting frames into the
/// panorama stitcher (when present).
/// </summary>
public partial class VideoExtractWindow : Window {
  private readonly bool _autoCloseOnSuccess;
  private CancellationTokenSource? _runCts;
  private List<FileInfo> _lastFrames = new();
  private DirectoryInfo? _lastOutputDir;

  /// <summary>
  /// Fires when the user runs an extract that succeeds. The window
  /// owner can subscribe to receive the frames straight back; in
  /// "send-back" mode the window auto-closes after raising this.
  /// </summary>
  public event Action<IReadOnlyList<FileInfo>>? FramesReady;

  public VideoExtractWindow() : this(autoCloseOnSuccess: false) { }

  public VideoExtractWindow(bool autoCloseOnSuccess) {
    this._autoCloseOnSuccess = autoCloseOnSuccess;
    this.InitializeComponent();
    this.Opened += this.OnOpened;
    this.WireSliderLabels();
  }

  private void OnOpened(object? sender, EventArgs e) {
    this.RefreshFfmpegBanner();
  }

  private void RefreshFfmpegBanner() {
    var available = FfmpegLocator.IsAvailable();
    if (this.FindControl<Border>("MissingFfmpegBanner") is { } banner)
      banner.IsVisible = !available;
    if (this.FindControl<Button>("ExtractButton") is { } btn)
      btn.IsEnabled = available;
  }

  private void WireSliderLabels() {
    if (this.FindControl<Slider>("FpsSlider") is { } fps && this.FindControl<TextBlock>("FpsLabel") is { } fpsLabel) {
      fps.PropertyChanged += (_, e) => {
        if (e.Property == Slider.ValueProperty)
          fpsLabel.Text = string.Create(CultureInfo.InvariantCulture, $"{fps.Value:0.#} fps");
      };
    }
    if (this.FindControl<Slider>("JpegQualitySlider") is { } q && this.FindControl<TextBlock>("JpegQualityLabel") is { } qLabel) {
      q.PropertyChanged += (_, e) => {
        if (e.Property == Slider.ValueProperty)
          qLabel.Text = ((int)Math.Round(q.Value)).ToString(CultureInfo.InvariantCulture);
      };
    }
  }

  private async void OnBrowseVideoClick(object? sender, RoutedEventArgs e) {
    var topLevel = TopLevel.GetTopLevel(this);
    if (topLevel?.StorageProvider is not { CanOpen: true } storage)
      return;
    var file = await storage.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = "Select video file",
      AllowMultiple = false,
      FileTypeFilter = [
        new FilePickerFileType("Video files") {
          Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm", "*.m4v"]
        }
      ]
    });
    if (file.Count == 0)
      return;
    var path = file[0].TryGetLocalPath();
    if (string.IsNullOrWhiteSpace(path))
      return;
    if (this.FindControl<TextBox>("VideoPathBox") is { } box)
      box.Text = path;
  }

  private void OnVideoPathChanged(object? sender, TextChangedEventArgs e) {
    if (this.FindControl<TextBox>("VideoPathBox") is not { } videoBox)
      return;
    if (this.FindControl<TextBox>("OutputPathBox") is not { } outBox)
      return;
    if (!string.IsNullOrWhiteSpace(outBox.Text))
      return;
    var path = videoBox.Text;
    if (string.IsNullOrWhiteSpace(path))
      return;
    try {
      var fi = new FileInfo(path);
      if (fi.Directory is { } dir) {
        var stem = Path.GetFileNameWithoutExtension(fi.Name);
        outBox.Text = Path.Combine(dir.FullName, $"{stem}-frames");
      }
    } catch {
      // path not yet a valid file — ignore.
    }
  }

  private async void OnBrowseOutputClick(object? sender, RoutedEventArgs e) {
    var picker = new AvaloniaFolderPicker();
    var initial = this.FindControl<TextBox>("OutputPathBox")?.Text;
    var chosen = await picker.PickFolderAsync("Select output folder", initial);
    if (chosen != null && this.FindControl<TextBox>("OutputPathBox") is { } box)
      box.Text = chosen;
  }

  private async void OnExtractClick(object? sender, RoutedEventArgs e) {
    if (!FfmpegLocator.IsAvailable()) {
      this.SetStatus("ffmpeg not found — install it first.");
      this.RefreshFfmpegBanner();
      return;
    }

    var videoPath = this.FindControl<TextBox>("VideoPathBox")?.Text;
    if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) {
      this.SetStatus("Pick an existing video file first.");
      return;
    }
    var outPath = this.FindControl<TextBox>("OutputPathBox")?.Text;
    if (string.IsNullOrWhiteSpace(outPath)) {
      this.SetStatus("Pick an output folder first.");
      return;
    }

    if (!this.TryParseOptions(out var options, out var error)) {
      this.SetStatus(error);
      return;
    }

    var video = new FileInfo(videoPath);
    var outputDir = new DirectoryInfo(outPath);

    this.ToggleRunningUi(running: true);
    if (this.FindControl<ItemsControl>("FrameLog") is { } log)
      log.ItemsSource = Array.Empty<string>();
    this.SetProgress(0);
    this.SetStatus("Running ffmpeg...");

    this._runCts = new CancellationTokenSource();
    var token = this._runCts.Token;

    var progress = new Progress<int>(frame => {
      Dispatcher.UIThread.Post(() => {
        this.SetStatus(string.Create(CultureInfo.InvariantCulture, $"Extracted {frame} frame(s)..."));
        // Indeterminate-style fill: cap at 99 until we get the
        // success signal below. Beats no progress at all.
        if (frame > 0)
          this.SetProgress(Math.Min(99, frame % 100));
      });
    });

    try {
      var extractor = new VideoFrameExtractor();
      var frames = await extractor.ExtractAsync(video, outputDir, options, progress, token);
      this._lastFrames = frames.ToList();
      this._lastOutputDir = outputDir;

      this.SetProgress(100);
      this.SetStatus(string.Create(CultureInfo.InvariantCulture, $"Done — {frames.Count} frame(s) written to {outputDir.FullName}"));
      this.PopulateFrameLog(frames);

      this.SetButtonEnabled("OpenFolderButton", true);
      this.SetButtonEnabled("SendPanoramaButton", true);
      this.SetButtonEnabled("SendSphericalButton", true);

      this.FramesReady?.Invoke(frames);
      if (this._autoCloseOnSuccess && frames.Count > 0)
        this.Close();
    } catch (OperationCanceledException) {
      this.SetStatus("Cancelled.");
    } catch (Exception ex) {
      this.SetStatus($"Failed: {ex.Message}");
    } finally {
      this._runCts?.Dispose();
      this._runCts = null;
      this.ToggleRunningUi(running: false);
    }
  }

  private void OnCancelClick(object? sender, RoutedEventArgs e) {
    this._runCts?.Cancel();
  }

  private void OnOpenOutputClick(object? sender, RoutedEventArgs e) {
    if (this._lastOutputDir is { Exists: true } dir) {
      try {
        ShellLauncher.OpenInDefaultViewer(dir.FullName);
      } catch (Exception ex) {
        this.SetStatus($"Open failed: {ex.Message}");
      }
    }
  }

  private void OnSendToPanoramaClick(object? sender, RoutedEventArgs e) {
    if (this._lastFrames.Count == 0) {
      this.SetStatus("Run extract first.");
      return;
    }
    this.FramesReady?.Invoke(this._lastFrames);
    this.SetStatus($"Sent {this._lastFrames.Count} frame(s) to the panorama stitcher.");
  }

  private void OnSendToSphericalClick(object? sender, RoutedEventArgs e) {
    // The 360° spherical stitcher is on a parallel branch; until it
    // lands fall back to the standard panorama hand-off.
    this.OnSendToPanoramaClick(sender, e);
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private bool TryParseOptions(out VideoExtractOptions options, out string error) {
    options = new VideoExtractOptions();
    error = string.Empty;

    var inv = CultureInfo.InvariantCulture;
    var fpsValue = this.FindControl<Slider>("FpsSlider")?.Value ?? 2.0;
    var qValue = (int)Math.Round(this.FindControl<Slider>("JpegQualitySlider")?.Value ?? 2);

    int? maxEdge = null;
    if (this.FindControl<ComboBox>("MaxEdgeCombo")?.SelectedItem is ComboBoxItem ci) {
      var tag = ci.Tag?.ToString();
      if (int.TryParse(tag, NumberStyles.Integer, inv, out var px) && px > 0)
        maxEdge = px;
    }

    TimeSpan? start = null;
    var startText = this.FindControl<TextBox>("StartTimeBox")?.Text;
    if (!string.IsNullOrWhiteSpace(startText)) {
      if (!TryParseTime(startText, out var s)) {
        error = $"Bad start time '{startText}' — use hh:mm:ss[.fff].";
        return false;
      }
      start = s;
    }
    TimeSpan? end = null;
    var endText = this.FindControl<TextBox>("EndTimeBox")?.Text;
    if (!string.IsNullOrWhiteSpace(endText)) {
      if (!TryParseTime(endText, out var ee)) {
        error = $"Bad end time '{endText}' — use hh:mm:ss[.fff].";
        return false;
      }
      end = ee;
    }

    options = new VideoExtractOptions {
      Fps = fpsValue,
      JpegQuality = qValue,
      MaxLongEdge = maxEdge,
      StartTime = start,
      EndTime = end
    };
    return true;
  }

  private static bool TryParseTime(string text, out TimeSpan ts) {
    var inv = CultureInfo.InvariantCulture;
    string[] formats = ["hh\\:mm\\:ss", "hh\\:mm\\:ss\\.fff", "h\\:mm\\:ss", "mm\\:ss", "m\\:ss", "ss"];
    if (TimeSpan.TryParseExact(text.Trim(), formats, inv, out ts))
      return true;
    return TimeSpan.TryParse(text.Trim(), inv, out ts);
  }

  private void PopulateFrameLog(IReadOnlyList<FileInfo> frames) {
    if (this.FindControl<ItemsControl>("FrameLog") is not { } list)
      return;
    var rows = frames.Take(40).Select(f => f.Name).ToList();
    if (frames.Count > rows.Count)
      rows.Add($"... and {frames.Count - rows.Count} more");
    list.ItemsSource = rows;
  }

  private void ToggleRunningUi(bool running) {
    this.SetButtonEnabled("ExtractButton", !running && FfmpegLocator.IsAvailable());
    if (this.FindControl<Button>("CancelButton") is { } cancel)
      cancel.IsVisible = running;
  }

  private void SetButtonEnabled(string name, bool enabled) {
    if (this.FindControl<Button>(name) is { } btn)
      btn.IsEnabled = enabled;
  }

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = message;
  }

  private void SetProgress(int value) {
    if (this.FindControl<ProgressBar>("ExtractProgress") is { } p)
      p.Value = value;
  }
}
