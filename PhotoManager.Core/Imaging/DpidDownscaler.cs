using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Imaging;

/// <summary>
/// Detail-Preserving Image Downscaling (Weber, Aliaga, Liang;
/// Eurographics 2016). Two-pass content-aware downscale that
/// preserves edges, text, and thin features which Lanczos / Box
/// average out into mush. The cost vs Box is ~3–5×, paid for by
/// noticeably sharper thumbnails on detailed sources (UI icons,
/// product shots, scanned text, fine landscape texture).
///
/// Algorithm:
///   1. For each output pixel, define the corresponding input patch.
///   2. Compute the patch's box average <c>avg</c>.
///   3. Re-average the patch with weights that grow with the per-pixel
///      colour distance to <c>avg</c> (perceptually distinctive pixels
///      dominate). The exponent <c>lambda</c> controls how aggressively
///      detail is preserved; the paper recommends 1.0 for natural
///      images, 0.5 for noisy sources, 2.0 for very detail-heavy ones.
///
/// Only supports downscale (one or both target dimensions strictly
/// less than the source). Upscale callers should keep using
/// <see cref="ThumbnailManager"/>'s Bicubic / Triangle path.
/// </summary>
public static class DpidDownscaler {
  /// <summary>Default detail-preservation exponent. Matches the Weber paper's natural-image recommendation.</summary>
  public const double DefaultLambda = 1.0;

  /// <summary>Downscale <paramref name="source"/> to (<paramref name="targetW"/>,
  /// <paramref name="targetH"/>) using DPID. Returns a freshly allocated
  /// image; caller owns it. Source is not mutated.</summary>
  /// <param name="lambda">Detail-preservation exponent. Higher = stronger detail
  /// preservation (and more noise amplification on grainy sources). Clamped to
  /// [0, 10] to avoid numerical issues.</param>
  public static Image<Rgba32> Downscale(Image<Rgba32> source, int targetW, int targetH, double lambda = DefaultLambda) {
    ArgumentNullException.ThrowIfNull(source);
    if (targetW <= 0 || targetH <= 0)
      throw new ArgumentOutOfRangeException(nameof(targetW), $"Target dimensions must be positive ({targetW}x{targetH}).");
    var srcW = source.Width;
    var srcH = source.Height;
    if (targetW > srcW || targetH > srcH)
      throw new ArgumentException($"DPID only supports downscale: source {srcW}x{srcH}, target {targetW}x{targetH}.");

    lambda = Math.Clamp(lambda, 0.0, 10.0);

    // Snapshot source pixels into a flat array — ImageSharp's per-image
    // accessor isn't safe to share across the row-parallel inner loop.
    var srcPixels = new Rgba32[srcW * srcH];
    source.CopyPixelDataTo(srcPixels);

    var output = new Image<Rgba32>(targetW, targetH);
    var scaleX = (double)srcW / targetW;
    var scaleY = (double)srcH / targetH;

    // Each output row is independent — DPID is embarrassingly parallel.
    var rowBuffers = new Rgba32[targetH][];
    Parallel.For(0, targetH, y => {
      var row = new Rgba32[targetW];
      var srcY0 = y * scaleY;
      var srcY1 = (y + 1) * scaleY;
      var iy0 = (int)Math.Floor(srcY0);
      var iy1 = Math.Min(srcH, (int)Math.Ceiling(srcY1));
      for (var x = 0; x < targetW; x++) {
        var srcX0 = x * scaleX;
        var srcX1 = (x + 1) * scaleX;
        var ix0 = (int)Math.Floor(srcX0);
        var ix1 = Math.Min(srcW, (int)Math.Ceiling(srcX1));
        row[x] = ComputePixel(srcPixels, srcW, ix0, iy0, ix1, iy1, lambda);
      }
      rowBuffers[y] = row;
    });

    output.ProcessPixelRows(accessor => {
      for (var y = 0; y < targetH; y++)
        rowBuffers[y].AsSpan().CopyTo(accessor.GetRowSpan(y));
    });
    return output;
  }

  /// <summary>
  /// Two-pass DPID core for a single output pixel. Pass 1 = box mean,
  /// pass 2 = distance-weighted mean. Patch is the half-open rectangle
  /// [ix0,ix1) × [iy0,iy1); guaranteed non-empty by the caller.
  /// </summary>
  private static Rgba32 ComputePixel(Rgba32[] src, int srcW, int ix0, int iy0, int ix1, int iy1, double lambda) {
    long sumR = 0, sumG = 0, sumB = 0;
    long count = 0;
    for (var py = iy0; py < iy1; py++) {
      var rowOff = py * srcW;
      for (var px = ix0; px < ix1; px++) {
        var p = src[rowOff + px];
        sumR += p.R; sumG += p.G; sumB += p.B;
        count++;
      }
    }
    if (count == 0)
      return new Rgba32((byte)0, (byte)0, (byte)0, (byte)255);

    var avgR = sumR / (double)count;
    var avgG = sumG / (double)count;
    var avgB = sumB / (double)count;

    // Pass 2 — re-weight by perceptual distance to the patch mean.
    double wSumR = 0, wSumG = 0, wSumB = 0;
    double wSum = 0;
    for (var py = iy0; py < iy1; py++) {
      var rowOff = py * srcW;
      for (var px = ix0; px < ix1; px++) {
        var p = src[rowOff + px];
        var dr = p.R - avgR;
        var dg = p.G - avgG;
        var db = p.B - avgB;
        // Euclidean colour distance, then raised to lambda. Skip the
        // pow() call for the very common lambda=1.0 case.
        var dist = Math.Sqrt(dr * dr + dg * dg + db * db);
        var w = lambda == 1.0 ? dist : Math.Pow(dist, lambda);
        // Pixels identical to the mean would get weight 0 and disappear
        // from the average. Add a small epsilon so a perfectly-uniform
        // patch still produces the box mean rather than NaN/0.
        w += 1e-6;
        wSumR += w * p.R;
        wSumG += w * p.G;
        wSumB += w * p.B;
        wSum += w;
      }
    }
    return new Rgba32(
      ClampByte(wSumR / wSum),
      ClampByte(wSumG / wSum),
      ClampByte(wSumB / wSum),
      (byte)255);
  }

  private static byte ClampByte(double v) {
    if (v <= 0.0) return 0;
    if (v >= 255.0) return 255;
    return (byte)Math.Round(v);
  }
}
