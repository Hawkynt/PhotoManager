using Avalonia.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.UI.Services;

/// <summary>
/// Loads a file into an Avalonia Bitmap, with an ImageSharp fallback for
/// formats Avalonia can't decode natively (WebP, HEIC, TIFF variants, etc.).
/// Thumbnails are capped at 1600px on the long edge to keep memory sane.
/// </summary>
public static class ImagePreviewLoader {
  private const int MaxDimension = 1600;

  public static async Task<Bitmap?> LoadAsync(FileInfo file, CancellationToken cancellationToken = default) {
    if (!file.Exists)
      return null;

    try {
      await using var fs = file.OpenRead();
      return new Bitmap(fs);
    } catch {
      // Fall through to ImageSharp
    }

    try {
      using var image = await Image.LoadAsync<Rgba32>(file.FullName, cancellationToken);

      var longest = Math.Max(image.Width, image.Height);
      if (longest > MaxDimension) {
        var scale = (double)MaxDimension / longest;
        image.Mutate(c => c.Resize((int)(image.Width * scale), (int)(image.Height * scale)));
      }

      using var ms = new MemoryStream();
      await image.SaveAsBmpAsync(ms, cancellationToken);
      ms.Position = 0;
      return new Bitmap(ms);
    } catch {
      return null;
    }
  }
}
