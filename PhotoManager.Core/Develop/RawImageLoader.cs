using FileFormat.Apng;
using FileFormat.Avif;
using FileFormat.CameraRaw;
using FileFormat.Core;
using FileFormat.Dds;
using FileFormat.Dng;
using FileFormat.Exr;
using FileFormat.Hdr;
using FileFormat.Heif;
using FileFormat.Ico;
using FileFormat.Jpeg2000;
using FileFormat.Pcx;
using FileFormat.Psd;
using Hawkynt.PhotoManager.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Loads any image file the rest of the develop / restoration pipeline
/// understands. Three tiers, picked by extension:
///
///   1. RAW containers (.cr2, .nef, .arw, .dng, …) → PNGCrushCS RAW
///      readers (FileFormat.CameraRaw + FileFormat.Dng).
///   2. Modern + previously-unsupported formats (.heic, .heif, .avif,
///      .psd/.psb, .jp2/.j2k/.jpc, .hdr, .exr, .apng, .dds, .pcx, .ico)
///      → matching FileFormat.* lib via FormatIO.Decode&lt;T&gt;.
///   3. Everything else → ImageSharp's own decoder (JPEG, PNG, BMP,
///      GIF, TIFF, WebP, TGA, PBM, QOI).
///
/// Multi-image formats (ICO, APNG) return their canonical single image
/// (largest icon for ICO, first frame for APNG) — the IImageToRawImage
/// implementations on those file types pick a sensible default.
///
/// Pure-managed code, no native dependencies beyond what each
/// FileFormat.* lib pulls in.
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
  /// PNGCrushCS readers; modern containers go through their matching
  /// FileFormat.* lib; everything else falls through to ImageSharp.
  /// </summary>
  public static async Task<Image<Rgba32>> LoadAsync(FileInfo file, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(file);
    if (!file.Exists)
      throw new FileNotFoundException("Source image not found.", file.FullName);

    var loaded = await LoadAsyncCore(file, cancellationToken);
    // ImageSharp's JPEG encoder (used downstream for previews + Save As)
    // bakes transparent pixels to black, which silently makes any GIF /
    // transparent-PNG develop preview render as a mostly-black image.
    // The develop pipeline isn't built for alpha anyway — flatten before
    // returning so every stage downstream sees opaque RGB regardless of
    // which loader path produced the image.
    return AlphaFlattener.FlattenOntoWhite(loaded);
  }

  private static async Task<Image<Rgba32>> LoadAsyncCore(FileInfo file, CancellationToken cancellationToken) {
    var ext = file.Extension;

    if (ext.Equals(".dng", StringComparison.OrdinalIgnoreCase))
      return await Task.Run(() => FromDng(file), cancellationToken);

    if (CameraRawExtensions.Contains(ext))
      return await Task.Run(() => FromCameraRaw(file), cancellationToken);

    // Modern formats handled via PNGCrushCS readers. The lambda dispatches
    // to the matching FileFormat.<Fmt>File via FormatIO.Decode<T>; null
    // means "not handled here, fall through to ImageSharp".
    var fromExtra = await Task.Run(() => FromPngCrushFormat(file), cancellationToken);
    if (fromExtra is not null)
      return fromExtra;

    return await Image.LoadAsync<Rgba32>(file.FullName, cancellationToken);
  }

  private static Image<Rgba32> FromCameraRaw(FileInfo file) {
    var raw = CameraRawReader.FromFile(file);
    return RebuildFromRgb24(raw.Width, raw.Height, raw.PixelData);
  }

  private static Image<Rgba32> FromDng(FileInfo file) {
    var dng = DngReader.FromFile(file);
    return RebuildFromRgb24(dng.Width, dng.Height, dng.PixelData);
  }

  /// <summary>
  /// Extensions that PNGCrushCS's FileFormat.* libs specifically claim
  /// to handle. The Magick.NET fallback fires only for THIS set when
  /// the PNGCrushCS decoder either fails or returns a zero buffer —
  /// for everything else (GIF, JPG, PNG, BMP, TIFF, WebP …) we return
  /// null so ImageSharp's own decoders take over. Critical: those
  /// non-PNGCrushCS formats must NOT touch Magick because Magick's
  /// GIF decoder flattens transparent regions against the GIF's
  /// background colour (typically pure black), which made every
  /// developed GIF preview come out black.
  /// </summary>
  private static readonly HashSet<string> PngCrushExtensions = new(StringComparer.OrdinalIgnoreCase) {
    ".heic", ".heif", ".avif", ".psd", ".psb",
    ".jp2", ".j2k", ".jpc", ".hdr", ".exr",
    ".apng", ".dds", ".pcx", ".ico"
  };

  /// <summary>
  /// Per-extension dispatch to the matching PNGCrushCS FileFormat.* lib.
  /// Returns null for extensions we don't handle here so the caller can
  /// fall through to ImageSharp.
  /// </summary>
  private static Image<Rgba32>? FromPngCrushFormat(FileInfo file) {
    var ext = file.Extension.ToLowerInvariant();
    if (!PngCrushExtensions.Contains(ext))
      return null;  // ImageSharp will handle it.

    var raw = ext switch {
      ".heic" or ".heif"           => FormatIO.Decode<HeifFile>(file),
      ".avif"                      => FormatIO.Decode<AvifFile>(file),
      ".psd" or ".psb"             => FormatIO.Decode<PsdFile>(file),
      ".jp2" or ".j2k" or ".jpc"   => FormatIO.Decode<Jpeg2000File>(file),
      ".hdr"                       => FormatIO.Decode<HdrFile>(file),
      ".exr"                       => FormatIO.Decode<ExrFile>(file),
      ".apng"                      => FormatIO.Decode<ApngFile>(file),
      ".dds"                       => FormatIO.Decode<DdsFile>(file),
      ".pcx"                       => FormatIO.Decode<PcxFile>(file),
      ".ico"                       => FormatIO.Decode<IcoFile>(file),
      _                            => null
    };

    // Sanity check: the upstream AVIF decoder (FileFormat.Avif at
    // ../../PNGCrushCS) returns a zero-filled buffer on certain AV1
    // profiles instead of failing — we'd otherwise paint pure black,
    // which the user reads as "decode silently broken". When that
    // happens we fall through to Magick.NET, which has libheif/libavif
    // bundled and decodes the broader set of profiles correctly.
    // Same fallback fires when the PNGCrushCS lib couldn't decode at
    // all (returns null) — Magick is the broader-codec last resort.
    if (raw is null || IsAllZero(raw.PixelData)) {
      var fromMagick = TryMagickDecode(file);
      if (fromMagick is not null)
        return fromMagick;
      if (raw is null)
        return null;  // PNGCrushCS lib didn't recognise it AND Magick couldn't either
      throw new NotSupportedException(
        $"Decoder for {ext} returned an empty image for '{file.Name}', " +
        "and the Magick.NET fallback also failed. Convert the file to PNG/JPEG as a workaround.");
    }

    return RawImageToImageSharp(raw);
  }

  /// <summary>
  /// Try to decode <paramref name="file"/> via Magick.NET (libheif /
  /// libavif / libjpeg / libtiff under the hood). Returns null if the
  /// native bits aren't available or the decode fails — caller falls
  /// through to whatever next-best path is appropriate.
  /// </summary>
  private static Image<Rgba32>? TryMagickDecode(FileInfo file) {
    try {
      using var magick = new ImageMagick.MagickImage(file.FullName);
      magick.ColorSpace = ImageMagick.ColorSpace.sRGB;
      // Read straight into our canonical RGBA buffer; Magick handles
      // 8-vs-16-bit and YUV→RGB conversion for us.
      var w = (int)magick.Width;
      var h = (int)magick.Height;
      var pixels = magick.GetPixelsUnsafe();
      var bytes = pixels.ToByteArray(ImageMagick.PixelMapping.RGBA);
      if (bytes is null || bytes.Length != w * h * 4)
        return null;
      var image = new Image<Rgba32>(w, h);
      image.ProcessPixelRows(accessor => {
        for (var y = 0; y < h; y++) {
          var row = accessor.GetRowSpan(y);
          var off = y * w * 4;
          for (var x = 0; x < w; x++) {
            var i = off + x * 4;
            row[x] = new Rgba32(bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]);
          }
        }
      });
      return image;
    } catch {
      return null;
    }
  }

  private static bool IsAllZero(byte[]? data) {
    if (data is null || data.Length == 0)
      return true;
    // Sample every Nth byte rather than walk the whole buffer; large
    // images would cost ~30 MB of pointless reads otherwise. A real
    // image has high-entropy pixels — finding even ONE non-zero in a
    // sparse scan is conclusive proof the buffer isn't blank.
    var step = Math.Max(1, data.Length / 4096);
    for (var i = 0; i < data.Length; i += step)
      if (data[i] != 0)
        return false;
    return true;
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

  /// <summary>
  /// Convert any <see cref="RawImage"/> to an ImageSharp <see cref="Image{Rgba32}"/>.
  /// PixelConverter.Convert handles the format-cross between the many
  /// PixelFormat variants (Gray8, Rgb24, Rgba64, Indexed8, etc.) and our
  /// canonical Rgba32, so each FileFormat lib doesn't have to ship its
  /// own ImageSharp adapter.
  /// </summary>
  private static Image<Rgba32> RawImageToImageSharp(RawImage raw) {
    var rgba = raw.Format == PixelFormat.Rgba32
      ? raw
      : PixelConverter.Convert(raw, PixelFormat.Rgba32);
    var data = rgba.PixelData;
    var width = rgba.Width;
    var image = new Image<Rgba32>(width, rgba.Height);
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var srcOffset = y * width * 4;
        for (var x = 0; x < row.Length; x++) {
          var i = srcOffset + x * 4;
          row[x] = new Rgba32(data[i], data[i + 1], data[i + 2], data[i + 3]);
        }
      }
    });
    return image;
  }
}
