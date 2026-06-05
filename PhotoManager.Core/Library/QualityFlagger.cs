using Hawkynt.PhotoManager.Core.Previews;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.Core.Library;

/// <summary>
/// Aggregated quality result for a single image. Each boolean is what the UI
/// surfaces directly; the raw scores are kept for tooltips / sorting / future
/// thresholds without a re-analysis pass.
/// </summary>
public readonly record struct QualityResult(
  bool IsBlurry,
  bool IsOverexposed,
  bool IsUnderexposed,
  double SharpnessScore,
  double ClippedHighlightFraction,
  double ClippedShadowFraction
);

/// <summary>
/// Flags photos that look soft (blurry) or have crushed shadows / clipped
/// highlights, by analysing a downsampled grayscale preview.
///
/// Sharpness uses Laplacian-variance — convolve the preview with a 3×3
/// laplacian kernel and report the variance of the response. A cleanly
/// focused photo with edges & texture lands well over 1000; an out-of-focus
/// one collapses near zero. The 100 cutoff catches the obviously soft cases
/// while leaving normal natural-light images alone (mirrors OpenCV's
/// classic blurry-image recipe).
///
/// Exposure uses a histogram on the grayscale preview: pixels at the
/// extremes are counted as clipped if any channel ≥ 250 (highlights) or
/// ≤ 5 (shadows). 1% of pixels in either bucket trips the corresponding
/// flag — small enough to fire on visibly bad exposures, large enough to
/// ignore the few stray hot pixels every CMOS sensor produces.
/// </summary>
public sealed class QualityFlagger {
  /// <summary>Laplacian-variance threshold — below this value the image is flagged as blurry.</summary>
  public const double BlurVarianceThreshold = 100.0;

  /// <summary>Pixel-channel value at or above which a pixel is considered a clipped highlight.</summary>
  public const byte HighlightClipValue = 250;

  /// <summary>Pixel-channel value at or below which a pixel is considered a crushed shadow.</summary>
  public const byte ShadowClipValue = 5;

  /// <summary>Fraction-of-pixels threshold for the over/under-exposure flags.</summary>
  public const double ExposureClipThreshold = 0.01;

  /// <summary>Long edge of the downsampled preview used for analysis. Keeps work bounded on RAW & big JPEGs.</summary>
  public const int AnalysisLongEdgePixels = 512;

  public async Task<QualityResult> AnalyseAsync(FileInfo imageFile, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(imageFile);
    if (!imageFile.Exists)
      return default;

    using var image = await LoadDownsampledAsync(imageFile, cancellationToken);
    if (image is null)
      return default;

    return AnalyseImage(image);
  }

  /// <summary>Synchronous, FileInfo-only entry point per the public contract.</summary>
  public QualityResult Analyse(FileInfo file) =>
    this.AnalyseAsync(file).GetAwaiter().GetResult();

  internal static QualityResult AnalyseImage(Image<L8> image) {
    var width = image.Width;
    var height = image.Height;
    if (width < 3 || height < 3)
      return default;

    var pixels = new byte[width * height];
    image.CopyPixelDataTo(pixels);

    var (varianceSum, count) = LaplacianStats(pixels, width, height);
    var laplacianVariance = count > 0 ? varianceSum / count : 0;

    var (highlightFrac, shadowFrac) = ExposureFractions(pixels);

    return new QualityResult(
      IsBlurry: laplacianVariance < BlurVarianceThreshold,
      IsOverexposed: highlightFrac >= ExposureClipThreshold,
      IsUnderexposed: shadowFrac >= ExposureClipThreshold,
      SharpnessScore: laplacianVariance,
      ClippedHighlightFraction: highlightFrac,
      ClippedShadowFraction: shadowFrac
    );
  }

  private static (double VarianceSum, int Count) LaplacianStats(byte[] pixels, int width, int height) {
    var inner = (width - 2) * (height - 2);
    if (inner == 0)
      return (0, 0);

    // Welford-ish two-pass: walk the inner pixels twice (once for mean, once
    // for variance). Avoids allocating an intermediate response[] array on
    // every analyse call — relevant when the user scans thousands of files.
    var sum = 0.0;
    for (var y = 1; y < height - 1; y++) {
      var rowAbove = (y - 1) * width;
      var row = y * width;
      var rowBelow = (y + 1) * width;
      for (var x = 1; x < width - 1; x++) {
        var v = (double)(pixels[rowAbove + x] + pixels[rowBelow + x] + pixels[row + x - 1] + pixels[row + x + 1])
                - 4.0 * pixels[row + x];
        sum += v;
      }
    }
    var mean = sum / inner;

    var varSum = 0.0;
    for (var y = 1; y < height - 1; y++) {
      var rowAbove = (y - 1) * width;
      var row = y * width;
      var rowBelow = (y + 1) * width;
      for (var x = 1; x < width - 1; x++) {
        var v = (double)(pixels[rowAbove + x] + pixels[rowBelow + x] + pixels[row + x - 1] + pixels[row + x + 1])
                - 4.0 * pixels[row + x];
        var d = v - mean;
        varSum += d * d;
      }
    }

    return (varSum, inner);
  }

  private static (double HighlightFraction, double ShadowFraction) ExposureFractions(byte[] pixels) {
    var total = pixels.Length;
    if (total == 0)
      return (0, 0);

    var highlights = 0;
    var shadows = 0;
    for (var i = 0; i < total; i++) {
      var p = pixels[i];
      if (p >= HighlightClipValue)
        highlights++;
      else if (p <= ShadowClipValue)
        shadows++;
    }

    return ((double)highlights / total, (double)shadows / total);
  }

  /// <summary>
  /// Loads <paramref name="imageFile"/> as a grayscale L8 image scaled so the
  /// long edge ≤ <see cref="AnalysisLongEdgePixels"/>. Reuses
  /// <see cref="RawPreviewExtractor"/> so RAW files come through their
  /// embedded JPEG preview rather than the mosaic data — same trick as
  /// <see cref="RegionThumbnailExtractor"/>.
  /// </summary>
  private static async Task<Image<L8>?> LoadDownsampledAsync(FileInfo imageFile, CancellationToken cancellationToken) {
    Image<L8>? loaded = null;
    try {
      var raw = await RawPreviewExtractor.ExtractLargestJpegAsync(imageFile, cancellationToken);
      if (raw is not null) {
        try {
          using var ms = new MemoryStream(raw, writable: false);
          loaded = await Image.LoadAsync<L8>(ms, cancellationToken);
        } catch {
          // Embedded preview unreadable; fall back to direct decode.
        }
      }

      loaded ??= await Image.LoadAsync<L8>(imageFile.FullName, cancellationToken);

      var longest = Math.Max(loaded.Width, loaded.Height);
      if (longest > AnalysisLongEdgePixels) {
        var scale = (double)AnalysisLongEdgePixels / longest;
        var newW = Math.Max(3, (int)(loaded.Width * scale));
        var newH = Math.Max(3, (int)(loaded.Height * scale));
        loaded.Mutate(c => c.Resize(newW, newH));
      }

      return loaded;
    } catch {
      loaded?.Dispose();
      return null;
    }
  }
}
