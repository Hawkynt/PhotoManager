using PhotoManager.Core.Interfaces;
using PhotoManager.Core.Services;

namespace PhotoManager.Core.Library;

/// <summary>One file plus its 64-bit pHash; what the duplicate finder buckets together.</summary>
public sealed record HashedFile(FileInfo File, ulong Hash);

/// <summary>
/// A group of near-duplicate files sharing pHashes within the requested
/// Hamming threshold. The first item is the "anchor" — every other item's
/// distance is measured against it.
/// </summary>
public sealed record DuplicateGroup(IReadOnlyList<HashedFile> Files, IReadOnlyList<int> Distances) {
  public int Count => this.Files.Count;
}

/// <summary>
/// Walks a directory, populates a <see cref="PerceptualHashCache"/>, and
/// returns clusters of similar files using Hamming distance on the cached
/// pHashes. O(N²) — fine for libraries up to ~10k images; rebuild instead
/// of incrementing for larger collections.
/// </summary>
public sealed class DuplicateFinder {
  public const int DefaultThreshold = 6;

  private readonly PerceptualHashCache _cache;
  private readonly ISupportedFormatsService _formats;

  public DuplicateFinder(PerceptualHashCache? cache = null, ISupportedFormatsService? formats = null) {
    this._cache = cache ?? new PerceptualHashCache();
    this._formats = formats ?? new SupportedFormatsService();
  }

  public PerceptualHashCache Cache => this._cache;

  /// <summary>
  /// Walk <paramref name="root"/>, hash each supported image, and report
  /// progress so long scans don't look hung. Skips unreadable files
  /// silently. Returns the number of files actually hashed.
  /// </summary>
  public async Task<int> ScanAsync(
    DirectoryInfo root,
    bool recursive = true,
    IProgress<FileInfo>? progress = null,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(root);
    if (!root.Exists)
      return 0;

    var extensions = (await this._formats.GetSupportedExtensionsWithoutWildcardsAsync())
      .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
    var hashed = 0;

    foreach (var file in root.EnumerateFiles("*", option)) {
      cancellationToken.ThrowIfCancellationRequested();
      if (!extensions.Contains(file.Extension))
        continue;

      progress?.Report(file);
      try {
        await this._cache.GetAsync(file, cancellationToken);
        hashed++;
      } catch (OperationCanceledException) {
        throw;
      } catch {
        // Skip unreadable files — same posture as LibraryIndex.
      }
    }

    return hashed;
  }

  /// <summary>
  /// Group cached files whose pHashes are within <paramref name="threshold"/>
  /// bits of each other. Greedy single-link clustering against the first
  /// unclaimed file in each iteration — same shape as the chained-diff
  /// dedup most desktop DAMs ship.
  /// </summary>
  public IReadOnlyList<DuplicateGroup> FindGroups(int threshold = DefaultThreshold) {
    if (threshold < 0)
      throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be ≥ 0.");

    var entries = this._cache.Snapshot()
      .Select(s => new HashedFile(s.File, s.Hash))
      .ToList();
    return FindGroups(entries, threshold);
  }

  /// <summary>
  /// Pure-data grouping; takes a snapshot of (file, hash) pairs so callers
  /// holding their own list (e.g. tests) can drive the cluster step
  /// directly without going through a cache.
  /// </summary>
  public static IReadOnlyList<DuplicateGroup> FindGroups(IReadOnlyList<HashedFile> files, int threshold) {
    if (threshold < 0)
      throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be ≥ 0.");

    var groups = new List<DuplicateGroup>();
    var consumed = new bool[files.Count];

    for (var i = 0; i < files.Count; i++) {
      if (consumed[i])
        continue;

      var members = new List<HashedFile> { files[i] };
      var distances = new List<int> { 0 };
      consumed[i] = true;

      for (var j = i + 1; j < files.Count; j++) {
        if (consumed[j])
          continue;
        var d = PerceptualHash.HammingDistance(files[i].Hash, files[j].Hash);
        if (d > threshold)
          continue;
        members.Add(files[j]);
        distances.Add(d);
        consumed[j] = true;
      }

      if (members.Count >= 2)
        groups.Add(new DuplicateGroup(members, distances));
    }

    return groups;
  }
}
