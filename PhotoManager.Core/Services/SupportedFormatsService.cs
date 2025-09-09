using PhotoManager.Core.Interfaces;

namespace PhotoManager.Core.Services;

public class SupportedFormatsService : ISupportedFormatsService {
  
  // Central definition of all supported formats based on MetadataExtractor capabilities
  private static readonly Dictionary<string, string[]> _formatExtensions = new(StringComparer.OrdinalIgnoreCase) {
    ["JPEG"] = [".jpg", ".jpeg", ".jfif", ".jpe"],
    ["TIFF"] = [".tiff", ".tif"],
    ["Photoshop"] = [".psd", ".psb"],
    ["PNG"] = [".png"],
    ["BMP"] = [".bmp", ".dib"],
    ["GIF"] = [".gif"],
    ["ICO"] = [".ico"],
    ["Netpbm"] = [".pgm", ".ppm", ".pbm", ".pnm"],
    ["PCX"] = [".pcx"],
    ["WebP"] = [".webp"],
    ["QuickTime"] = [".mov", ".mp4", ".m4v", ".3gp", ".3g2"],
    ["RAW"] = [
      // Canon
      ".cr2", ".cr3", ".crw",
      // Nikon
      ".nef",
      // Sony
      ".arw", ".srf", ".sr2", ".ari", ".sraw",
      // Generic/Adobe
      ".dng", ".raw",
      // Fujifilm
      ".raf",
      // Olympus
      ".orf",
      // Panasonic
      ".rw2",
      // Pentax
      ".pef", ".ptx", ".pxn",
      // Samsung
      ".srw",
      // Sigma
      ".x3f",
      // Minolta
      ".mrw", ".mdc",
      // Kodak
      ".dcr", ".kdc", ".dcs", ".dc2", ".k25",
      // Epson
      ".erf",
      // Mamiya
      ".mef",
      // Leaf
      ".mos",
      // RED
      ".r3d",
      // Leica
      ".rwl", ".rwz",
      // Phase One
      ".iiq", ".cap",
      // Hasselblad
      ".3fr", ".fff",
      // Other
      ".bay", ".ciff", ".cs1", ".drf"
    ]
  };

  public async Task<string[]> GetSupportedExtensionsAsync() {
    return await Task.FromResult(
      _formatExtensions.Values
        .SelectMany(extensions => extensions)
        .Select(ext => $"*{ext}")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(ext => ext)
        .ToArray()
    );
  }

  public async Task<string[]> GetSupportedExtensionsWithoutWildcardsAsync() {
    return await Task.FromResult(
      _formatExtensions.Values
        .SelectMany(extensions => extensions)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(ext => ext)
        .ToArray()
    );
  }

  public bool IsExtensionSupported(string extension) {
    if (string.IsNullOrWhiteSpace(extension))
      return false;
    
    // Normalize extension format (ensure it starts with a dot)
    var normalizedExt = extension.StartsWith('.') ? extension : $".{extension}";
    
    return _formatExtensions.Values
      .SelectMany(extensions => extensions)
      .Contains(normalizedExt, StringComparer.OrdinalIgnoreCase);
  }

  public async Task<Dictionary<string, string[]>> GetExtensionsByFormatAsync() {
    return await Task.FromResult(
      new Dictionary<string, string[]>(_formatExtensions, StringComparer.OrdinalIgnoreCase)
    );
  }
}