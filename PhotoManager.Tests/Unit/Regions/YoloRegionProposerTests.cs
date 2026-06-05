using Hawkynt.PhotoManager.Core.Regions;

namespace Hawkynt.PhotoManager.Tests.Unit.Regions;

[TestFixture]
public class YoloRegionProposerTests {
  [Test]
  [TestCase("person", RegionCategory.Person)]
  [TestCase("cat", RegionCategory.Animal)]
  [TestCase("dog", RegionCategory.Animal)]
  [TestCase("bird", RegionCategory.Animal)]
  [TestCase("horse", RegionCategory.Animal)]
  [TestCase("spoon", RegionCategory.Item)]
  [TestCase("chair", RegionCategory.Item)]
  [TestCase("bicycle", RegionCategory.Item)]
  public void CategoryFor_KnownCocoLabel_MapsCorrectly(string cocoLabel, RegionCategory expected) {
    Assert.That(YoloRegionProposer.CategoryFor(cocoLabel), Is.EqualTo(expected));
  }

  [Test]
  public void CategoryFor_UnknownLabel_ReturnsOther() {
    Assert.That(YoloRegionProposer.CategoryFor("not-a-coco-class"), Is.EqualTo(RegionCategory.Other));
  }

  [Test]
  public void CategoryFor_CaseInsensitive() {
    Assert.Multiple(() => {
      Assert.That(YoloRegionProposer.CategoryFor("PERSON"), Is.EqualTo(RegionCategory.Person));
      Assert.That(YoloRegionProposer.CategoryFor("Cat"), Is.EqualTo(RegionCategory.Animal));
    });
  }
}
