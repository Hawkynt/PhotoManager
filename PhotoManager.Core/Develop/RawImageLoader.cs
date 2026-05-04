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
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Develop;

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
  /// Per-extension dispatch to the matching PNGCrushCS FileFormat.* lib.
  /// Returns null for extensions we don't handle here so the caller can
  /// fall through to ImageSharp.
  /// </summary>
  private static Image<Rgba32>? FromPngCrushFormat(FileInfo file) {
    var raw = file.Extension.ToLowerInvariant() switch {
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
    return raw is null ? null : RawImageToImageSharp(raw);
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
