using PhotoManager.Core.Detection;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Regions;

namespace PhotoManager.Core.Faces;

/// <summary>
/// Runs face detection and reconciles the results with the sidecar:
///   - Existing face regions in XMP stay put (user-tagged names are never lost).
///   - New detected regions are added, named via registry match when possible.
///   - Named faces contribute their person names as keywords for search.
///
/// Face data lives in the broader <see cref="TaggedRegion"/> store alongside
/// animals, items, and places — faces are just regions with
/// <see cref="RegionCategory.Person"/>. Non-face regions in the sidecar are
/// preserved unchanged on every write.
/// </summary>
public sealed class FaceRecognitionService {
  private readonly IFaceDetector _detector;
  private readonly IMetadataReader _reader;
  private readonly IMetadataWriter _writer;
  private readonly PeopleRegistry _registry;
  private readonly OnnxFaceEmbedder? _embedder;
  private readonly float _iouMergeThreshold;

  public FaceRecognitionService(
    IFaceDetector detector,
    IMetadataReader reader,
    IMetadataWriter writer,
    PeopleRegistry registry,
    OnnxFaceEmbedder? embedder = null,
    float iouMergeThreshold = 0.5f
  ) {
    this._detector = detector ?? throw new ArgumentNullException(nameof(detector));
    this._reader = reader ?? throw new ArgumentNullException(nameof(reader));
    this._writer = writer ?? throw new ArgumentNullException(nameof(writer));
    this._registry = registry ?? throw new ArgumentNullException(nameof(registry));
    this._embedder = embedder;
    this._iouMergeThreshold = iouMergeThreshold;
  }

  public async Task<IReadOnlyList<FaceRegion>> DetectAndWriteAsync(
    FileInfo imageFile,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(imageFile);

    var existing = await this._reader.ReadAsync(imageFile, cancellationToken);
    var detected = await this._detector.DetectAsync(imageFile, cancellationToken);

    // If a face embedder is configured and the detector didn't already
    // produce embeddings, fill them in now — that's what drives the
    // registry auto-match.
    var detectedWithEmbeddings = await this.EmbedMissingAsync(imageFile, detected, cancellationToken);

    var named = detectedWithEmbeddings
      .Select(d => NameFromRegistry(d, this._registry))
      .ToList();

    // Preserve existing regions verbatim (including their Status) — detection
    // must never silently promote or demote a region the user has already
    // curated.
    var regions = existing.Regions.ToList();

    // Merge each new detection:
    //  - No overlap with an existing Person region → add as Proposed. Status
    //    is always Proposed regardless of whether auto-match found a name;
    //    the user explicitly confirms via the Accept button (named
    //    proposals) or Tag button (unnamed proposals).
    //  - Overlaps an existing Person region → adopt the auto-matched name
    //    only if the existing region has no label yet (user tags always win).
    //    Also adopt the embedding if the existing one is missing so the
    //    registry can match this face later.
    for (var di = 0; di < named.Count; di++) {
      var detection = named[di];
      var embedding = detectedWithEmbeddings[di].Embedding;
      var overlapIndex = -1;
      for (var i = 0; i < regions.Count; i++) {
        if (regions[i].Category != RegionCategory.Person)
          continue;
        if (BoxIou(regions[i].Box, detection.Box) >= this._iouMergeThreshold) {
          overlapIndex = i;
          break;
        }
      }

      if (overlapIndex < 0) {
        regions.Add(new TaggedRegion(
          detection.Box,
          RegionCategory.Person,
          Label: detection.PersonName,
          Status: RegionStatus.Proposed,
          Source: TaggedRegion.FaceDetectorSource,
          Embedding: embedding
        ));
      } else {
        var incumbent = regions[overlapIndex];
        var updatedLabel = !string.IsNullOrWhiteSpace(incumbent.Label)
          ? incumbent.Label
          : detection.PersonName;
        var updatedEmbedding = incumbent.Embedding is { Length: > 0 }
          ? incumbent.Embedding
          : embedding;
        regions[overlapIndex] = incumbent with {
          Label = updatedLabel,
          Embedding = updatedEmbedding
        };
      }
    }

    // Suppress YOLO-style whole-body Person regions that now fully contain a
    // face-detector region (body + face together = duplicate tiles / tags).
    var faceRegions = regions
      .Where(r => r.Category == RegionCategory.Person && r.Source == TaggedRegion.FaceDetectorSource)
      .ToList();
    regions = regions.Where(r =>
      !(r.Category == RegionCategory.Person
        && r.Source != TaggedRegion.FaceDetectorSource
        && faceRegions.Any(face => ContainsSmaller(r.Box, face.Box, areaRatio: 0.4f)))
    ).ToList();

    // Only already-Accepted Person region labels land in dc:subject keywords.
    // Proposed detections (named or not) wait for the user to click Accept —
    // matches the YOLO flow and the "everything must be accepted before
    // stored" workflow the user asked for.
    var acceptedPersonLabels = regions
      .Where(r => r.Category == RegionCategory.Person
               && r.Status == RegionStatus.Accepted
               && !string.IsNullOrWhiteSpace(r.Label))
      .Select(r => r.Label!)
      .ToArray();

    var mergedKeywords = DetectionService.MergeKeywords(existing.Keywords, acceptedPersonLabels);

    await this._writer.ApplyAsync(imageFile, new MetadataEdit {
      Regions = Optional<IReadOnlyList<TaggedRegion>>.Set(regions.ToArray()),
      Keywords = Optional<IReadOnlyList<string>>.Set(mergedKeywords)
    }, cancellationToken);

    return regions
      .Where(r => r.Category == RegionCategory.Person)
      .Select(r => new FaceRegion(r.Box, r.Label))
      .ToList();
  }

