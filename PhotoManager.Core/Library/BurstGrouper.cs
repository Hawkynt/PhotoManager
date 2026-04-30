using PhotoManager.Core.Metadata;

namespace PhotoManager.Core.Library;

/// <summary>
/// One detected burst — photos taken in rapid succession with similar
/// filenames. <see cref="SuggestedName"/> is built from the shared prefix
/// of the members' filenames plus the numeric range when possible (e.g.
/// "IMG_1234..1240"), otherwise just the first member's stem.
/// </summary>
public sealed record BurstGroup(
  IReadOnlyList<FileInfo> Members,
  DateTime From,
  DateTime To,
  string SuggestedName
);

/// <summary>
/// Pure logic for grouping photo bursts. Sorts entries by capture date,
/// then walks them and starts a new burst whenever the gap to the previous
/// shot exceeds <c>windowSize</c> OR the filename stem differs from the
/// running burst's prefix by more than <c>filenameSimilarityThreshold</c>
/// edit-stem characters. No IO, fully deterministic.
/// </summary>
public static class BurstGrouper {
  public static readonly TimeSpan DefaultWindowSize = TimeSpan.FromSeconds(2);
  public const int DefaultFilenameSimilarityThreshold = 3;

  public static IReadOnlyList<BurstGroup> GroupBursts(
    IEnumerable<(FileInfo File, FullMetadata Metadata)> entries,
    TimeSpan? windowSize = null,
    int? filenameSimilarityThreshold = null
  ) {
    ArgumentNullException.ThrowIfNull(entries);

    var window = windowSize ?? DefaultWindowSize;
    var threshold = filenameSimilarityThreshold ?? DefaultFilenameSimilarityThreshold;

    var sorted = entries
      .Where(e => e.File != null)
      .Select(e => (e.File, e.Metadata, Captured: ResolveCaptureDate(e.File, e.Metadata)))
      .OrderBy(e => e.Captured)
      .ThenBy(e => e.File.Name, StringComparer.OrdinalIgnoreCase)
      .ToList();

    if (sorted.Count == 0)
      return Array.Empty<BurstGroup>();

    var result = new List<BurstGroup>();
    var currentMembers = new List<FileInfo>();
    var currentTimes = new List<DateTime>();
    string? currentStem = null;

    foreach (var entry in sorted) {
      var stem = Path.GetFileNameWithoutExtension(entry.File.Name);

      if (currentMembers.Count == 0) {
        currentMembers.Add(entry.File);
        currentTimes.Add(entry.Captured);
        currentStem = stem;
        continue;
      }

      var gap = entry.Captured - currentTimes[^1];
      var stemDistance = StemDistance(currentStem!, stem);
      var startNew = gap > window || stemDistance > threshold;

      if (startNew) {
        result.Add(BuildGroup(currentMembers, currentTimes));
        currentMembers = new List<FileInfo> { entry.File };
        currentTimes = new List<DateTime> { entry.Captured };
        currentStem = stem;
        continue;
      }

      currentMembers.Add(entry.File);
      currentTimes.Add(entry.Captured);
    }

    if (currentMembers.Count > 0)
      result.Add(BuildGroup(currentMembers, currentTimes));

    return result;
  }

  private static DateTime ResolveCaptureDate(FileInfo file, FullMetadata? metadata) {
    if (metadata?.DateCreated is { } dc)
      return dc;
    try {
      return file.LastWriteTime;
    } catch {
      return DateTime.MinValue;
    }
  }

  private static BurstGroup BuildGroup(IReadOnlyList<FileInfo> members, IReadOnlyList<DateTime> times) {
    var from = times.Min();
    var to = times.Max();
    return new BurstGroup(members.ToList(), from, to, SuggestName(members));
  }

  /// <summary>
  /// Distance metric used to decide whether two filenames belong to the
  /// same burst. Compares the shared prefix length: characters AFTER the
  /// common prefix are counted up to the longer stem. Captures the
  /// "IMG_1234.jpg vs IMG_1235.jpg → distance 1" intuition without paying
  /// for a full Levenshtein DP.
  /// </summary>
  internal static int StemDistance(string a, string b) {
    if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b))
      return 0;
    if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
      return Math.Max(a?.Length ?? 0, b?.Length ?? 0);

    var min = Math.Min(a.Length, b.Length);
    var max = Math.Max(a.Length, b.Length);
    var prefix = 0;
    while (prefix < min && char.ToLowerInvariant(a[prefix]) == char.ToLowerInvariant(b[prefix]))
      prefix++;
    return max - prefix;
  }

  private static string SuggestName(IReadOnlyList<FileInfo> members) {
    if (members.Count == 0)
      return string.Empty;

    var firstStem = Path.GetFileNameWithoutExtension(members[0].Name);
    if (members.Count == 1)
      return firstStem;

    var lastStem = Path.GetFileNameWithoutExtension(members[^1].Name);

    // Split each stem at its numeric tail. When both share an identical
    // alphabetic prefix the suggested name becomes "PFX1234..1236"; otherwise
    // fall back to the longest common character prefix.
    var (firstPrefix, firstNumeric) = SplitTrailingDigits(firstStem);
    var (lastPrefix, lastNumeric) = SplitTrailingDigits(lastStem);

    if (firstPrefix == lastPrefix && !string.IsNullOrEmpty(firstNumeric) && !string.IsNullOrEmpty(lastNumeric))
      return $"{firstPrefix}{firstNumeric}..{lastNumeric}";

    var sharedPrefix = SharedPrefix(firstStem, lastStem);
    var firstSuffix = firstStem[sharedPrefix.Length..];
    var lastSuffix = lastStem[sharedPrefix.Length..];

    if (!string.IsNullOrEmpty(firstSuffix) && !string.IsNullOrEmpty(lastSuffix))
      return $"{sharedPrefix}{firstSuffix}..{lastSuffix}";
    return firstStem;
  }

  private static (string Prefix, string Numeric) SplitTrailingDigits(string stem) {
    var i = stem.Length;
    while (i > 0 && char.IsDigit(stem[i - 1]))
      i--;
    return (stem[..i], stem[i..]);
  }

  private static string SharedPrefix(string a, string b) {
    var min = Math.Min(a.Length, b.Length);
    var i = 0;
    while (i < min && a[i] == b[i])
      i++;
    return a[..i];
  }
}
