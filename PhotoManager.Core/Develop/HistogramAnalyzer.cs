using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Per-channel histogram counts and percentile lookups. The 4 channels are
/// sized 256 each — 8-bit precision is plenty for the editor's preview-
/// driven UI; full-bit-depth analysis isn't worth the memory footprint
/// when the source is already an 8-bit Rgba32.
/// </summary>
public sealed record ImageHistogram(int[] Red, int[] Green, int[] Blue, int[] Luminance, int TotalPixels) {
  /// <summary>
  /// Find the channel value at which <paramref name="fraction"/> of the
  /// pixels are at or below it. Used to detect blown / crushed regions
  /// for Auto-Tone (clip 0.5%, recover 5%, etc.).
  /// </summary>
  public static int Percentile(int[] channel, int totalPixels, double fraction) {
    if (channel == null || channel.Length == 0 || totalPixels == 0)
      return 0;
    var target = Math.Max(1, (long)(totalPixels * Math.Clamp(fraction, 0, 1)));
    long acc = 0;
    for (var i = 0; i < channel.Length; i++) {
      acc += channel[i];
      if (acc >= target)
        return i;
    }
    return channel.Length - 1;
  }
}

/// <summary>
/// Walks an Rgba32 image once and returns per-channel + luminance counts.
/// Cheap enough to call after every preview render so the histogram pane
/// stays in sync with the slider state.
/// </summary>
public static class HistogramAnalyzer {
  public static ImageHistogram Compute(Image<Rgba32> image) {
    ArgumentNullException.ThrowIfNull(image);
    var r = new int[256];
    var g = new int[256];
    var b = new int[256];
    var lum = new int[256];

    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var p = row[x];
          r[p.R]++;
          g[p.G]++;
          b[p.B]++;
          // Rec. 601 luminance via integer fixed-point: (77R + 150G + 29B) >> 8.
          // Eliminates the per-pixel double arithmetic + Math.Round that was
          // the dominant cost in the inner loop (~5× cheaper per pixel).
          // Max value: (77*255 + 150*255 + 29*255) >> 8 = 65280 >> 8 = 255. ✓
          lum[(77 * p.R + 150 * p.G + 29 * p.B) >> 8]++;
        }
      }
    });

    return new ImageHistogram(r, g, b, lum, image.Width * image.Height);
  }
}
