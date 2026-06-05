using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Hawkynt.PhotoManager.Core.Develop;
using Hawkynt.PhotoManager.Core.Stitching;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.UI.Views;

public partial class PieceStitchWindow : Window {
  private FileInfo? _sourceFile;
  private Image<Rgba32>? _sourceImage;
  private Image<Rgba32>? _assembled;

  // Parameterless constructor exists only for Avalonia's XAML loader /
  // design-time tooling. Runtime always uses the FileInfo overload.
  public PieceStitchWindow() {
    this.InitializeComponent();
    this.Closed += (_, _) => {
      this._sourceImage?.Dispose();
      this._sourceImage = null;
      this._assembled?.Dispose();
      this._assembled = null;
    };
  }

  public PieceStitchWindow(FileInfo seed) : this() {
    Dispatcher.UIThread.Post(async () => await this.LoadSourceAsync(seed), DispatcherPriority.Background);
  }

  private async Task LoadSourceAsync(FileInfo file) {
    if (!file.Exists) {
      this.SetStatus("Source file not found.");
      return;
    }
    this.SetStatus($"Loading {file.Name}…");
    try {
      // Route through RawImageLoader so AVIF / HEIC / JPEG2000 / RAW /
      // PSD / etc. all decode correctly. The previous direct ImageSharp
      // load only handled JPG/PNG/TIFF/BMP/WebP and silently failed on
      // every modern container the rest of PhotoManager already supports.
      var img = await RawImageLoader.LoadAsync(file);
      this._sourceImage?.Dispose();
      this._sourceImage = img;
      this._sourceFile = file;
      await this.UpdatePreviewAsync(this.FindControl<Avalonia.Controls.Image>("SourcePreview"), img);
      this.SetStatus($"Loaded {file.Name} ({img.Width}×{img.Height}).");
    } catch (Exception ex) {
      this.SetStatus($"Couldn't load {file.Name}: {ex.Message}");
    }
  }

  private async void OnRunClick(object? sender, RoutedEventArgs e) {
    if (this._sourceImage is null) {
      this.SetStatus("Open a scan first.");
      return;
    }
    var bgIndex = this.FindControl<ComboBox>("BackgroundCombo")?.SelectedIndex ?? 0;
    var background = bgIndex switch {
      1 => ScannerBackground.White,
      2 => ScannerBackground.Black,
      _ => ScannerBackground.Auto
    };
    var sourceSnapshot = this._sourceImage;

    this.SetProgress(5);
    this.SetStatus("Detecting pieces…");
    PieceStitchResult result;
    try {
      // The new pipeline always assembles. Caller doesn't need to pre-decide
      // "just segment vs. segment + stitch" — both panes refresh from the
      // same result, and an empty pieces list short-circuits to a no-op
      // canvas anyway.
      result = await Task.Run(() => PieceStitcher.Run(sourceSnapshot, background));
    } catch (Exception ex) {
      this.SetProgress(0);
      this.SetStatus($"Stitch failed: {ex.Message}");
      return;
    }

    this.SetProgress(70);
    this.SetStatus($"Detected {result.Pieces.Count} piece(s); placed {result.Placed.Count}, unplaced {result.Unplaced.Count}.");
    using (var overlay = await Task.Run(() => DrawOverlay(sourceSnapshot, result.Pieces)))
      await this.UpdatePreviewAsync(this.FindControl<Avalonia.Controls.Image>("SourcePreview"), overlay);

    this._assembled?.Dispose();
    this._assembled = result.Canvas;
    foreach (var p in result.Pieces)
      p.Image.Dispose();

    if (result.Pieces.Count == 0) {
      this.SetStatus("No pieces detected — the scan looks uniform. Pick a different background colour or check the threshold.");
    } else {
      await this.UpdatePreviewAsync(this.FindControl<Avalonia.Controls.Image>("OutputPreview"), this._assembled);
      this.SetStatus($"Assembled {result.Placed.Count}/{result.Pieces.Count} piece(s) into {this._assembled.Width}×{this._assembled.Height} output.");
    }
    this.SetProgress(100);
  }

  private static Image<Rgba32> DrawOverlay(Image<Rgba32> source, IReadOnlyList<DetectedPiece> pieces) {
    var overlay = source.Clone();
    var pen = Pens.Solid(SixLabors.ImageSharp.Color.Magenta, 2f);
    overlay.Mutate(ctx => {
      foreach (var p in pieces) {
        var b = p.BoundingBox;
        var poly = new SixLabors.ImageSharp.Drawing.RectangularPolygon(b.X, b.Y, b.Width, b.Height);
        ctx.Draw(pen, poly);
      }
    });
    return overlay;
  }

  private async void OnSaveAsClick(object? sender, RoutedEventArgs e) {
    if (this._assembled is null) {
      this.SetStatus("Run the pipeline first — there's nothing to save yet.");
      return;
    }
    var topLevel = TopLevel.GetTopLevel(this);
    if (topLevel?.StorageProvider is not { CanSave: true } storage) {
      this.SetStatus("Save picker unavailable.");
      return;
    }
    var suggested = (this._sourceFile is null
      ? "stitched"
      : Path.GetFileNameWithoutExtension(this._sourceFile.Name)) + "_stitched.png";
    var picked = await storage.SaveFilePickerAsync(new FilePickerSaveOptions {
      Title = "Save assembled image",
      SuggestedFileName = suggested,
      DefaultExtension = "png",
      FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }]
    });
    if (picked is null)
      return;
    var dest = picked.TryGetLocalPath();
    if (string.IsNullOrWhiteSpace(dest)) {
      this.SetStatus("Couldn't resolve save path.");
      return;
    }
    try {
      await this._assembled.SaveAsPngAsync(dest);
      this.SetStatus($"Saved {Path.GetFileName(dest)}.");
    } catch (Exception ex) {
      this.SetStatus($"Save failed: {ex.Message}");
    }
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private async Task UpdatePreviewAsync(Avalonia.Controls.Image? control, Image<Rgba32> source) {
    if (control is null)
      return;
    const int max = 800;
    var scale = Math.Min(1.0, (double)max / Math.Max(source.Width, source.Height));
    var w = Math.Max(1, (int)(source.Width * scale));
    var h = Math.Max(1, (int)(source.Height * scale));
    using var ms = new MemoryStream();
    using (var thumb = source.Clone(ctx => ctx.Resize(w, h)))
      await thumb.SaveAsPngAsync(ms);
    ms.Position = 0;
    var bmp = new Bitmap(ms);
    await Dispatcher.UIThread.InvokeAsync(() => control.Source = bmp);
  }

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = message;
  }

  private void SetProgress(double value) {
    if (this.FindControl<ProgressBar>("ProgressBar") is { } p)
      p.Value = value;
  }
}
