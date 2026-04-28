namespace PhotoManager.Core.Metadata;

/// <summary>
/// Detect a file's actual format from its leading bytes (magic number)
/// and compare to its declared extension. Used to flag obvious mislabels
/// — e.g. PNG bytes living in <c>.jpg</c> after a sloppy export.
/// Read-only: never renames anything itself; surfaces the suggested
/// extension and lets the caller decide.
/// </summary>
public static class FileSignatureDetector {
  public sealed record DetectionResult(
    string DetectedExtension,
    bool MatchesDeclared,
    string? SuggestedRenameExtension);

  public static async Task<DetectionResult?> DetectAsync(FileInfo file, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(file);
    if (!file.Exists)
      return null;

    var head = new byte[16];
    try {
      await using var stream = file.OpenRead();
      var read = await stream.ReadAsync(head.AsMemory(0, head.Length), cancellationToken);
      if (read < 4)
        return null;
      Array.Resize(ref head, read);
    } catch {
      return null;
    }

    var detected = ClassifySignature(head);
    if (detected is null)
      return null;

    var declared = file.Extension.TrimStart('.').ToLowerInvariant();
    var matches = ExtensionsForFormat(detected).Contains(declared, StringComparer.OrdinalIgnoreCase);
    return new DetectionResult(
      DetectedExtension: detected,
      MatchesDeclared: matches,
      SuggestedRenameExtension: matches ? null : detected);
  }

  /// <summary>
  /// Recognise the broad-strokes formats we routinely see in photo
  /// libraries. Returns the canonical extension (jpg/png/...) without the
  /// leading dot, or null if the bytes don't ring any bells.
  /// </summary>
  private static string? ClassifySignature(byte[] head) {
    if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
      return "jpg";
    if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
        && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
      return "png";
    if (head.Length >= 6 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46
        && head[3] == 0x38 && (head[4] == 0x37 || head[4] == 0x39) && head[5] == 0x61)
      return "gif";
    if (head.Length >= 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
        && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50)
      return "webp";
    if (head.Length >= 4 && head[0] == 0x42 && head[1] == 0x4D)
      return "bmp";
    if (head.Length >= 4
        && ((head[0] == 0x49 && head[1] == 0x49 && head[2] == 0x2A && head[3] == 0x00)
         || (head[0] == 0x4D && head[1] == 0x4D && head[2] == 0x00 && head[3] == 0x2A)))
      return "tif";  // also matches DNG / CR2 / NEF / ARW which are TIFF containers; we can't disambiguate by magic alone
    if (head.Length >= 12 && head[4] == 0x66 && head[5] == 0x74 && head[6] == 0x79 && head[7] == 0x70)
      return "heic";
    return null;
  }

  /// <summary>Map a canonical extension to the family it covers (so jpg ⇔ jpeg, tif ⇔ tiff).</summary>
  private static IReadOnlyList<string> ExtensionsForFormat(string canonical) => canonical switch {
    "jpg"  => new[] { "jpg", "jpeg", "jpe", "jfif" },
    "png"  => new[] { "png" },
    "gif"  => new[] { "gif" },
    "webp" => new[] { "webp" },
    "bmp"  => new[] { "bmp" },
    "tif"  => new[] { "tif", "tiff", "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "pef", "raf", "raw" },
    "heic" => new[] { "heic", "heif" },
    _ => Array.Empty<string>()
  };
}
