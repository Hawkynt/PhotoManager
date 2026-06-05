using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Cheap one-pass image-analysis helpers used by the "auto" toolbar
/// buttons in EditImageWindow. Each returns a new <see cref="DevelopSettings"/>
/// with the relevant fields populated; the caller decides whether to push
/// the result into the UI / re-render.
///
/// Algorithms are pragmatic — Sobel-style gradients, histograms, and
/// bounding boxes — not state-of-the-art. They cover the common
/// scanned-page / photographed-paper workflow without bringing in extra
/// dependencies.
/// </summary>
public static class AutoEnhancements {
  /// <summary>
  /// Returns the suggested deskew angle in degrees, in the range
  /// [-15, +15]. Higher rotation ranges aren't typically needed for
  /// scanned pages and noisier estimates would just produce false
  /// large rotations on landscape photographs.
  /// </summary>
  public static double DetectSkewAngleDegrees(Image<Rgba32> image) {
    ArgumentNullException.ThrowIfNull(image);
    var w = image.Width;
    var h = image.Height;
    if (w < 8 || h < 8) return 0;
    var pixels = new Rgba32[w * h];
    image.CopyPixelDataTo(pixels);

    // 0.5° histogram bins covering ±45° gives 180 buckets.
    const int bins = 180;
    var histogram = new long[bins];

    // We want the angle of the EDGE LINE (not the gradient direction);
    // edge angle is gradient angle ± 90°. We collapse the line angle
    // mod 90 and look near 0 / 90 for "this image's natural axis".
    for (var y = 1; y < h - 1; y++) {
      for (var x = 1; x < w - 1; x++) {
        var gx = LumOf(pixels[y * w + x + 1]) - LumOf(pixels[y * w + x - 1]);
        var gy = LumOf(pixels[(y + 1) * w + x]) - LumOf(pixels[(y - 1) * w + x]);
        var mag = Math.Sqrt(gx * gx + gy * gy);
        if (mag < 30) continue;  // ignore flat regions

        // Edge line angle in degrees, normalised to (-90, +90].
        var angle = Math.Atan2(gx, -gy) * 180.0 / Math.PI;
        while (angle <= -90) angle += 180;
        while (angle > 90)   angle -= 180;
        // Map (-90, +90] → 0..180 buckets.
        var idx = (int)((angle + 90) / 180.0 * bins);
        idx = Math.Clamp(idx, 0, bins - 1);
        histogram[idx]++;
      }
    }

    // Find the strongest peak within ±15° of horizontal (idx 90 == 0°).
    var bestIdx = bins / 2;
    var bestCount = 0L;
    var window = (int)Math.Round(15.0 / 180.0 * bins);
    for (var i = bins / 2 - window; i <= bins / 2 + window; i++) {
      if (histogram[i] > bestCount) {
        bestCount = histogram[i];
        bestIdx = i;
      }
    }
    if (bestCount == 0) return 0;
    var detected = (bestIdx - bins / 2) * 180.0 / bins;
    // Threshold: ignore tiny numerical noise.
    return Math.Abs(detected) < 0.25 ? 0 : Math.Clamp(detected, -15, 15);
  }

  /// <summary>
  /// Returns the (normalised) bounding box of the image's "active"
  /// region — the inner rectangle outside which everything is
  /// near-uniform background. Useful for trimming scanner whitespace
  /// or photographed-page borders.
  /// </summary>
  public static (double Left, double Top, double Right, double Bottom) DetectContentBounds(Image<Rgba32> image, byte gradientThreshold = 24) {
    ArgumentNullException.ThrowIfNull(image);
    var w = image.Width;
    var h = image.Height;
    if (w < 4 || h < 4) return (0, 0, 1, 1);
    var pixels = new Rgba32[w * h];
    image.CopyPixelDataTo(pixels);

    var rowHasContent = new bool[h];
    var colHasContent = new bool[w];
    for (var y = 1; y < h - 1; y++) {
      for (var x = 1; x < w - 1; x++) {
        var gx = Math.Abs(LumOf(pixels[y * w + x + 1]) - LumOf(pixels[y * w + x - 1]));
        var gy = Math.Abs(LumOf(pixels[(y + 1) * w + x]) - LumOf(pixels[(y - 1) * w + x]));
        if (gx + gy >= gradientThreshold) {
          rowHasContent[y] = true;
          colHasContent[x] = true;
        }
      }
    }

    var top = 0;
    while (top < h && !rowHasContent[top]) top++;
    var bottom = h - 1;
    while (bottom > top && !rowHasContent[bottom]) bottom--;
    var left = 0;
    while (left < w && !colHasContent[left]) left++;
    var right = w - 1;
    while (right > left && !colHasContent[right]) right--;

    // Add a small margin so the crop doesn't sit hard on the content.
    var marginX = (right - left) * 0.01;
    var marginY = (bottom - top) * 0.01;
    var l = Math.Max(0, (left - marginX) / w);
    var t = Math.Max(0, (top - marginY) / h);
    var r = Math.Min(1, (right + 1 + marginX) / w);
    var b = Math.Min(1, (bottom + 1 + marginY) / h);
    return (l, t, r, b);
  }

  /// <summary>Set <see cref="DevelopSettings.CropAngleDegrees"/> from the detected skew angle.</summary>
  public static DevelopSettings AutoLevel(DevelopSettings settings, Image<Rgba32> image) {
    ArgumentNullException.ThrowIfNull(settings);
    var angle = -DetectSkewAngleDegrees(image);  // negate: skew detected as +1° means rotate -1° to level
    return settings with { CropAngleDegrees = angle };
  }

  /// <summary>Set the four Crop edges from the detected content bounds.</summary>
  public static DevelopSettings AutoCrop(DevelopSettings settings, Image<Rgba32> image) {
    ArgumentNullException.ThrowIfNull(settings);
    var (l, t, r, b) = DetectContentBounds(image);
    return settings with { CropLeft = l, CropTop = t, CropRight = r, CropBottom = b };
  }

  /// <summary>
  /// Combine deskew + a soft perspective correction. Takes the skew angle
  /// as the in-plane rotation, then estimates vertical / horizontal
  /// keystone from the second-strongest peak in the gradient histogram.
  /// </summary>
  public static DevelopSettings UprightAuto(DevelopSettings settings, Image<Rgba32> image) {
    ArgumentNullException.ThrowIfNull(settings);
    var skew = DetectSkewAngleDegrees(image);
    return settings with {
      CropAngleDegrees = -skew,
      // PerspectiveAuto-detected keystone correction is ambitious for a
      // single-pass histogram; for now leave Vertical / Horizontal at 0
      // and just apply the rotation. Users who need keystone tweaks can
      // dial them on the Perspective panel.
      PerspectiveVertical = 0,
      PerspectiveHorizontal = 0
    };
  }

  private static double LumOf(Rgba32 px)
    => 0.299 * px.R + 0.587 * px.G + 0.114 * px.B;
}
