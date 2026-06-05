using Hawkynt.PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Bridges the user's brush-painted inpaint mask with <see cref="OnnxInpainter"/>.
/// Rasterises the normalised <see cref="BrushDab"/> cloud into a pixel mask,
/// dilates it by a small feather margin, and runs LaMa content-aware fill.
/// The result can be blended back non-destructively (each removal is one
/// undo entry in the UI).
/// </summary>
public static class ContentAwareFill {
  /// <summary>Default dilation radius in pixels applied to the brush mask
  /// before feeding it to LaMa. A small expansion ensures the inpainter
  /// sees a few pixels of clean context around every masked edge, which
  /// dramatically reduces halo artefacts.</summary>
  public const int DefaultDilationPx = 5;

  /// <summary>
  /// Rasterise the normalised brush dabs into a full-resolution binary
  /// mask image (R channel &ge; 128 = inpaint). The dab coordinates
  /// (<see cref="BrushDab.X"/>, <see cref="BrushDab.Y"/>,
  /// <see cref="BrushDab.Radius"/>) are normalised 0..1; this method
  /// maps them to <paramref name="width"/> x <paramref name="height"/>.
  /// </summary>
  public static Image<Rgba32> RasteriseMask(IReadOnlyList<BrushDab> dabs, int width, int height) {
    var mask = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 255));
    if (dabs is not { Count: > 0 })
      return mask;

    mask.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var py = (double)y / height;
        for (var x = 0; x < row.Length; x++) {
          var px = (double)x / width;
          var weight = 0.0;
          foreach (var dab in dabs) {
            if (Math.Abs(dab.Radius) < 1e-6) continue;
            var dx = px - dab.X;
            var dy = py - dab.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy) / dab.Radius;
            if (dist >= 1) continue;
            var falloff = 1 - dist * dist;
            weight = Math.Clamp(weight + falloff * dab.Flow, 0, 1);
          }
          if (weight > 0.5)
            row[x] = new Rgba32(255, 255, 255, 255);
        }
      }
    });

    return mask;
  }

  /// <summary>
  /// Dilate the mask in-place using a simple box-ball approximation.
  /// Every pixel whose R channel is &ge; 128 propagates out to its
  /// neighbours within <paramref name="radius"/> pixels (Chebyshev
  /// distance). This is intentionally simple and O(w*h*r) but the
  /// mask images are small (preview-sized) and r is tiny (5 px).
  /// </summary>
  public static void DilateMask(Image<Rgba32> mask, int radius) {
    if (radius <= 0) return;

    var w = mask.Width;
    var h = mask.Height;

    // Snapshot the current mask so we can read the original while writing.
    var original = new bool[w * h];
    mask.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var off = y * w;
        for (var x = 0; x < row.Length; x++)
          original[off + x] = row[x].R >= 128;
      }
    });

    mask.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          if (row[x].R >= 128) continue; // already set

          var found = false;
          var y0 = Math.Max(0, y - radius);
          var y1 = Math.Min(h - 1, y + radius);
          var x0 = Math.Max(0, x - radius);
          var x1 = Math.Min(w - 1, x + radius);
          for (var ny = y0; ny <= y1 && !found; ny++)
            for (var nx = x0; nx <= x1 && !found; nx++)
              if (original[ny * w + nx])
                found = true;

          if (found)
            row[x] = new Rgba32(255, 255, 255, 255);
        }
      }
    });
  }

  /// <summary>
  /// Run the full content-aware fill pipeline: rasterise dabs to a mask,
  /// dilate it, run LaMa inpainting, return the result. Returns null when
  /// the inpainter model is unavailable or the mask is empty.
  /// </summary>
  /// <param name="source">Source image (not mutated).</param>
  /// <param name="dabs">Brush dabs defining the removal region.</param>
  /// <param name="dilationPx">Mask dilation radius. Use
  ///   <see cref="DefaultDilationPx"/> for the standard feather margin.</param>
  /// <param name="ct">Cancellation token forwarded to the inpainter.</param>
  public static Image<Rgba32>? Apply(
    Image<Rgba32> source,
    IReadOnlyList<BrushDab> dabs,
    int dilationPx = DefaultDilationPx,
    CancellationToken ct = default) {
    if (dabs is not { Count: > 0 })
      return null;

    using var mask = RasteriseMask(dabs, source.Width, source.Height);

    // Check if the mask has any set pixels — if not, it's a no-op.
    if (!HasAnyMaskedPixels(mask))
      return null;

    DilateMask(mask, dilationPx);

    using var inpainter = new OnnxInpainter();
    if (!inpainter.IsAvailable)
      return null;

    return inpainter.Inpaint(source, mask, ct);
  }

  /// <summary>Quick scan to check whether any pixel in the mask is set.</summary>
  internal static bool HasAnyMaskedPixels(Image<Rgba32> mask) {
    var found = false;
    mask.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height && !found; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          if (row[x].R >= 128) {
            found = true;
            break;
          }
        }
      }
    });
    return found;
  }
}
