using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Picks the best (width, height, quality) tuple for an embedded JPEG
/// thumbnail under a hard byte-budget — usually the JPEG APP1 cap minus
/// the cost of the surrounding EXIF IFDs. Search starts at the user's
/// requested values and steps down both axes; among combinations that
/// fit, the one with the highest PSNR vs the developed source wins.
/// </summary>
public static class ThumbnailFitter {
  /// <summary>One scored candidate that fits the byte budget.</summary>
  public sealed record Fit(byte[] JpegBytes, int Width, int Height, int Quality, double PsnrDb);

  /// <summary>Stretched search grid — relative scales applied to the user's max-edge request.</summary>
  private static readonly double[] DimensionScales = { 1.00, 0.90, 0.80, 0.70, 0.60, 0.50, 0.40, 0.30, 0.20 };

  /// <summary>Quality stepping. Walks down by 5 until 25 — anything below that loses real visual quality.</summary>
  private static readonly int[] QualityDeltas = { 0, -5, -10, -15, -20, -30, -40, -50 };

  /// <summary>
  /// Encode <paramref name="source"/> as a JPEG that fits in
  /// <paramref name="budgetBytes"/> bytes, preferring the requested
  /// dimensions and quality. Returns null when even the smallest probe
  /// (~30% of requested edge, quality 25) is over budget.
  /// </summary>
  public static Fit? FindBestFit(Image<Rgba32> source, int requestedLongEdge, int requestedQuality, int budgetBytes) {
    ArgumentNullException.ThrowIfNull(source);
    if (budgetBytes <= 0)
      return null;

    var aspect = (double)source.Width / source.Height;
    Fit? best = null;

    foreach (var scale in DimensionScales) {
      var longEdge = Math.Max(16, (int)Math.Round(requestedLongEdge * scale));
      var (w, h) = source.Width >= source.Height
        ? (longEdge, Math.Max(1, (int)Math.Round(longEdge / aspect)))
        : (Math.Max(1, (int)Math.Round(longEdge * aspect)), longEdge);

      using var resized = source.Clone(c => c.Resize(w, h));

      foreach (var delta in QualityDeltas) {
        var q = Math.Clamp(requestedQuality + delta, 25, 100);
        if (delta < 0 && q == requestedQuality)
          continue;  // already covered at delta == 0

        using var ms = new MemoryStream();
        resized.SaveAsJpeg(ms, new JpegEncoder { Quality = q });
        var bytes = ms.ToArray();
        if (bytes.Length > budgetBytes)
          continue;

        var psnr = ComputePsnr(resized, bytes);
        if (best is null || psnr > best.PsnrDb)
          best = new Fit(bytes, w, h, q, psnr);
      }
    }

    return best;
  }

  /// <summary>
  /// Standard PSNR (peak signal-to-noise ratio) in dB between
  /// <paramref name="reference"/> and the JPEG-decoded
  /// <paramref name="encodedJpeg"/>. Returns positive infinity when
  /// the two are bit-identical and ~0 dB for unrelated images. Higher
  /// is better; ~30+ dB is usually visually lossless for our purposes.
  /// </summary>
  public static double ComputePsnr(Image<Rgba32> reference, byte[] encodedJpeg) {
    using var roundtrip = Image.Load<Rgba32>(encodedJpeg);
    if (roundtrip.Width != reference.Width || roundtrip.Height != reference.Height)
      return double.NegativeInfinity;

    // Snapshot both images into flat Rgba32 arrays so we can iterate them
    // simultaneously without juggling two ProcessPixelRows accessors.
    var w = reference.Width;
    var h = reference.Height;
    var refPixels = new Rgba32[w * h];
    var rtPixels = new Rgba32[w * h];
    reference.CopyPixelDataTo(refPixels);
    roundtrip.CopyPixelDataTo(rtPixels);

    var sumSquared = 0L;
    for (var i = 0; i < refPixels.Length; i++) {
      var r = refPixels[i].R - rtPixels[i].R;
      var g = refPixels[i].G - rtPixels[i].G;
      var b = refPixels[i].B - rtPixels[i].B;
      sumSquared += r * r + g * g + b * b;
    }
    var pixelCount = (long)refPixels.Length * 3;

    if (pixelCount == 0 || sumSquared == 0)
      return double.PositiveInfinity;

    var mse = sumSquared / (double)pixelCount;
    return 20 * Math.Log10(255.0 / Math.Sqrt(mse));
  }
}
