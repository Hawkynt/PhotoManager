using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Hawkynt.PhotoManager.Core.Detection;

namespace Hawkynt.PhotoManager.Core.Previews;

/// <summary>
/// Cuts a <see cref="NormalizedBoundingBox"/> out of an image and encodes it
/// as JPEG bytes suitable for Avalonia's <c>Bitmap</c> constructor. Adds a
/// small margin so the cropped face/object isn't flush against the edge,
/// then caps the long side at <see cref="MaxEdgePixels"/> so giant RAW
/// previews don't blow up the UI.
/// </summary>
public static class RegionThumbnailExtractor {
  public const int MaxEdgePixels = 240;
  private const float MarginFraction = 0.08f;

  public static async Task<byte[]?> CropAsync(
    FileInfo imageFile,
    NormalizedBoundingBox box,
    CancellationToken cancellationToken = default
  ) {
    if (!imageFile.Exists)
      return null;

    try {
      // For RAW formats reuse the embedded JPEG rather than decoding the mosaic.
      var decoded = await LoadDecodeTargetAsync(imageFile, cancellationToken);
      if (decoded == null)
        return null;

      using var image = decoded;

      var (x, y, w, h) = ExpandWithMargin(box, image.Width, image.Height);
      if (w <= 0 || h <= 0)
        return null;

      image.Mutate(c => c.Crop(new Rectangle(x, y, w, h)));

      var longest = Math.Max(image.Width, image.Height);
      if (longest > MaxEdgePixels) {
        var scale = (double)MaxEdgePixels / longest;
        image.Mutate(c => c.Resize((int)(image.Width * scale), (int)(image.Height * scale)));
      }

      using var ms = new MemoryStream();
      await image.SaveAsJpegAsync(ms, cancellationToken);
      return ms.ToArray();
    } catch {
      return null;
    }
  }

  private static async Task<Image<Rgba32>?> LoadDecodeTargetAsync(FileInfo imageFile, CancellationToken cancellationToken) {
    // Prefer the embedded JPEG for RAW files — decoding the mosaic for a
    // thumbnail would be absurd, and the previews already contain what we want.
    var raw = await RawPreviewExtractor.ExtractLargestJpegAsync(imageFile, cancellationToken);
    if (raw != null) {
      try {
        using var ms = new MemoryStream(raw, writable: false);
        // Flatten alpha onto white — transparent GIF / PNG thumbnails
        // would otherwise render as black via the JPEG / WebP encoder.
        return Hawkynt.PhotoManager.Core.Imaging.AlphaFlattener.FlattenOntoWhite(
          await Image.LoadAsync<Rgba32>(ms, cancellationToken));
      } catch {
        // Fall through to the generic decode attempt.
      }
    }

    try {
      return Hawkynt.PhotoManager.Core.Imaging.AlphaFlattener.FlattenOntoWhite(
        await Image.LoadAsync<Rgba32>(imageFile.FullName, cancellationToken));
    } catch {
      return null;
    }
  }

  private static (int X, int Y, int W, int H) ExpandWithMargin(NormalizedBoundingBox box, int imageWidth, int imageHeight) {
    var marginX = box.Width * MarginFraction;
    var marginY = box.Height * MarginFraction;

    var leftN = Math.Clamp(box.X - marginX, 0, 1);
    var topN = Math.Clamp(box.Y - marginY, 0, 1);
    var rightN = Math.Clamp(box.X + box.Width + marginX, 0, 1);
    var bottomN = Math.Clamp(box.Y + box.Height + marginY, 0, 1);

    var x = (int)Math.Floor(leftN * imageWidth);
    var y = (int)Math.Floor(topN * imageHeight);
    var right = (int)Math.Ceiling(rightN * imageWidth);
    var bottom = (int)Math.Ceiling(bottomN * imageHeight);

    return (x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
  }
}
