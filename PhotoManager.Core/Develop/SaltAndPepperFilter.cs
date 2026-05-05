using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Develop;

/// <summary>
/// Adaptive median filter that removes salt-and-pepper noise (the dust
/// speckles, white spots, and isolated dark dots that pepper old photo
/// scans) without blurring fine detail.
///
/// Pure C# — no ML model needed. Salt-and-pepper noise is fundamentally
/// a classical-DSP problem: pixels whose value is wildly out of step
/// with their neighbours' median. A regular median filter handles this
/// but ALSO smooths real fine detail (eyes, fur, texture). The
/// "adaptive" part: only replace a pixel when its value differs from
/// the local median by more than <see cref="SaltAndPepperOptions.NoiseThreshold"/>
/// — so genuine detail is left alone, but the obvious speckles are
/// cleaned up. Window size grows from 3×3 up to <see cref="SaltAndPepperOptions.MaxWindow"/>
/// for pixels that fail the test at the smaller window (so a tight
/// cluster of bad pixels still gets fixed).
/// </summary>
public static class SaltAndPepperFilter {
  /// <summary>
  /// Returns a freshly allocated image with salt-and-pepper noise
  /// removed. The input is not mutated.
  /// </summary>
  public static Image<Rgba32> Filter(Image<Rgba32> source, SaltAndPepperOptions? options = null) {
    ArgumentNullException.ThrowIfNull(source);
    options ??= new SaltAndPepperOptions();
    var w = source.Width;
    var h = source.Height;

    // Materialise the source's R/G/B planes into flat byte[]s — random
    // access via ImageSharp accessors per-pixel would dominate runtime.
    var r = new byte[w * h];
    var g = new byte[w * h];
    var b = new byte[w * h];
    var a = new byte[w * h];
    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        var off = y * w;
        for (var x = 0; x < row.Length; x++) {
          r[off + x] = row[x].R;
          g[off + x] = row[x].G;
          b[off + x] = row[x].B;
          a[off + x] = row[x].A;
        }
      }
    });

    // Process each channel independently — RGB salt-and-pepper noise
    // (e.g. CRT phosphor smear, scanner CCD hot pixels) shows up
    // per-channel, not joint.
    var rOut = AdaptiveMedian(r, w, h, options);
    var gOut = AdaptiveMedian(g, w, h, options);
    var bOut = AdaptiveMedian(b, w, h, options);

    // Reassemble.
    var output = new Image<Rgba32>(w, h);
    output.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        var off = y * w;
        for (var x = 0; x < row.Length; x++)
          row[x] = new Rgba32(rOut[off + x], gOut[off + x], bOut[off + x], a[off + x]);
      }
    });
    return output;
  }

  /// <summary>
  /// Per-channel adaptive median. For each pixel, compute the median
  /// of its 3×3 neighbourhood; if the pixel deviates from that median
  /// by more than the noise threshold, mark for replacement.
  /// Pixels still above threshold at 3×3 retry at increasing window
  /// sizes (5×5, 7×7, …, up to MaxWindow) — handles tight clusters
  /// of dust speckles that a single 3×3 wouldn't catch.
  /// </summary>
  private static byte[] AdaptiveMedian(byte[] src, int w, int h, SaltAndPepperOptions options) {
    var dst = (byte[])src.Clone();
    var threshold = options.NoiseThreshold;

    for (var window = 3; window <= options.MaxWindow; window += 2) {
      var halfWin = window / 2;
      var newReplaced = 0;
      for (var y = 0; y < h; y++) {
        for (var x = 0; x < w; x++) {
          var off = y * w + x;
          var median = ComputeMedian(dst, w, h, x, y, halfWin);
          if (Math.Abs(dst[off] - median) > threshold) {
            // Within this window the pixel still looks like noise —
            // only commit the replacement if it's "extreme" (true
            // salt-and-pepper) at any window size.
            dst[off] = median;
            newReplaced++;
          }
        }
      }
      // No more replacements at this window — done.
      if (newReplaced == 0)
        break;
    }
    return dst;
  }

  /// <summary>
  /// Median over the (2*halfWin+1)² neighbourhood centred at (cx, cy),
  /// clamped to the image bounds. Quickselect would be faster but the
  /// max window is small enough (≤9×9 = 81 values) that an in-place
  /// sort of a stackalloc'd buffer is fast enough.
  /// </summary>
  private static byte ComputeMedian(byte[] src, int w, int h, int cx, int cy, int halfWin) {
    Span<byte> buffer = stackalloc byte[(2 * halfWin + 1) * (2 * halfWin + 1)];
    var count = 0;
    var minY = Math.Max(0, cy - halfWin);
    var maxY = Math.Min(h - 1, cy + halfWin);
    var minX = Math.Max(0, cx - halfWin);
    var maxX = Math.Min(w - 1, cx + halfWin);
    for (var y = minY; y <= maxY; y++)
      for (var x = minX; x <= maxX; x++)
        buffer[count++] = src[y * w + x];
    var slice = buffer[..count];
    SortInsertion(slice);
    return slice[count / 2];
  }

  /// <summary>Tiny insertion sort — faster than Array.Sort for ≤81 bytes (no heap-alloc, no comparer dispatch).</summary>
  private static void SortInsertion(Span<byte> data) {
    for (var i = 1; i < data.Length; i++) {
      var v = data[i];
      var j = i - 1;
      while (j >= 0 && data[j] > v) {
        data[j + 1] = data[j];
        j--;
      }
      data[j + 1] = v;
    }
  }
}

/// <summary>
/// Tunables for <see cref="SaltAndPepperFilter.Filter"/>. Defaults
/// are calibrated against typical dust / speckle severity in scanned
/// old photos.
/// </summary>
public sealed record SaltAndPepperOptions {
  /// <summary>
  /// Pixels deviating from their local median by more than this many
  /// luma units get replaced with the median. Higher = only replace
  /// extreme outliers (preserves more detail); lower = more aggressive
  /// despeckling.
  /// </summary>
  public int NoiseThreshold { get; init; } = 35;

  /// <summary>
  /// Largest window (in pixels) the adaptive pass tries. Bigger
  /// windows catch larger speckles but risk smoothing genuine small
  /// features. 7 catches 1–3-pixel dust; 9 catches up to 5-pixel
  /// speckles but starts touching small detail.
  /// </summary>
  public int MaxWindow { get; init; } = 7;
}
