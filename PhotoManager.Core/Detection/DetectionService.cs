using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Detection;

/// <summary>
/// Orchestrates detection + auto-keyword writing. Runs an
/// <see cref="IDetector"/> against a file, reads existing keywords from the
/// sidecar, merges the detected labels on top (case-insensitive dedupe,
/// preserving user-added keywords), and writes the sidecar back through
/// the Phase-1 <see cref="IMetadataWriter"/>.
/// </summary>
public sealed class DetectionService {
  private readonly IDetector _detector;
  private readonly IMetadataReader _reader;
  private readonly IMetadataWriter _writer;

  public DetectionService(IDetector detector, IMetadataReader reader, IMetadataWriter writer) {
    this._detector = detector ?? throw new ArgumentNullException(nameof(detector));
    this._reader = reader ?? throw new ArgumentNullException(nameof(reader));
    this._writer = writer ?? throw new ArgumentNullException(nameof(writer));
  }

  /// <summary>
  /// Runs detection and merges the resulting labels into the sidecar's
  /// keyword list. Returns the detection result so callers can show what
  /// was found.
  /// </summary>
  public async Task<DetectionResult> DetectAndWriteKeywordsAsync(
    FileInfo imageFile,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(imageFile);

    var result = await this._detector.DetectAsync(imageFile, cancellationToken);
    if (cancellationToken.IsCancellationRequested)
      return result;

    var detectedNames = result.DistinctLabelNames().ToArray();
    if (detectedNames.Length == 0)
      return result;

    var existing = await this._reader.ReadAsync(imageFile, cancellationToken);
    var merged = MergeKeywords(existing.Keywords, detectedNames);

    await this._writer.ApplyAsync(imageFile, new MetadataEdit {
      Keywords = Optional<IReadOnlyList<string>>.Set(merged)
    }, cancellationToken);

    return result;
  }

  /// <summary>
  /// Merges detected labels into an existing keyword list, preserving order
  /// and case of existing entries. Case-insensitive dedupe so "beach" and
  /// "Beach" don't both end up in the sidecar.
  /// </summary>
  public static IReadOnlyList<string> MergeKeywords(IEnumerable<string> existing, IEnumerable<string> detected) {
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var result = new List<string>();

    foreach (var k in existing) {
      if (string.IsNullOrWhiteSpace(k) || !seen.Add(k))
        continue;
      result.Add(k);
    }

    foreach (var k in detected) {
      if (string.IsNullOrWhiteSpace(k) || !seen.Add(k))
        continue;
      result.Add(k);
    }

    return result;
  }
}
