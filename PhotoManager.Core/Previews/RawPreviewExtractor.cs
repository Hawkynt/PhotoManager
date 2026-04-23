namespace PhotoManager.Core.Previews;

/// <summary>
/// Extracts the largest embedded JPEG preview from a RAW file. Every modern
/// camera writes a full-resolution JPEG preview alongside the raw sensor data
/// for in-camera playback, which we can lift out without touching the mosaic'd
/// pixel data. This avoids shipping LibRaw or dcraw while covering NEF, CR2,
/// CR3, ARW, DNG, RAF, ORF, RW2, PEF, SRW, and friends.
/// </summary>
public static class RawPreviewExtractor {
  /// <summary>
  /// Scans <paramref name="file"/> for embedded JPEG byte-streams and returns
  /// the bytes of the largest one, or null if no JPEG is found.
  /// </summary>
  public static async Task<byte[]?> ExtractLargestJpegAsync(FileInfo file, CancellationToken cancellationToken = default) {
    if (!file.Exists)
      return null;

    // Read the whole file once; scanning needs random access.
    // RAW files top out around 100 MB — fine for one-shot allocation on a preview click.
    var data = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
    return FindLargestJpeg(data);
  }

  /// <summary>
  /// Byte-scans <paramref name="data"/> for well-formed JPEG spans. Handles
  /// nested SOI/EOI markers (a preview JPEG often embeds its own thumbnail)
  /// by tracking depth, so the outer JPEG is captured whole rather than
  /// truncated at the inner EOI.
  /// </summary>
  internal static byte[]? FindLargestJpeg(ReadOnlySpan<byte> data) {
    byte[]? best = null;
    var bestSize = 0;

    var i = 0;
    while (i < data.Length - 3) {
      // JPEG SOI is FF D8 followed by another marker (FF xx) — requiring the
      // trailing FF filters out most false hits in RAW pixel noise.
      if (data[i] != 0xFF || data[i + 1] != 0xD8 || data[i + 2] != 0xFF) {
        i++;
        continue;
      }

      if (TryFindEnd(data, i, out var end)) {
        var size = end - i;
        if (size > bestSize) {
          bestSize = size;
          best = data.Slice(i, size).ToArray();
        }
        i = end;
      } else {
        // Unterminated JPEG — abandon this candidate and keep scanning.
        i++;
      }
    }

    return best;
  }

  /// <summary>
  /// Walks forward from a known SOI position tracking nested SOI/EOI depth,
  /// and returns the index just past the matching EOI.
  /// </summary>
  private static bool TryFindEnd(ReadOnlySpan<byte> data, int soiIndex, out int endIndex) {
    var depth = 1;
    var i = soiIndex + 2;

    while (i < data.Length - 1) {
      if (data[i] != 0xFF) {
        i++;
        continue;
      }

      var marker = data[i + 1];
      switch (marker) {
        case 0x00:
          // Stuffed 0xFF in compressed scan data — skip both bytes.
          i += 2;
          break;
        case 0xD8:
          depth++;
          i += 2;
          break;
        case 0xD9:
          depth--;
          if (depth == 0) {
            endIndex = i + 2;
            return true;
          }
          i += 2;
          break;
        default:
          i++;
          break;
      }
    }

    endIndex = 0;
    return false;
  }
}
