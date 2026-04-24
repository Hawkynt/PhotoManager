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
  /// Build an index from the scanned faces. Two modes:
  /// <list type="bullet">
  ///   <item><description><paramref name="groupByName"/> = true (default):
  ///   faces that share the EXACT same non-blank name merge into one
  ///   cluster. Unnamed faces stay as singletons. Two faces that happen to
  ///   have similar embeddings but different names NEVER merge — the
  ///   previous similarity-based path could silently join unrelated faces,
  ///   especially when embeddings picked up shared lighting / pose from a
  ///   single photo.</description></item>
  ///   <item><description><paramref name="groupByName"/> = false: every
  ///   face is its own cluster. Useful when the user wants to review each
  ///   face individually before any auto-grouping.</description></item>
  /// </list>
  /// </summary>
  public static FaceClusterIndex Build(IReadOnlyList<ScannedFace> faces, bool groupByName = true) {
    ArgumentNullException.ThrowIfNull(faces);

    if (faces.Count == 0)
      return new FaceClusterIndex(0f, Array.Empty<FaceCluster>());

    if (!groupByName)
      return new FaceClusterIndex(0f, BuildSingletons(faces));

    // Name-keyed buckets. Unnamed faces bypass the bucket and become
    // singletons so they surface one tile per face for explicit tagging.
    var buckets = new Dictionary<string, List<ScannedFace>>(StringComparer.OrdinalIgnoreCase);
    var unnamed = new List<ScannedFace>();
    foreach (var face in faces) {
      if (string.IsNullOrWhiteSpace(face.Name)) {
        unnamed.Add(face);
        continue;
      }
      if (!buckets.TryGetValue(face.Name!, out var list)) {
        list = new List<ScannedFace>();
        buckets[face.Name!] = list;
      }
      list.Add(face);
    }

    // Named clusters alphabetical (so "Alice" lands before "Bob"), then
    // unnamed singletons appended. IDs are stable and sequential so the
    // gallery displays deterministic order across scans.
    var clusters = new List<FaceCluster>();
    var nextId = 1;
    foreach (var kv in buckets.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
      clusters.Add(new FaceCluster(nextId++, kv.Value));
    foreach (var face in unnamed)
      clusters.Add(new FaceCluster(nextId++, new[] { face }));

    return new FaceClusterIndex(0f, clusters);
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
