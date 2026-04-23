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
  public void Build_ClosePair_ClustersTogether() {
    var a = Unit(1, 0, 0);
    var b = Unit(0.99f, 0.1f, 0);  // cosine ~0.995 — well above threshold
    var faces = new[] { Face("a.jpg", a), Face("b.jpg", b) };

    var index = FaceClusterIndex.Build(faces);

    Assert.Multiple(() => {
      Assert.That(index.Clusters, Has.Count.EqualTo(1));
      Assert.That(index.Clusters[0].Count, Is.EqualTo(2));
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
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0)),
      Face("b.jpg", Unit(0.99f, 0.1f, 0))
    };
    var index = FaceClusterIndex.Build(faces);
    var cluster = index.Clusters.Single();

    var renamed = FaceClusterIndex.ApplyName(cluster, "Alice");

    Assert.Multiple(() => {
      Assert.That(renamed, Has.Count.EqualTo(2));
      Assert.That(renamed, Is.All.Matches<ScannedFace>(f => f.Name == "Alice"));
      // original members unchanged — records are immutable
      Assert.That(cluster.Members, Is.All.Matches<ScannedFace>(f => f.Name is null));
    });
  }

  [Test]
  public void Build_MismatchedDimensions_DoNotCluster() {
    var faces = new[] {
      Face("a.jpg", Unit(1, 0, 0)),
      Face("b.jpg", Unit(1, 0, 0, 0, 0))  // 5-D vs 3-D
    };

    var index = FaceClusterIndex.Build(faces);

    Assert.That(index.Clusters, Has.Count.EqualTo(2));
  }
}
