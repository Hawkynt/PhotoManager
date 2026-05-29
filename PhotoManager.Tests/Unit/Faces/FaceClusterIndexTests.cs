using PhotoManager.Core.Detection;
using PhotoManager.Core.Faces;
using PhotoManager.Core.Regions;

namespace PhotoManager.Tests.Unit.Faces;

[TestFixture]
public class FaceClusterIndexTests {
  private static ScannedFace Face(string fileName, float[] embedding, string? name = null) {
    var file = new FileInfo(Path.Combine(Path.GetTempPath(), fileName));
    var region = new TaggedRegion(
      new NormalizedBoundingBox(0, 0, 0.1f, 0.1f),
      RegionCategory.Person,
      Label: name,
      Embedding: embedding
    );
    return new ScannedFace(file, region);
  }

  private static float[] Unit(params float[] v) => OnnxFaceEmbedder.L2Normalize(v);

  [Test]
  public void Build_EmptyInput_YieldsZeroClusters() {
    var index = FaceClusterIndex.Build(Array.Empty<ScannedFace>());
    Assert.That(index.Clusters, Is.Empty);
  }

  [Test]
  public void Build_FacesWithoutEmbeddings_BecomeSingletons() {
    // Unembedded faces become singleton clusters so the gallery shows them
    // even when the ArcFace model isn't installed.
    var faces = new[] {
      new ScannedFace(
        new FileInfo("a.jpg"),
        new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Person)
      ),
      new ScannedFace(
        new FileInfo("b.jpg"),
        new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Person)
      )
    };
    var index = FaceClusterIndex.Build(faces);
    Assert.Multiple(() => {
      Assert.That(index.Clusters, Has.Count.EqualTo(2));
      Assert.That(index.Clusters.All(c => c.Count == 1), Is.True);
    });
  }

  [Test]
  public void Build_ClosePair_ButDifferentNames_StaySeparate() {
    // Similarity is irrelevant — the only thing that merges faces is an
    // identical non-blank name. Two faces with near-identical embeddings
    // but different labels must stay in different clusters.
    var a = Unit(1, 0, 0);
    var b = Unit(0.99f, 0.1f, 0);
    var faces = new[] { Face("a.jpg", a, "Alice"), Face("b.jpg", b, "Bob") };

    var index = FaceClusterIndex.Build(faces);

    Assert.That(index.Clusters, Has.Count.EqualTo(2));
  }

  [Test]
  public void Build_ClosePair_BothUnnamed_BecomeTwoSingletons() {
    // Unnamed faces NEVER merge, regardless of embedding similarity —
    // the user must explicitly name them first.
    var a = Unit(1, 0, 0);
    var b = Unit(0.99f, 0.1f, 0);
    var faces = new[] { Face("a.jpg", a), Face("b.jpg", b) };

    var index = FaceClusterIndex.Build(faces);

    Assert.Multiple(() => {
      Assert.That(index.Clusters, Has.Count.EqualTo(2));
      Assert.That(index.Clusters.All(c => c.Count == 1), Is.True);
    });
  }

  [Test]
  public void Build_DistantEmbeddings_StayApart() {
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0)),
      Face("b.jpg", Unit(0, 1, 0)),
      Face("c.jpg", Unit(0, 0, 1))
    };

    var index = FaceClusterIndex.Build(faces);

    Assert.That(index.Clusters, Has.Count.EqualTo(3));
  }

  [Test]
  public void Build_NoGrouping_EveryFaceIsSingleton_EvenWithMatchingNames() {
    // groupByName=false disables bucketing entirely — the user gets one
    // tile per face, even when names already match.
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0), "Alice"),
      Face("b.jpg", Unit(0, 1, 0), "Alice"),
      Face("c.jpg", Unit(0, 0, 1))
    };

    var index = FaceClusterIndex.Build(faces, groupByName: false);

    Assert.Multiple(() => {
      Assert.That(index.Clusters, Has.Count.EqualTo(3));
      Assert.That(index.Clusters.All(c => c.Count == 1), Is.True);
    });
  }

  [Test]
  public void Build_SameName_ForcedIntoSameCluster_EvenIfDistant() {
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0), "Alice"),
      Face("b.jpg", Unit(0, 1, 0), "Alice")  // far from a geometrically, but same label
    };

    var index = FaceClusterIndex.Build(faces);

    Assert.Multiple(() => {
      Assert.That(index.Clusters, Has.Count.EqualTo(1));
      Assert.That(index.Clusters[0].Label, Is.EqualTo("Alice"));
    });
  }

  [Test]
  public void Build_NamedClustersSortFirst() {
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0)),
      Face("b.jpg", Unit(0, 1, 0), "Alice"),
      Face("c.jpg", Unit(0, 0, 1))
    };

    var index = FaceClusterIndex.Build(faces);

    Assert.Multiple(() => {
      Assert.That(index.Clusters, Has.Count.EqualTo(3));
      Assert.That(index.Clusters[0].Label, Is.EqualTo("Alice"));
      Assert.That(index.Clusters[1].Label, Is.Null);
      Assert.That(index.Clusters[1].DisplayName, Does.StartWith("Unknown-"));
    });
  }

  [Test]
  public void ApplyName_ReturnsMembersWithUpdatedLabel_WithoutMutatingOriginal() {
    // Use two faces with the same name so they end up in one cluster.
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0), "Bob"),
      Face("b.jpg", Unit(0.99f, 0.1f, 0), "Bob")
    };
    var index = FaceClusterIndex.Build(faces);
    var cluster = index.Clusters.Single();

    var renamed = FaceClusterIndex.ApplyName(cluster, "Alice");

    Assert.Multiple(() => {
      Assert.That(renamed, Has.Count.EqualTo(2));
      Assert.That(renamed, Is.All.Matches<ScannedFace>(f => f.Name == "Alice"));
      // original members unchanged — records are immutable
      Assert.That(cluster.Members, Is.All.Matches<ScannedFace>(f => f.Name == "Bob"));
    });
  }

  [Test]
  public void Build_MismatchedDimensions_DoNotCluster() {
    // Both unnamed → two singletons regardless of dimension. (No similarity
    // logic exists any more, so this mostly documents that embeddings do
    // not influence cluster membership at all.)
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0)),
      Face("b.jpg", Unit(1, 0, 0, 0, 0))
    };

    var index = FaceClusterIndex.Build(faces);

    Assert.That(index.Clusters, Has.Count.EqualTo(2));
  }

  // =====================================================================
  // BuildWithEmbeddingClustering tests
  // =====================================================================

  [Test]
  public void EmbeddingClustering_EmptyInput_YieldsZeroClusters() {
    var index = FaceClusterIndex.BuildWithEmbeddingClustering(Array.Empty<ScannedFace>());
    Assert.That(index.Clusters, Is.Empty);
  }

  [Test]
  public void EmbeddingClustering_SimilarUnnamedFaces_MergeIntoOneCluster() {
    // Two unnamed faces with very similar embeddings should cluster together.
    var a = Unit(1, 0, 0);
    var b = Unit(0.99f, 0.1f, 0);  // very close to a
    var faces = new[] { Face("a.jpg", a), Face("b.jpg", b) };

    var index = FaceClusterIndex.BuildWithEmbeddingClustering(faces, similarityThreshold: 0.6f);

    Assert.Multiple(() => {
      Assert.That(index.Clusters, Has.Count.EqualTo(1));
      Assert.That(index.Clusters[0].Count, Is.EqualTo(2));
      Assert.That(index.Clusters[0].IsNamed, Is.False);
      Assert.That(index.Clusters[0].DisplayName, Does.StartWith("Unknown-"));
    });
  }

  [Test]
  public void EmbeddingClustering_DissimilarUnnamedFaces_StaySeparate() {
    // Two unnamed faces with orthogonal embeddings should stay separate.
    var a = Unit(1, 0, 0);
    var b = Unit(0, 1, 0);
    var faces = new[] { Face("a.jpg", a), Face("b.jpg", b) };

    var index = FaceClusterIndex.BuildWithEmbeddingClustering(faces, similarityThreshold: 0.6f);

    Assert.Multiple(() => {
      Assert.That(index.Clusters, Has.Count.EqualTo(2));
      Assert.That(index.Clusters.All(c => c.Count == 1), Is.True);
    });
  }

  [Test]
  public void EmbeddingClustering_NamedFaces_GroupByName_NotEmbedding() {
    // Named faces must group by exact name, even if embeddings are dissimilar.
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0), "Alice"),
      Face("b.jpg", Unit(0, 1, 0), "Alice")
    };

    var index = FaceClusterIndex.BuildWithEmbeddingClustering(faces);

    Assert.Multiple(() => {
      Assert.That(index.Clusters, Has.Count.EqualTo(1));
      Assert.That(index.Clusters[0].Label, Is.EqualTo("Alice"));
      Assert.That(index.Clusters[0].Count, Is.EqualTo(2));
    });
  }

  [Test]
  public void EmbeddingClustering_DifferentNamedFaces_NeverMerge() {
    // Even if embeddings are nearly identical, different names stay apart.
    var a = Unit(1, 0, 0);
    var b = Unit(0.999f, 0.01f, 0);
    var faces = new[] { Face("a.jpg", a, "Alice"), Face("b.jpg", b, "Bob") };

    var index = FaceClusterIndex.BuildWithEmbeddingClustering(faces);

    Assert.That(index.Clusters, Has.Count.EqualTo(2));
  }

  [Test]
  public void EmbeddingClustering_MixedNamedAndUnnamed_NamedSortFirst() {
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0)),          // unnamed
      Face("b.jpg", Unit(0, 1, 0), "Alice"), // named
      Face("c.jpg", Unit(0, 0, 1))           // unnamed
    };

    var index = FaceClusterIndex.BuildWithEmbeddingClustering(faces);

    Assert.Multiple(() => {
      Assert.That(index.Clusters[0].Label, Is.EqualTo("Alice"));
      // unnamed clusters follow
      Assert.That(index.Clusters.Skip(1).All(c => !c.IsNamed), Is.True);
    });
  }

  [Test]
  public void EmbeddingClustering_UnnamedWithoutEmbeddings_BecomeSingletons() {
    // Faces without embeddings can't be clustered — they become singletons.
    var faces = new[] {
      new ScannedFace(
        new FileInfo("a.jpg"),
        new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Person)
      ),
      new ScannedFace(
        new FileInfo("b.jpg"),
        new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Person)
      )
    };

    var index = FaceClusterIndex.BuildWithEmbeddingClustering(faces);

    Assert.Multiple(() => {
      Assert.That(index.Clusters, Has.Count.EqualTo(2));
      Assert.That(index.Clusters.All(c => c.Count == 1), Is.True);
    });
  }

  [Test]
  public void EmbeddingClustering_TransitiveMerge_SingleLinkage() {
    // Single-linkage: A is close to B, B is close to C, but A is not close to C.
    // All three should end up in the same cluster.
    var a = Unit(1, 0, 0);
    var b = Unit(0.85f, 0.53f, 0);    // cosine(a,b) ~0.85 > 0.6
    var c = Unit(0.55f, 0.83f, 0.1f); // cosine(b,c) ~0.9 > 0.6, cosine(a,c) ~0.55 < 0.6
    var faces = new[] { Face("a.jpg", a), Face("b.jpg", b), Face("c.jpg", c) };

    var index = FaceClusterIndex.BuildWithEmbeddingClustering(faces, similarityThreshold: 0.6f);

    // Single-linkage: a--b merge first, then b--c is close enough, so all three unite.
    Assert.Multiple(() => {
      Assert.That(index.Clusters, Has.Count.EqualTo(1));
      Assert.That(index.Clusters[0].Count, Is.EqualTo(3));
    });
  }

  [Test]
  public void EmbeddingClustering_HighThreshold_KeepsApart() {
    // With a very high threshold, even moderately similar faces stay separate.
    var a = Unit(1, 0, 0);
    var b = Unit(0.9f, 0.44f, 0);  // cosine ~0.9
    var faces = new[] { Face("a.jpg", a), Face("b.jpg", b) };

    var index = FaceClusterIndex.BuildWithEmbeddingClustering(faces, similarityThreshold: 0.95f);

    Assert.That(index.Clusters, Has.Count.EqualTo(2));
  }

  [Test]
  public void EmbeddingClustering_MismatchedDimensions_NeverMerge() {
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0)),
      Face("b.jpg", Unit(1, 0, 0, 0, 0))
    };

    var index = FaceClusterIndex.BuildWithEmbeddingClustering(faces);

    Assert.That(index.Clusters, Has.Count.EqualTo(2));
  }

  [Test]
  public void EmbeddingClustering_ThresholdOutOfRange_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      FaceClusterIndex.BuildWithEmbeddingClustering(Array.Empty<ScannedFace>(), similarityThreshold: 1.5f));
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      FaceClusterIndex.BuildWithEmbeddingClustering(Array.Empty<ScannedFace>(), similarityThreshold: -0.1f));
  }

  [Test]
  public void EmbeddingClustering_StoresThreshold() {
    var index = FaceClusterIndex.BuildWithEmbeddingClustering(
      Array.Empty<ScannedFace>(),
      similarityThreshold: 0.75f
    );
    Assert.That(index.SimilarityThreshold, Is.EqualTo(0.75f));
  }

  [Test]
  public void EmbeddingClustering_LargerGroupsSortFirst() {
    // When unnamed faces cluster, larger clusters should appear before singletons.
    var a1 = Unit(1, 0, 0);
    var a2 = Unit(0.99f, 0.1f, 0);
    var a3 = Unit(0.98f, 0.15f, 0);
    var b = Unit(0, 1, 0);  // singleton — far from a-group
    var faces = new[] {
      Face("a1.jpg", a1), Face("a2.jpg", a2), Face("a3.jpg", a3),
      Face("b.jpg", b)
    };

    var index = FaceClusterIndex.BuildWithEmbeddingClustering(faces, similarityThreshold: 0.6f);

    Assert.Multiple(() => {
      Assert.That(index.Clusters, Has.Count.EqualTo(2));
      Assert.That(index.Clusters[0].Count, Is.EqualTo(3)); // larger cluster first
      Assert.That(index.Clusters[1].Count, Is.EqualTo(1));
    });
  }

  // =====================================================================
  // ComputeCentroid tests
  // =====================================================================

  [Test]
  public void ComputeCentroid_SingleFace_ReturnsNormalizedEmbedding() {
    var emb = Unit(1, 0, 0);
    var cluster = new FaceCluster(1, new[] { Face("a.jpg", emb) });

    var centroid = FaceClusterIndex.ComputeCentroid(cluster);

    Assert.That(centroid, Is.Not.Null);
    // Single face: centroid == normalized embedding
    for (var i = 0; i < emb.Length; i++)
      Assert.That(centroid![i], Is.EqualTo(emb[i]).Within(1e-5f));
  }

  [Test]
  public void ComputeCentroid_MultipleFaces_ReturnsMeanNormalized() {
    var a = Unit(1, 0, 0);
    var b = Unit(0, 1, 0);
    var cluster = new FaceCluster(1, new[] { Face("a.jpg", a), Face("b.jpg", b) });

    var centroid = FaceClusterIndex.ComputeCentroid(cluster);

    Assert.That(centroid, Is.Not.Null);
    // Mean of [1,0,0] and [0,1,0] = [0.5, 0.5, 0], normalized = [1/sqrt(2), 1/sqrt(2), 0]
    var expected = Unit(0.5f, 0.5f, 0);
    for (var i = 0; i < expected.Length; i++)
      Assert.That(centroid![i], Is.EqualTo(expected[i]).Within(1e-5f));
  }

  [Test]
  public void ComputeCentroid_NoEmbeddings_ReturnsNull() {
    var cluster = new FaceCluster(1, new[] {
      new ScannedFace(
        new FileInfo("a.jpg"),
        new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Person)
      )
    });

    Assert.That(FaceClusterIndex.ComputeCentroid(cluster), Is.Null);
  }

  [Test]
  public void ComputeCentroid_MismatchedDimensions_ReturnsNull() {
    var cluster = new FaceCluster(1, new[] {
      Face("a.jpg", Unit(1, 0, 0)),
      Face("b.jpg", Unit(1, 0, 0, 0, 0))
    });

    Assert.That(FaceClusterIndex.ComputeCentroid(cluster), Is.Null);
  }

  // =====================================================================
  // FindNearestNamedClusters tests
  // =====================================================================

  [Test]
  public void FindNearestNamedClusters_ReturnsRankedList() {
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0), "Alice"),
      Face("b.jpg", Unit(0, 1, 0), "Bob"),
      Face("target.jpg", Unit(0.9f, 0.44f, 0))  // closer to Alice than Bob
    };

    var index = FaceClusterIndex.Build(faces);
    var targetCluster = index.Clusters.First(c => !c.IsNamed);

    var nearest = index.FindNearestNamedClusters(targetCluster, topN: 5);

    Assert.Multiple(() => {
      Assert.That(nearest, Has.Count.EqualTo(2));
      Assert.That(nearest[0].Cluster.Label, Is.EqualTo("Alice"));
      Assert.That(nearest[0].Similarity, Is.GreaterThan(nearest[1].Similarity));
    });
  }

  [Test]
  public void FindNearestNamedClusters_NoNamedClusters_ReturnsEmpty() {
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0)),
      Face("b.jpg", Unit(0, 1, 0))
    };

    var index = FaceClusterIndex.Build(faces);
    var target = index.Clusters[0];

    var nearest = index.FindNearestNamedClusters(target);

    Assert.That(nearest, Is.Empty);
  }

  [Test]
  public void FindNearestNamedClusters_TargetWithoutEmbedding_ReturnsEmpty() {
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0), "Alice"),
      new ScannedFace(
        new FileInfo("target.jpg"),
        new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Person)
      )
    };

    var index = FaceClusterIndex.Build(faces);
    var target = index.Clusters.First(c => !c.IsNamed);

    Assert.That(index.FindNearestNamedClusters(target), Is.Empty);
  }

  [Test]
  public void FindNearestNamedClusters_RespectsTopN() {
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0), "Alice"),
      Face("b.jpg", Unit(0, 1, 0), "Bob"),
      Face("c.jpg", Unit(0, 0, 1), "Charlie"),
      Face("target.jpg", Unit(0.5f, 0.5f, 0.5f))
    };

    var index = FaceClusterIndex.Build(faces);
    var target = index.Clusters.First(c => !c.IsNamed);

    var nearest = index.FindNearestNamedClusters(target, topN: 2);

    Assert.That(nearest, Has.Count.EqualTo(2));
  }
}
