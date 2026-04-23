namespace PhotoManager.Core.Faces;

/// <summary>
/// A group of faces that appear to belong to the same person, derived by
/// agglomerative clustering over embedding vectors. <see cref="Label"/> is
/// the first non-blank name encountered in the cluster; unnamed clusters
/// surface as "Unknown-N" in the gallery so the user can select and name them.
/// </summary>
public sealed record FaceCluster(int Id, IReadOnlyList<ScannedFace> Members) {
  public string? Label => this.Members.Select(m => m.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
  public bool IsNamed => !string.IsNullOrWhiteSpace(this.Label);
  public string DisplayName => this.Label ?? $"Unknown-{this.Id}";
  public int Count => this.Members.Count;
}

/// <summary>
/// Single-linkage agglomerative clustering over face embeddings using cosine
/// similarity. Merges any two clusters if they contain a pair of faces whose
/// similarity meets <see cref="SimilarityThreshold"/>. This is the Picasa
/// behavior — aggressive merging keeps the gallery short; the user cleans up
/// wrong merges by splitting manually.
///
/// Kept fully in-memory and rebuilt each scan — per the no-local-DB rule,
/// XMP is the truth and the cluster index is a view over it.
/// </summary>
public sealed class FaceClusterIndex {
  public float SimilarityThreshold { get; }
  public IReadOnlyList<FaceCluster> Clusters { get; }

  private FaceClusterIndex(float threshold, IReadOnlyList<FaceCluster> clusters) {
    this.SimilarityThreshold = threshold;
    this.Clusters = clusters;
  }

  /// <summary>
  /// Build an index from the scanned faces. Faces with embeddings get
  /// clustered via cosine similarity + same-name seeding; faces without
  /// embeddings become singleton clusters (one face each) so the gallery
  /// still shows them — users without the ArcFace model still benefit from
  /// thumbnails + tagging, just without automatic grouping. Named faces
  /// get merged first so a photo tagged "Alice" pulls nearby unnamed
  /// embedded crops into Alice's cluster.
  /// </summary>
  public static FaceClusterIndex Build(IReadOnlyList<ScannedFace> faces, float similarityThreshold = 0.55f) {
    ArgumentNullException.ThrowIfNull(faces);

    var embedded = faces.Where(f => f.HasEmbedding).ToList();
    var unembedded = faces.Where(f => !f.HasEmbedding).ToList();

    if (embedded.Count == 0 && unembedded.Count == 0)
      return new FaceClusterIndex(similarityThreshold, Array.Empty<FaceCluster>());

    if (embedded.Count == 0)
      return new FaceClusterIndex(similarityThreshold, BuildSingletons(unembedded));

    // Parent pointers for union-find; parent[i] == i means i is a root.
    var parent = new int[embedded.Count];
    for (var i = 0; i < parent.Length; i++)
      parent[i] = i;

    // Seed: force every pair of same-named faces into the same cluster.
    // That way a user who's already named some photos carries the name
    // onto every embedded face that lands in the same component.
    for (var i = 0; i < embedded.Count; i++) {
      if (string.IsNullOrWhiteSpace(embedded[i].Name))
        continue;
      for (var j = i + 1; j < embedded.Count; j++) {
        if (string.Equals(embedded[i].Name, embedded[j].Name, StringComparison.OrdinalIgnoreCase))
          Union(parent, i, j);
      }
    }

    // Similarity-driven merges: O(n²), acceptable for a single library scan.
    for (var i = 0; i < embedded.Count; i++) {
      var a = embedded[i].Embedding!;
      for (var j = i + 1; j < embedded.Count; j++) {
        if (Find(parent, i) == Find(parent, j))
          continue;
        var b = embedded[j].Embedding!;
        if (a.Length != b.Length)
          continue;  // mismatched models — don't cluster across
        var sim = PeopleRegistry.CosineSimilarity(a, b);
        if (sim >= similarityThreshold)
          Union(parent, i, j);
      }
    }

    var groups = new Dictionary<int, List<ScannedFace>>();
    for (var i = 0; i < embedded.Count; i++) {
      var root = Find(parent, i);
      if (!groups.TryGetValue(root, out var list)) {
        list = new List<ScannedFace>();
        groups[root] = list;
      }
      list.Add(embedded[i]);
    }

    // Stable ordering: named clusters first (alphabetical), then unknown
    // clusters by descending size so the biggest blob to label floats up.
    var clusters = groups
      .Select(kv => (Members: (IReadOnlyList<ScannedFace>)kv.Value,
                     Label: kv.Value.Select(m => m.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))))
      .OrderByDescending(t => !string.IsNullOrWhiteSpace(t.Label))
      .ThenBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
      .ThenByDescending(t => t.Members.Count)
      .Select((t, ix) => new FaceCluster(ix + 1, t.Members))
      .ToList();

    // Append singletons for faces without embeddings so they still appear.
    clusters.AddRange(BuildSingletons(unembedded, startId: clusters.Count + 1));

    return new FaceClusterIndex(similarityThreshold, clusters);
  }

  private static IReadOnlyList<FaceCluster> BuildSingletons(IReadOnlyList<ScannedFace> faces, int startId = 1) {
    var result = new List<FaceCluster>(faces.Count);
    for (var i = 0; i < faces.Count; i++)
      result.Add(new FaceCluster(startId + i, new[] { faces[i] }));
    return result;
  }

  /// <summary>
  /// Propagate <paramref name="name"/> onto every face in the cluster by
  /// updating each ScannedFace's Region.Label. Returns the updated (file,
  /// region) pairs so the caller can write them back through the metadata
  /// writer pipeline. Doesn't touch disk — keeps IO in the caller.
  /// </summary>
  public static IReadOnlyList<ScannedFace> ApplyName(FaceCluster cluster, string name) {
    ArgumentNullException.ThrowIfNull(cluster);
    ArgumentException.ThrowIfNullOrWhiteSpace(name);

    return cluster.Members
      .Select(m => new ScannedFace(m.File, m.Region with { Label = name }))
      .ToList();
  }

  private static int Find(int[] parent, int x) {
    while (parent[x] != x) {
      parent[x] = parent[parent[x]];  // path compression
      x = parent[x];
    }
    return x;
  }

  private static void Union(int[] parent, int a, int b) {
    var ra = Find(parent, a);
    var rb = Find(parent, b);
    if (ra == rb)
      return;
    parent[ra] = rb;
  }
}
