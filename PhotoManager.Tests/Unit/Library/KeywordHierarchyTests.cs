using System.Text.Json;
using Hawkynt.PhotoManager.Core.Library;

namespace Hawkynt.PhotoManager.Tests.Unit.Library;

[TestFixture]
public class KeywordHierarchyTests {

  private static IReadOnlyList<KeywordNode> SampleTree() => new List<KeywordNode> {
    new("Travel", new[] {
      new KeywordNode("Italy", new[] {
        new KeywordNode("Rome"),
        new KeywordNode("Florence")
      }),
      new KeywordNode("France", new[] {
        new KeywordNode("Paris")
      })
    }),
    new("Family", new[] {
      new KeywordNode("Kids")
    })
  };

  [Test]
  public void Expand_EmptyTree_ReturnsInputUnchanged() {
    var result = KeywordHierarchy.Expand(Array.Empty<KeywordNode>(), new[] { "Rome", "Holiday" });

    Assert.That(result, Is.EquivalentTo(new[] { "Rome", "Holiday" }));
  }

  [Test]
  public void Expand_LeafSelection_IncludesAncestors() {
    var result = KeywordHierarchy.Expand(SampleTree(), new[] { "Rome" });

    Assert.That(result, Is.EquivalentTo(new[] { "Travel", "Italy", "Rome" }));
  }

  [Test]
  public void Expand_MultipleSelections_NoDuplicateAncestors() {
    var result = KeywordHierarchy.Expand(SampleTree(), new[] { "Rome", "Florence" });

    // Travel + Italy are shared ancestors and must appear exactly once.
    Assert.That(result.Count, Is.EqualTo(4));
    Assert.That(result, Is.EquivalentTo(new[] { "Travel", "Italy", "Rome", "Florence" }));
  }

  [Test]
  public void Expand_SelectionsAcrossBranches_KeepsAllAncestors() {
    var result = KeywordHierarchy.Expand(SampleTree(), new[] { "Paris", "Rome" });

    Assert.That(result, Is.EquivalentTo(new[] { "Travel", "France", "Paris", "Italy", "Rome" }));
  }

  [Test]
  public void Expand_AdHocKeyword_PassesThroughUnchanged() {
    var result = KeywordHierarchy.Expand(SampleTree(), new[] { "Rome", "Sunset" });

    Assert.That(result, Does.Contain("Sunset"));
    Assert.That(result, Does.Contain("Travel"));
  }

  [Test]
  public void Expand_CaseInsensitiveMatch_DoesNotDuplicate() {
    var result = KeywordHierarchy.Expand(SampleTree(), new[] { "rome", "ROME" });

    Assert.That(result.Count(s => string.Equals(s, "Rome", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1));
  }

  [Test]
  public void Expand_NullOrWhitespaceEntries_AreSkipped() {
    var result = KeywordHierarchy.Expand(SampleTree(), new[] { "Rome", "", "  ", null! });

    Assert.That(result, Is.EquivalentTo(new[] { "Travel", "Italy", "Rome" }));
  }

  [Test]
  public void Find_ReturnsAllMatchingNodes() {
    var hits = KeywordHierarchy.Find(SampleTree(), "Italy").ToList();

    Assert.That(hits.Count, Is.EqualTo(1));
    Assert.That(hits[0].Children.Select(c => c.Name), Is.EquivalentTo(new[] { "Rome", "Florence" }));
  }

  [Test]
  public void Find_NoMatch_ReturnsEmpty() {
    var hits = KeywordHierarchy.Find(SampleTree(), "Atlantis").ToList();

    Assert.That(hits, Is.Empty);
  }

  [Test]
  public void FindPaths_ReturnsAncestorChain() {
    var paths = KeywordHierarchy.FindPaths(SampleTree(), "Rome");

    Assert.That(paths.Count, Is.EqualTo(1));
    Assert.That(paths[0], Is.EqualTo(new[] { "Travel", "Italy", "Rome" }));
  }

  [Test]
  public void JsonRoundTrip_DeepNesting_PreservesShape() {
    var original = SampleTree();
    var options = new JsonSerializerOptions { WriteIndented = true };

    var json = JsonSerializer.Serialize(original, options);
    var roundTripped = JsonSerializer.Deserialize<List<KeywordNode>>(json);

    Assert.That(roundTripped, Is.Not.Null);
    Assert.That(roundTripped!.Count, Is.EqualTo(2));
    Assert.That(roundTripped[0].Name, Is.EqualTo("Travel"));
    Assert.That(roundTripped[0].Children.Count, Is.EqualTo(2));
    Assert.That(roundTripped[0].Children[0].Name, Is.EqualTo("Italy"));
    Assert.That(roundTripped[0].Children[0].Children.Select(c => c.Name),
      Is.EquivalentTo(new[] { "Rome", "Florence" }));

    var expanded = KeywordHierarchy.Expand(roundTripped, new[] { "Rome" });
    Assert.That(expanded, Is.EquivalentTo(new[] { "Travel", "Italy", "Rome" }));
  }
}
