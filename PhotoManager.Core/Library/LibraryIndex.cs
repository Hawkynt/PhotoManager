using PhotoManager.Core.Interfaces;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Regions;
using PhotoManager.Core.Services;

namespace PhotoManager.Core.Library;

/// <summary>
/// What the user is searching for. Each field that's non-null contributes
/// an AND clause; fields that are null are ignored. Keyword/person/location
/// do substring matches case-insensitively; <see cref="AnyText"/> searches
/// across title, caption, keywords, person names, and all location fields —
/// the fallback when the user doesn't care where the term lives.
/// </summary>
public sealed record LibrarySearchQuery(
  string? AnyText = null,
  string? Keyword = null,
  string? Person = null,
  string? Location = null,
  int? MinRating = null,
  string? ColorLabel = null
) {
  public bool IsEmpty =>
    string.IsNullOrWhiteSpace(this.AnyText)
    && string.IsNullOrWhiteSpace(this.Keyword)
    && string.IsNullOrWhiteSpace(this.Person)
    && string.IsNullOrWhiteSpace(this.Location)
    && this.MinRating is null
    && string.IsNullOrWhiteSpace(this.ColorLabel);
}

/// <summary>One hit from a library search.</summary>
public sealed record SearchHit(FileInfo File, FullMetadata Metadata);

/// <summary>
/// Walks a directory, reads metadata via <see cref="MetadataCache"/>, and
/// serves searches from memory. Rebuilt on demand via <see cref="ScanAsync"/>;
/// re-running the scan is cheap because the cache skips unchanged files by
/// <c>LastWriteTimeUtc</c>.
/// </summary>
public sealed class LibraryIndex {
  private readonly MetadataCache _cache;
  private readonly ISupportedFormatsService _formats;

  public LibraryIndex(MetadataCache cache, ISupportedFormatsService? formats = null) {
    ArgumentNullException.ThrowIfNull(cache);
    this._cache = cache;
    this._formats = formats ?? new SupportedFormatsService();
  }

  public MetadataCache Cache => this._cache;

  /// <summary>
  /// Populates the cache by walking <paramref name="root"/>. Reports the file
  /// currently being processed via <paramref name="progress"/> so long scans
  /// don't look hung. Returns the number of files visited.
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
    var visited = 0;

    // Collect the file list first so Parallel.ForEachAsync can partition it.
    var files = root.EnumerateFiles("*", option)
      .Where(f => extensions.Contains(f.Extension))
      .ToList();

    var maxDop = Math.Min(8, Environment.ProcessorCount);
    await Parallel.ForEachAsync(files,
      new ParallelOptions {
        MaxDegreeOfParallelism = maxDop,
        CancellationToken = cancellationToken
      },
      async (file, ct) => {
        progress?.Report(file);
        try {
          await this._cache.GetAsync(file, ct);
        } catch (OperationCanceledException) {
          throw;
        } catch {
          // Skip unreadable files without aborting the whole scan.
        }
        Interlocked.Increment(ref visited);
      });

    return visited;
  }

  /// <summary>
  /// Every unique person name (from Person regions with a non-blank label)
  /// across the cached library. Handy for driving a "Who's in my photos"
  /// dropdown in the search UI.
  /// </summary>
  public IReadOnlyList<string> DistinctPeople() => this._cache.Snapshot()
    .SelectMany(s => s.Metadata.Regions)
    .Where(r => r.Category == RegionCategory.Person && !string.IsNullOrWhiteSpace(r.Label))
    .Select(r => r.Label!)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
    .ToList();

  /// <summary>Every unique keyword across the cached library.</summary>
  public IReadOnlyList<string> DistinctKeywords() => this._cache.Snapshot()
    .SelectMany(s => s.Metadata.Keywords)
    .Where(k => !string.IsNullOrWhiteSpace(k))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
    .ToList();

  /// <summary>Every unique location/city/state/country across the cached library.</summary>
  public IReadOnlyList<string> DistinctLocations() => this._cache.Snapshot()
    .SelectMany(s => new[] { s.Metadata.Location, s.Metadata.City, s.Metadata.State, s.Metadata.Country })
    .Where(s => !string.IsNullOrWhiteSpace(s))
    .Select(s => s!)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
    .ToList();

  /// <summary>
  /// Run the query against the cached library. Sync because the data is all
  /// in memory; the caller decides whether to dispatch on a background thread.
  /// </summary>
  public IReadOnlyList<SearchHit> Search(LibrarySearchQuery query) {
    ArgumentNullException.ThrowIfNull(query);

    var hits = new List<SearchHit>();
    foreach (var (file, metadata) in this._cache.Snapshot()) {
      if (!Matches(metadata, query))
        continue;
      hits.Add(new SearchHit(file, metadata));
    }

    // Newest-first is the most useful default for a photo library.
    hits.Sort((a, b) => b.File.LastWriteTimeUtc.CompareTo(a.File.LastWriteTimeUtc));
    return hits;
  }

  internal static bool Matches(FullMetadata metadata, LibrarySearchQuery query) {
    if (!string.IsNullOrWhiteSpace(query.Keyword)
        && !metadata.Keywords.Any(k => ContainsIgnoreCase(k, query.Keyword)))
      return false;

    if (!string.IsNullOrWhiteSpace(query.Person)
        && !metadata.Regions.Any(r => r.Category == RegionCategory.Person
                                   && r.Label is { } l
                                   && ContainsIgnoreCase(l, query.Person)))
      return false;

    if (!string.IsNullOrWhiteSpace(query.Location)) {
      var needle = query.Location;
      var hay = new[] { metadata.Location, metadata.City, metadata.State, metadata.Country, metadata.CountryCode };
      if (!hay.Any(s => s is { } v && ContainsIgnoreCase(v, needle)))
        return false;
    }

    if (query.MinRating is { } minRating && (metadata.Rating ?? 0) < minRating)
      return false;

    if (!string.IsNullOrWhiteSpace(query.ColorLabel)
        && !string.Equals(metadata.ColorLabel, query.ColorLabel, StringComparison.OrdinalIgnoreCase))
      return false;

    if (!string.IsNullOrWhiteSpace(query.AnyText)) {
      var needle = query.AnyText;
      var haystack = AllSearchableText(metadata);
      if (!haystack.Any(s => ContainsIgnoreCase(s, needle)))
        return false;
    }

    return true;
  }

  private static IEnumerable<string> AllSearchableText(FullMetadata metadata) {
    if (metadata.Title is { } t) yield return t;
    if (metadata.Caption is { } c) yield return c;
    if (metadata.Location is { } l) yield return l;
    if (metadata.City is { } city) yield return city;
    if (metadata.State is { } st) yield return st;
    if (metadata.Country is { } co) yield return co;
    if (metadata.CountryCode is { } cc) yield return cc;
    foreach (var k in metadata.Keywords)
      yield return k;
    foreach (var r in metadata.Regions) {
      if (r.Label is { } label)
        yield return label;
    }
  }

  private static bool ContainsIgnoreCase(string haystack, string needle)
    => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