  /// <summary>
  /// Updates the Nth face region in the sidecar to have <paramref name="name"/>,
  /// and (if an embedding is supplied) adds that embedding as a new reference
  /// for the person in the registry.
  /// </summary>
  public async Task TagFaceAsync(
    FileInfo imageFile,
    int faceIndex,
    string name,
    float[]? embedding = null,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(imageFile);
    ArgumentException.ThrowIfNullOrWhiteSpace(name);

    var existing = await this._reader.ReadAsync(imageFile, cancellationToken);
    var faces = existing.Faces;
    if (faceIndex < 0 || faceIndex >= faces.Count)
      throw new ArgumentOutOfRangeException(nameof(faceIndex),
        $"Sidecar has {faces.Count} face region(s).");

    var targetBox = faces[faceIndex].Box;

    // Rebuild the regions list: find the Person-category region whose box
    // matches the target, rename it, leave everything else alone.
    var updatedRegions = existing.Regions
      .Select(r => (r.Category == RegionCategory.Person
                    && r.Status == RegionStatus.Accepted
                    && r.Box.Equals(targetBox))
        ? r with { Label = name }
        : r)
      .ToArray();

    var mergedKeywords = DetectionService.MergeKeywords(existing.Keywords, new[] { name });

    await this._writer.ApplyAsync(imageFile, new MetadataEdit {
      Regions = Optional<IReadOnlyList<TaggedRegion>>.Set(updatedRegions),
      Keywords = Optional<IReadOnlyList<string>>.Set(mergedKeywords)
    }, cancellationToken);

    if (embedding != null && embedding.Length > 0)
      this._registry.AddReference(name, embedding);
  }

  /// <summary>
  /// Merges <paramref name="detected"/> regions into <paramref name="existing"/>:
  /// a detected region is considered the same face as an existing region
  /// when their bounding boxes overlap above <paramref name="iouThreshold"/>.
  /// When merged, the existing region's name wins (user tags are authoritative);
  /// detected embeddings can still update the registry separately.
  /// </summary>
  internal static IReadOnlyList<FaceRegion> MergeRegions(
    IReadOnlyList<FaceRegion> existing,
    IEnumerable<FaceRegion> detected,
    float iouThreshold
  ) {
    var result = existing.ToList();

    foreach (var candidate in detected) {
      var matchIndex = FindOverlap(result, candidate, iouThreshold);
      if (matchIndex < 0) {
        result.Add(candidate);
        continue;
      }

      var incumbent = result[matchIndex];
      if (string.IsNullOrEmpty(incumbent.PersonName) && !string.IsNullOrEmpty(candidate.PersonName))
        result[matchIndex] = incumbent with { PersonName = candidate.PersonName };
    }

    return result;
  }

