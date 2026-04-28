using FileFormat.CameraRaw;
using FileFormat.Dng;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Develop;

/// <summary>
/// Loads RAW container files via PNGCrushCS (FileFormat.CameraRaw +
/// FileFormat.Dng) and hands back an ImageSharp <see cref="Image{Rgba32}"/>
/// ready for the develop pipeline. Falls through to ImageSharp's own
/// loader for non-RAW formats so the developer treats every format the
/// same downstream.
///
/// PNGCrushCS does the demosaic + colour conversion; we just rewrap its
/// RGB24 pixel buffer into an ImageSharp image. Pure-managed code, no
/// LibRaw / dcraw dependency.
/// </summary>
public static class RawImageLoader {
  /// <summary>
  /// Camera-RAW container extensions. DNG is handled separately because
  /// FileFormat.Dng has its own optimised reader.
  /// </summary>
  private static readonly HashSet<string> CameraRawExtensions = new(StringComparer.OrdinalIgnoreCase) {
    ".cr2", ".cr3", ".crw",   // Canon
    ".nef",                    // Nikon
    ".arw", ".srf", ".sr2",    // Sony
    ".raf",                    // Fujifilm
    ".orf",                    // Olympus
    ".rw2",                    // Panasonic
    ".pef", ".ptx",            // Pentax
    ".srw",                    // Samsung
    ".raw"                     // Generic
  };

  public static bool IsRawExtension(string extension)
    => CameraRawExtensions.Contains(extension)
       || extension.Equals(".dng", StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Loads any image into an ImageSharp Rgba32 buffer. RAWs go through
  /// PNGCrushCS readers; everything else falls through to ImageSharp's
  /// own decode path.
  /// </summary>
  public static async Task<Image<Rgba32>> LoadAsync(FileInfo file, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(file);
    if (!file.Exists)
      throw new FileNotFoundException("Source image not found.", file.FullName);

    var ext = file.Extension;

    if (ext.Equals(".dng", StringComparison.OrdinalIgnoreCase))
      return await Task.Run(() => FromDng(file), cancellationToken);

    if (CameraRawExtensions.Contains(ext))
      return await Task.Run(() => FromCameraRaw(file), cancellationToken);

    return await Image.LoadAsync<Rgba32>(file.FullName, cancellationToken);
  }

  private static Image<Rgba32> FromCameraRaw(FileInfo file) {
    var raw = CameraRawReader.FromFile(file);
    return RebuildFromRgb24(raw.Width, raw.Height, raw.PixelData);
  }

  private static Image<Rgba32> FromDng(FileInfo file) {
    var dng = DngReader.FromFile(file);
    // DngFile carries the same Width/Height/PixelData (RGB24) shape as the
    // CameraRaw reader once decoded.
    return RebuildFromRgb24(dng.Width, dng.Height, dng.PixelData);
  }

  /// <summary>
  /// Wrap an interleaved RGB24 byte buffer into an ImageSharp Rgba32
  /// image. Allocates a fresh buffer (RGB24 → RGBA32 means we have to
  /// stride out the alpha channel) but stays single-pass.
  /// </summary>
  private static Image<Rgba32> RebuildFromRgb24(int width, int height, byte[] rgb24) {
    var image = new Image<Rgba32>(width, height);
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var srcOffset = y * width * 3;
        for (var x = 0; x < row.Length; x++) {
          var i = srcOffset + x * 3;
          row[x] = new Rgba32(rgb24[i], rgb24[i + 1], rgb24[i + 2], (byte)255);
        }
      }
    });
    return image;
  }
}
