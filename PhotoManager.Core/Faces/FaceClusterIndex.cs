namespace Hawkynt.PhotoManager.Core.Faces;

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
  public const float DefaultSimilarityThreshold = 0.6f;

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

  /// <summary>
  /// Build an index with embedding-similarity-based clustering for unnamed
  /// faces. Named faces still group by exact name. Unnamed faces that have
  /// embeddings are clustered using single-linkage agglomerative clustering:
  /// two clusters merge when ANY pair across them has cosine similarity
  /// &gt;= <paramref name="similarityThreshold"/>. Unnamed faces without
  /// embeddings remain singletons.
  ///
  /// Named faces with different names NEVER merge — embeddings only govern
  /// unnamed-to-unnamed grouping.
  /// </summary>
  public static FaceClusterIndex BuildWithEmbeddingClustering(
    IReadOnlyList<ScannedFace> faces,
    float similarityThreshold = DefaultSimilarityThreshold
  ) {
    ArgumentNullException.ThrowIfNull(faces);
    if (similarityThreshold < 0f || similarityThreshold > 1f)
      throw new ArgumentOutOfRangeException(nameof(similarityThreshold), "Threshold must be between 0.0 and 1.0.");

    if (faces.Count == 0)
      return new FaceClusterIndex(similarityThreshold, Array.Empty<FaceCluster>());

    // ---- Step 1: separate named from unnamed ----
    var buckets = new Dictionary<string, List<ScannedFace>>(StringComparer.OrdinalIgnoreCase);
    var unnamedWithEmbedding = new List<(ScannedFace Face, int OriginalIndex)>();
    var unnamedWithoutEmbedding = new List<ScannedFace>();

    for (var i = 0; i < faces.Count; i++) {
      var face = faces[i];
      if (!string.IsNullOrWhiteSpace(face.Name)) {
        if (!buckets.TryGetValue(face.Name!, out var list)) {
          list = new List<ScannedFace>();
          buckets[face.Name!] = list;
        }
        list.Add(face);
      } else if (face.HasEmbedding) {
        unnamedWithEmbedding.Add((face, i));
      } else {
        unnamedWithoutEmbedding.Add(face);
      }
    }

    // ---- Step 2: cluster unnamed faces with embeddings via union-find ----
    var n = unnamedWithEmbedding.Count;
    var parent = new int[n];
    for (var i = 0; i < n; i++)
      parent[i] = i;

    // O(n^2) pairwise comparison — acceptable for typical face counts
    // (hundreds, not millions). Single-linkage: merge if ANY pair meets
    // the threshold.
    for (var i = 0; i < n; i++) {
      var embA = unnamedWithEmbedding[i].Face.Embedding!;
      for (var j = i + 1; j < n; j++) {
        if (Find(parent, i) == Find(parent, j))
          continue; // already in the same cluster
        var embB = unnamedWithEmbedding[j].Face.Embedding!;
        if (embA.Length != embB.Length)
          continue; // dimension mismatch — skip
        var sim = PeopleRegistry.CosineSimilarity(embA, embB);
        if (sim >= similarityThreshold)
          Union(parent, i, j);
      }
    }

    // Collect unnamed clusters by root
    var unnamedClusters = new Dictionary<int, List<ScannedFace>>();
    for (var i = 0; i < n; i++) {
      var root = Find(parent, i);
      if (!unnamedClusters.TryGetValue(root, out var list)) {
        list = new List<ScannedFace>();
        unnamedClusters[root] = list;
      }
      list.Add(unnamedWithEmbedding[i].Face);
    }

    // ---- Step 3: assemble final cluster list ----
    var clusters = new List<FaceCluster>();
    var nextId = 1;

    // Named clusters first, alphabetical
    foreach (var kv in buckets.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
      clusters.Add(new FaceCluster(nextId++, kv.Value));

    // Unnamed embedding clusters, ordered by size descending (largest groups first)
    foreach (var kv in unnamedClusters.OrderByDescending(c => c.Value.Count))
      clusters.Add(new FaceCluster(nextId++, kv.Value));

    // Unnamed faces without embeddings as singletons at the end
    foreach (var face in unnamedWithoutEmbedding)
      clusters.Add(new FaceCluster(nextId++, new[] { face }));

    return new FaceClusterIndex(similarityThreshold, clusters);
  }

  /// <summary>
  /// Compute the centroid (mean embedding) of a cluster's members that have
  /// embeddings. Returns null if no member has an embedding or if dimension
  /// lengths are inconsistent.
  /// </summary>
  public static float[]? ComputeCentroid(FaceCluster cluster) {
    ArgumentNullException.ThrowIfNull(cluster);

    var withEmbedding = cluster.Members
      .Where(m => m.HasEmbedding)
      .Select(m => m.Embedding!)
      .ToList();

    if (withEmbedding.Count == 0)
      return null;

    var dim = withEmbedding[0].Length;
    if (withEmbedding.Any(e => e.Length != dim))
      return null; // inconsistent dimensions

    var centroid = new float[dim];
    foreach (var emb in withEmbedding)
      for (var i = 0; i < dim; i++)
        centroid[i] += emb[i];

    for (var i = 0; i < dim; i++)
      centroid[i] /= withEmbedding.Count;

    return OnnxFaceEmbedder.L2Normalize(centroid);
  }

  /// <summary>
  /// Find the top-N named clusters nearest to <paramref name="target"/> by
  /// centroid-to-centroid cosine similarity. Returns pairs of (cluster,
  /// similarity) sorted by descending similarity. Only considers named
  /// clusters that have at least one member with an embedding.
  /// </summary>
  public IReadOnlyList<(FaceCluster Cluster, float Similarity)> FindNearestNamedClusters(
    FaceCluster target,
    int topN = 5
  ) {
    ArgumentNullException.ThrowIfNull(target);
    if (topN <= 0)
      throw new ArgumentOutOfRangeException(nameof(topN));

    var targetCentroid = ComputeCentroid(target);
    if (targetCentroid == null)
      return Array.Empty<(FaceCluster, float)>();

    var candidates = new List<(FaceCluster Cluster, float Similarity)>();
    foreach (var cluster in this.Clusters) {
      if (!cluster.IsNamed || cluster == target)
        continue;

      var centroid = ComputeCentroid(cluster);
      if (centroid == null || centroid.Length != targetCentroid.Length)
        continue;

      var sim = PeopleRegistry.CosineSimilarity(targetCentroid, centroid);
      candidates.Add((cluster, sim));
    }

    return candidates
      .OrderByDescending(c => c.Similarity)
      .Take(topN)
      .ToList();
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
