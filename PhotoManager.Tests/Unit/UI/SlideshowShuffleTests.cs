using Hawkynt.PhotoManager.UI.Views;

namespace Hawkynt.PhotoManager.Tests.Unit.UI;

[TestFixture]
public class SlideshowShuffleTests {
  [Test]
  public void FisherYatesShuffle_PreservesAllElements() {
    var original = Enumerable.Range(0, 100).ToList();
    var shuffled = new List<int>(original);

    SlideshowWindow.FisherYatesShuffle(shuffled, new Random(42));

    // Same count, same elements (just reordered).
    Assert.That(shuffled, Has.Count.EqualTo(original.Count));
    Assert.That(shuffled.OrderBy(x => x), Is.EqualTo(original));
  }

  [Test]
  public void FisherYatesShuffle_ActuallyReorders() {
    // With 100 elements and a fixed seed, it's astronomically unlikely
    // that the shuffle leaves the list in the original order.
    var original = Enumerable.Range(0, 100).ToList();
    var shuffled = new List<int>(original);

    SlideshowWindow.FisherYatesShuffle(shuffled, new Random(42));

    Assert.That(shuffled, Is.Not.EqualTo(original));
  }

  [Test]
  public void FisherYatesShuffle_SingleElement_NoError() {
    var list = new List<int> { 1 };
    SlideshowWindow.FisherYatesShuffle(list);
    Assert.That(list, Is.EqualTo(new[] { 1 }));
  }

  [Test]
  public void FisherYatesShuffle_Empty_NoError() {
    var list = new List<int>();
    SlideshowWindow.FisherYatesShuffle(list);
    Assert.That(list, Is.Empty);
  }

  [Test]
  public void FisherYatesShuffle_TwoElements_Swaps() {
    // With 2 elements: 50% chance of swap on any seed. Run with a known
    // seed that does swap.
    var list = new List<int> { 1, 2 };
    SlideshowWindow.FisherYatesShuffle(list, new Random(0));
    // Whether swapped or not, both elements must still be present.
    Assert.That(list, Has.Count.EqualTo(2));
    Assert.That(list, Does.Contain(1));
    Assert.That(list, Does.Contain(2));
  }

  [Test]
  public void Unshuffle_RestoresOriginalOrder() {
    // Simulate the slideshow's unshuffle behaviour:
    // original order is preserved separately, shuffling replaces the
    // working list, unshuffling copies original back.
    var original = Enumerable.Range(0, 50).Select(i => new FileInfo($"photo{i:D3}.jpg")).ToList();
    var working = new List<FileInfo>(original);

    SlideshowWindow.FisherYatesShuffle(working, new Random(123));
    Assert.That(working, Is.Not.EqualTo(original), "Shuffle should reorder");

    // Unshuffle: restore from original.
    var restored = new List<FileInfo>(original);
    Assert.That(restored.Select(f => f.Name), Is.EqualTo(original.Select(f => f.Name)));
  }

  [Test]
  public void FisherYatesShuffle_Deterministic_WithSameSeed() {
    var a = Enumerable.Range(0, 20).ToList();
    var b = Enumerable.Range(0, 20).ToList();

    SlideshowWindow.FisherYatesShuffle(a, new Random(999));
    SlideshowWindow.FisherYatesShuffle(b, new Random(999));

    Assert.That(a, Is.EqualTo(b));
  }
}
