using PhotoManager.Core.Regions;

namespace PhotoManager.Tests.Unit.Regions;

[TestFixture]
public class RegionCategoryExtensionsTests {
  [TestCase(RegionCategory.Person, "#2E86DE")]
  [TestCase(RegionCategory.Animal, "#10AC84")]
  [TestCase(RegionCategory.Item,   "#EE5A24")]
  [TestCase(RegionCategory.Place,  "#8854D0")]
  [TestCase(RegionCategory.Other,  "#808080")]
  public void ToHexColor_ReturnsExpectedSwatch(RegionCategory category, string expected) {
    Assert.That(category.ToHexColor(), Is.EqualTo(expected));
  }

  [Test]
  public void ToHexColor_UnknownEnumValue_FallsBackToGray() {
    // Cast a deliberately-out-of-range value to exercise the default switch arm.
    var bogus = (RegionCategory)999;
    Assert.That(bogus.ToHexColor(), Is.EqualTo("#808080"));
  }
}