  private static int FindOverlap(List<FaceRegion> regions, FaceRegion candidate, float iouThreshold) {
    for (var i = 0; i < regions.Count; i++) {
      if (BoxIou(regions[i].Box, candidate.Box) >= iouThreshold)
        return i;
    }
    return -1;
  }

  /// <summary>
  /// True when <paramref name="inner"/> lies fully inside <paramref name="outer"/>
  /// AND is noticeably smaller (inner area ≤ <paramref name="areaRatio"/> ×
  /// outer area). A whole-body YOLO box that contains a face crop satisfies
  /// this easily (face ≈ 5-15% of body area), so the body can be dropped
  /// without worrying about dismissing a meaningfully-different region.
  /// </summary>
  internal static bool ContainsSmaller(NormalizedBoundingBox outer, NormalizedBoundingBox inner, float areaRatio) {
    // Small tolerance lets a face that juts slightly past the body's edge
    // (e.g. when the body box was tight) still count as contained.
    const float tolerance = 0.02f;
    var fullyInside =
      inner.X >= outer.X - tolerance
      && inner.Y >= outer.Y - tolerance
      && inner.X + inner.Width  <= outer.X + outer.Width  + tolerance
      && inner.Y + inner.Height <= outer.Y + outer.Height + tolerance;
    if (!fullyInside)
      return false;
    var outerArea = outer.Width * outer.Height;
    var innerArea = inner.Width * inner.Height;
    return outerArea > 0 && innerArea <= outerArea * areaRatio;
  }

  internal static float BoxIou(NormalizedBoundingBox a, NormalizedBoundingBox b) {
    var aRight = a.X + a.Width;
    var aBottom = a.Y + a.Height;
    var bRight = b.X + b.Width;
    var bBottom = b.Y + b.Height;

    var interLeft = Math.Max(a.X, b.X);
    var interTop = Math.Max(a.Y, b.Y);
    var interRight = Math.Min(aRight, bRight);
    var interBottom = Math.Min(aBottom, bBottom);

    if (interRight <= interLeft || interBottom <= interTop)
      return 0f;

    var intersection = (interRight - interLeft) * (interBottom - interTop);
    var union = a.Width * a.Height + b.Width * b.Height - intersection;
    return union <= 0 ? 0f : intersection / union;
  }

  /// <summary>
  /// Runs the embedder against every detected face that doesn't already
  /// carry an embedding. If the embedder isn't configured or its model
  /// isn't loaded, the input list is returned as-is.
  /// </summary>
  private async Task<IReadOnlyList<DetectedFace>> EmbedMissingAsync(
    FileInfo imageFile,
    IReadOnlyList<DetectedFace> detected,
    CancellationToken cancellationToken
  ) {
    if (this._embedder is null || !this._embedder.IsAvailable || detected.Count == 0)
      return detected;

    var result = new List<DetectedFace>(detected.Count);
    foreach (var face in detected) {
      if (face.Embedding is not null) {
        result.Add(face);
        continue;
      }

      var embedding = await this._embedder.EmbedFaceAsync(imageFile, face.Region.Box, cancellationToken);
      result.Add(embedding == null ? face : face with { Embedding = embedding });
    }
    return result;
  }

  private static FaceRegion NameFromRegistry(DetectedFace detected, PeopleRegistry registry) {
    if (!string.IsNullOrEmpty(detected.Region.PersonName))
      return detected.Region;

    if (detected.Embedding is { } embedding) {
      var match = registry.FindMatch(embedding);
      if (match != null)
        return detected.Region with { PersonName = match };
    }

    return detected.Region;
  }
}
