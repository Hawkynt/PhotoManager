using Hawkynt.PhotoManager.Core.Detection;
using Hawkynt.PhotoManager.Core.Regions;

namespace Hawkynt.PhotoManager.Tests.Unit.Regions;

[TestFixture]
public class TaggedRegionTests {
  [Test]
  public void IsNamed_ReturnsFalse_WhenLabelNullOrWhitespace() {
    var box = new NormalizedBoundingBox(0, 0, 0.1f, 0.1f);
    Assert.Multiple(() => {
      Assert.That(new TaggedRegion(box, RegionCategory.Person).IsNamed, Is.False);
      Assert.That(new TaggedRegion(box, RegionCategory.Person, Label: "").IsNamed, Is.False);
      Assert.That(new TaggedRegion(box, RegionCategory.Person, Label: "   ").IsNamed, Is.False);
    });
  }

  [Test]
  public void IsNamed_ReturnsTrue_WhenLabelHasContent() {
    var box = new NormalizedBoundingBox(0, 0, 0.1f, 0.1f);
    Assert.That(new TaggedRegion(box, RegionCategory.Person, Label: "Alice").IsNamed, Is.True);
  }

  [Test]
  public void Sources_AreStableConstants() {
    Assert.Multiple(() => {
      Assert.That(TaggedRegion.ManualSource, Is.EqualTo("manual"));
      Assert.That(TaggedRegion.YoloSource, Is.EqualTo("yolo"));
      Assert.That(TaggedRegion.FaceDetectorSource, Is.EqualTo("face-detector"));
    });
  }

  [Test]
  public void Default_StatusIsAccepted() {
    var r = new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Person);
    Assert.That(r.Status, Is.EqualTo(RegionStatus.Accepted));
  }
}
