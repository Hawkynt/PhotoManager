using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Imaging;

/// <summary>
/// In-place "src-over-white" composite for any <see cref="Image{Rgba32}"/>.
///
/// Every loader that hands an image to a display / save-as path goes
/// through this — ImageSharp's JPEG encoder (which the preview painter
/// and Save As both use) bakes alpha=0 pixels to black, producing the
/// well-known "developing a GIF gives an all-black preview" failure
/// mode. Flattening at load time lets the rest of the pipeline assume
/// opaque RGB without each downstream stage needing to care about alpha.
///
/// Opaque pixels (alpha == 255) hit the early continue and pay zero cost,
/// so calling this on a clean JPEG / RAW source is essentially free.
/// </summary>
public static class AlphaFlattener {
  /// <summary>
  /// Composite every partially-transparent pixel onto a pure-white
  /// background and force alpha to 255. Returns <paramref name="image"/>
  /// for fluent use; the operation is in-place.
  /// </summary>
  public static Image<Rgba32> FlattenOntoWhite(Image<Rgba32> image) {
    ArgumentNullException.ThrowIfNull(image);
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var p = row[x];
          if (p.A == 255)
            continue;
          if (p.A == 0) {
            row[x] = new Rgba32((byte)255, (byte)255, (byte)255, (byte)255);
            continue;
          }
          var a = p.A / 255f;
          var inv = 1f - a;
          row[x] = new Rgba32(
            (byte)(p.R * a + 255 * inv),
            (byte)(p.G * a + 255 * inv),
            (byte)(p.B * a + 255 * inv),
            (byte)255);
        }
      }
    });
    return image;
  }
}
