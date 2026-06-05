using Hawkynt.PhotoManager.Core.Faces;

namespace Hawkynt.PhotoManager.Tests.Unit.Faces;

[TestFixture]
public class PeopleRegistryTests {
  private FileInfo _tempFile = null!;

  [SetUp]
  public void Setup() {
    this._tempFile = new FileInfo(Path.Combine(Path.GetTempPath(), "pm-people-" + Guid.NewGuid().ToString("N") + ".json"));
  }

  [TearDown]
  public void Teardown() {
    if (this._tempFile.Exists)
      this._tempFile.Delete();
  }

  [Test]
  public void AddReference_NewName_ShowsUpInKnownNames() {
    var registry = new PeopleRegistry(this._tempFile);
    registry.AddReference("Alice", new[] { 1f, 0f, 0f });

    Assert.Multiple(() => {
      Assert.That(registry.KnownNames, Does.Contain("Alice"));
      Assert.That(registry.EmbeddingCount("Alice"), Is.EqualTo(1));
    });
  }

  [Test]
  public void AddReference_SameName_AppendsNotReplaces() {
    var registry = new PeopleRegistry(this._tempFile);
    registry.AddReference("Alice", new[] { 1f, 0f, 0f });
    registry.AddReference("Alice", new[] { 0f, 1f, 0f });

    Assert.That(registry.EmbeddingCount("Alice"), Is.EqualTo(2));
  }

  [Test]
  public void FindMatch_ExactEmbedding_ReturnsName() {
    var registry = new PeopleRegistry(this._tempFile);
    var reference = new[] { 1f, 0f, 0f };
    registry.AddReference("Alice", reference);

    Assert.That(registry.FindMatch(reference), Is.EqualTo("Alice"));
  }

  [Test]
  public void FindMatch_ClosestAmongMultiple_Wins() {
    var registry = new PeopleRegistry(this._tempFile);
    registry.AddReference("Alice", new[] { 1f, 0f, 0f });
    registry.AddReference("Bob", new[] { 0f, 1f, 0f });

    // Vector close to Alice's reference
    var probe = new[] { 0.9f, 0.1f, 0f };

    Assert.That(registry.FindMatch(probe, minSimilarity: 0.5f), Is.EqualTo("Alice"));
  }

  [Test]
  public void FindMatch_NoCloseCandidate_ReturnsNull() {
    var registry = new PeopleRegistry(this._tempFile);
    registry.AddReference("Alice", new[] { 1f, 0f, 0f });

    var probe = new[] { 0f, 0f, 1f };
    Assert.That(registry.FindMatch(probe, minSimilarity: 0.8f), Is.Null);
  }

  [Test]
  public void Persistence_SurvivesNewRegistryInstance() {
    var first = new PeopleRegistry(this._tempFile);
    first.AddReference("Alice", new[] { 1f, 0f, 0f });

    var second = new PeopleRegistry(this._tempFile);

    Assert.Multiple(() => {
      Assert.That(second.KnownNames, Does.Contain("Alice"));
      Assert.That(second.FindMatch(new[] { 1f, 0f, 0f }), Is.EqualTo("Alice"));
    });
  }

  [Test]
  public void CosineSimilarity_OrthogonalVectors_Zero() {
    var a = new[] { 1f, 0f };
    var b = new[] { 0f, 1f };
    Assert.That(PeopleRegistry.CosineSimilarity(a, b), Is.EqualTo(0f).Within(1e-6));
  }

  [Test]
  public void CosineSimilarity_IdenticalVectors_One() {
    var a = new[] { 1f, 2f, 3f };
    Assert.That(PeopleRegistry.CosineSimilarity(a, a), Is.EqualTo(1f).Within(1e-6));
  }

  [Test]
  public void CosineSimilarity_OppositeVectors_NegativeOne() {
    var a = new[] { 1f, 2f, 3f };
    var b = new[] { -1f, -2f, -3f };
    Assert.That(PeopleRegistry.CosineSimilarity(a, b), Is.EqualTo(-1f).Within(1e-6));
  }

  [Test]
  public void Remove_ExistingName_GoneAfterSave() {
    var registry = new PeopleRegistry(this._tempFile);
    registry.AddReference("Alice", new[] { 1f, 0f });
    registry.Remove("Alice");

    var reloaded = new PeopleRegistry(this._tempFile);
    Assert.That(reloaded.KnownNames, Does.Not.Contain("Alice"));
  }
}
