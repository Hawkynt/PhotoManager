using Avalonia.Media.Imaging;
using PhotoManager.Core.Develop;
using PhotoManager.Core.Previews;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.UI.Services;

/// <summary>
/// Loads a file into an Avalonia Bitmap. Flow:
///   1. Check the in-memory cache (keyed by path+size+mtime+target-size).
///   2. If it's a RAW file, extract the largest embedded JPEG preview.
///   3. Else decode natively via Avalonia.
///   4. If native decode fails, fall back to ImageSharp (covers WebP, TIFF
///      variants, HEIC via plugins, etc.).
///   5. Resize to a max edge length and store JPEG bytes in the cache.
///
/// All caching is in-memory per the no-local-DB principle — on relaunch the
/// cache is empty and previews regenerate on demand.
/// </summary>
public static class ImagePreviewLoader {
  private const int MaxDimension = 1600;
  private static readonly InMemoryPreviewCache _cache = new();

  /// <summary>Exposes the shared preview cache so the thumbnail
  /// pre-cache service can populate the same store the on-demand
  /// loader reads from.</summary>
  public static InMemoryPreviewCache Cache => _cache;

  /// <summary>Decode + resize a file to JPEG bytes at preview
  /// resolution. Exposed so the pre-cache service uses the exact
  /// same pipeline as the on-demand path.</summary>
  public static async Task<byte[]?> DecodeAsync(FileInfo file, CancellationToken ct = default) {
    try {
      using var image = await RawImageLoader.LoadAsync(file);
      return await ResizeImageAsync(image, ct);
    } catch {
      return null;
    }
  }

  private static readonly HashSet<string> _rawExtensions = new(StringComparer.OrdinalIgnoreCase) {
    ".cr2", ".cr3", ".crw",
    ".nef",
    ".arw", ".srf", ".sr2", ".ari", ".sraw",
    ".dng", ".raw",
    ".raf",
    ".orf",
    ".rw2",
    ".pef", ".ptx", ".pxn",
    ".srw",
    ".x3f",
    ".mrw", ".mdc",
    ".dcr", ".kdc", ".dcs", ".dc2", ".k25",
    ".erf",
    ".mef",
    ".mos",
    ".rwl", ".rwz",
    ".iiq", ".cap",
    ".3fr", ".fff",
    ".bay", ".ciff", ".cs1", ".drf"
  };

  public static async Task<Bitmap?> LoadAsync(FileInfo file, CancellationToken cancellationToken = default) {
    if (!file.Exists)
      return null;

    var key = PreviewCacheKey.For(file, MaxDimension);
    if (_cache.TryGet(key, out var cachedBytes))
      return BitmapFromBytes(cachedBytes);

    var bytes = await DecodeAndResizeAsync(file, cancellationToken);
    if (bytes == null)
      return null;

    _cache.Set(key, bytes);
    return BitmapFromBytes(bytes);
  }

  private static async Task<byte[]?> DecodeAndResizeAsync(FileInfo file, CancellationToken cancellationToken) {
    // RAW: extract embedded JPEG preview rather than decoding the mosaic.
    if (_rawExtensions.Contains(file.Extension)) {
      var jpeg = await RawPreviewExtractor.ExtractLargestJpegAsync(file, cancellationToken);
      if (jpeg != null)
        return await ResizeJpegAsync(jpeg, cancellationToken);
      // fall through to generic decode if no preview was found
    }

    try {
      // Route through RawImageLoader so AVIF / HEIC / JPEG2000 / PSD / HDR /
      // EXR / DDS / PCX / ICO / APNG all decode via their FileFormat.* libs.
      // ImageSharp's own decoder only covers JPG/PNG/BMP/GIF/TIFF/WebP/etc.,
      // so calling it directly here would silently null-out everything else.
      using var image = await RawImageLoader.LoadAsync(file, cancellationToken);
      return await ResizeImageAsync(image, cancellationToken);
    } catch {
      return null;
    }
  }

  private static async Task<byte[]?> ResizeJpegAsync(byte[] jpegBytes, CancellationToken cancellationToken) {
    try {
      // If the embedded JPEG already fits within MaxDimension, skip the
      // decode/resize/encode cycle entirely and cache the raw bytes. Use
      // Image.Identify to read JPEG headers without fully decoding pixels.
      using var identifyStream = new MemoryStream(jpegBytes, writable: false);
      var info = await Image.IdentifyAsync(identifyStream, cancellationToken);
      if (info != null && Math.Max(info.Width, info.Height) <= MaxDimension)
        return jpegBytes;

      using var ms = new MemoryStream(jpegBytes, writable: false);
      using var image = await Image.LoadAsync<Rgba32>(ms, cancellationToken);
      // Flatten any alpha onto white — ImageSharp's downstream JPEG /
      // WebP encoders bake transparent pixels to black, so a transparent
      // GIF / PNG would render as a mostly-black thumbnail otherwise.
      PhotoManager.Core.Imaging.AlphaFlattener.FlattenOntoWhite(image);
      return await ResizeImageAsync(image, cancellationToken);
    } catch {
      return null;
    }
  }

  private static readonly WebpEncoder _webpEncoder = new() { Quality = 80 };

  private static async Task<byte[]> ResizeImageAsync(Image<Rgba32> image, CancellationToken cancellationToken) {
    var longest = Math.Max(image.Width, image.Height);
    if (longest > MaxDimension) {
      var scale = (double)MaxDimension / longest;
      image.Mutate(c => c.Resize((int)(image.Width * scale), (int)(image.Height * scale)));
    }

    using var ms = new MemoryStream();
    await image.SaveAsync(ms, _webpEncoder, cancellationToken);
    return ms.ToArray();
  }

  private static Bitmap? BitmapFromBytes(byte[] bytes) {
    try {
      using var ms = new MemoryStream(bytes, writable: false);
      return new Bitmap(ms);
    } catch {
      return null;
    }
  }
}
