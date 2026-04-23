using PhotoManager.Core.Detection;
using PhotoManager.Core.Metadata;

namespace PhotoManager.Core.Regions;

/// <summary>
/// High-level operations on tagged regions. Wraps the sidecar read/write
/// pattern so callers (CLI + UI) don't have to deal with MetadataEdit
/// construction for simple add/accept/discard flows. Accepted region
/// labels propagate to <c>dc:subject</c> keywords so search picks them up.
/// </summary>
public sealed class RegionService {
  private readonly IMetadataReader _reader;
  private readonly IMetadataWriter _writer;

  public RegionService(IMetadataReader reader, IMetadataWriter writer) {
    this._reader = reader ?? throw new ArgumentNullException(nameof(reader));
    this._writer = writer ?? throw new ArgumentNullException(nameof(writer));
  }

  public async Task<IReadOnlyList<TaggedRegion>> ListAsync(FileInfo imageFile, CancellationToken cancellationToken = default) {
    var md = await this._reader.ReadAsync(imageFile, cancellationToken);
    return md.Regions;
  }

  /// <summary>
  /// Appends <paramref name="newRegions"/> to the existing regions in the
  /// sidecar, deduping by exact box + category overlap so running a
  /// proposer repeatedly doesn't pile up duplicates. Labels on any
  /// already-accepted regions flow into <c>dc:subject</c> keywords so
  /// search picks them up — proposed regions stay out of keywords until
  /// the user explicitly accepts them.
  /// </summary>
  public async Task AppendAsync(FileInfo imageFile, IReadOnlyList<TaggedRegion> newRegions, CancellationToken cancellationToken = default) {
    var md = await this._reader.ReadAsync(imageFile, cancellationToken);
    var merged = DedupeByBoxAndCategory(md.Regions.Concat(newRegions));

    var acceptedLabels = newRegions
      .Where(r => r.Status == RegionStatus.Accepted && !string.IsNullOrWhiteSpace(r.Label))
      .Select(r => r.Label!)
      .ToArray();

    var edit = new MetadataEdit {
      Regions = Optional<IReadOnlyList<TaggedRegion>>.Set(merged)
    };

    if (acceptedLabels.Length > 0) {
      var mergedKeywords = DetectionService.MergeKeywords(md.Keywords, acceptedLabels);
      edit = edit with { Keywords = Optional<IReadOnlyList<string>>.Set(mergedKeywords) };
    }

    await this._writer.ApplyAsync(imageFile, edit, cancellationToken);
  }

  /// <summary>
  /// Flips the Nth region from Proposed to Accepted and promotes its
  /// label into <c>dc:subject</c> keywords.
  /// </summary>
  public async Task AcceptAsync(FileInfo imageFile, int index, CancellationToken cancellationToken = default) {
    var md = await this._reader.ReadAsync(imageFile, cancellationToken);
    ValidateIndex(index, md.Regions.Count);

    var updated = md.Regions
      .Select((r, i) => i == index ? r with { Status = RegionStatus.Accepted } : r)
      .ToArray();

    var keywordsDelta = string.IsNullOrWhiteSpace(updated[index].Label)
      ? md.Keywords
      : DetectionService.MergeKeywords(md.Keywords, new[] { updated[index].Label! });

    await this._writer.ApplyAsync(imageFile, new MetadataEdit {
      Regions = Optional<IReadOnlyList<TaggedRegion>>.Set(updated),
      Keywords = Optional<IReadOnlyList<string>>.Set(keywordsDelta)
    }, cancellationToken);
  }

  /// <summary>
  /// Removes the Nth region from the sidecar entirely. Keywords are not
  /// removed — if the region's label happens to appear in keywords it
  /// probably came from another source (user tag, another region) and
  /// silently dropping it would surprise.
  /// </summary>
  public async Task DiscardAsync(FileInfo imageFile, int index, CancellationToken cancellationToken = default) {
    var md = await this._reader.ReadAsync(imageFile, cancellationToken);
    ValidateIndex(index, md.Regions.Count);

    var updated = md.Regions
      .Where((_, i) => i != index)
      .ToArray();

    await this._writer.ApplyAsync(imageFile, new MetadataEdit {
      Regions = Optional<IReadOnlyList<TaggedRegion>>.Set(updated)
    }, cancellationToken);
  }

  public async Task RelabelAsync(FileInfo imageFile, int index, string newLabel, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(newLabel);

    var md = await this._reader.ReadAsync(imageFile, cancellationToken);
    ValidateIndex(index, md.Regions.Count);

    var updated = md.Regions
      .Select((r, i) => i == index ? r with { Label = newLabel } : r)
      .ToArray();

    // Promote the new label into dc:subject keywords so library search picks
    // it up (e.g. right-click-tagging a region "Tim" makes the photo searchable
    // via keyword "Tim" too — matches what Accept does for proposed regions).
    var mergedKeywords = DetectionService.MergeKeywords(md.Keywords, new[] { newLabel });

    await this._writer.ApplyAsync(imageFile, new MetadataEdit {
      Regions = Optional<IReadOnlyList<TaggedRegion>>.Set(updated),
      Keywords = Optional<IReadOnlyList<string>>.Set(mergedKeywords)
    }, cancellationToken);
  }

  internal static IReadOnlyList<TaggedRegion> DedupeByBoxAndCategory(IEnumerable<TaggedRegion> regions) {
    var seen = new HashSet<(float, float, float, float, RegionCategory)>();
    var result = new List<TaggedRegion>();

    foreach (var region in regions) {
      var key = (
        MathF.Round(region.Box.X, 3),
        MathF.Round(region.Box.Y, 3),
        MathF.Round(region.Box.Width, 3),
        MathF.Round(region.Box.Height, 3),
        region.Category
      );

      if (seen.Add(key))
        result.Add(region);
    }

    return result;
  }

  private static void ValidateIndex(int index, int count) {
    if (index < 0 || index >= count)
      throw new ArgumentOutOfRangeException(nameof(index),
        $"Sidecar has {count} region(s).");
  }
}
