using Hawkynt.PhotoManager.Core.Faces;

namespace Hawkynt.PhotoManager.Tests.Unit.Faces;

[TestFixture]
public class UltraFacePostProcessTests {
  [Test]
  public void Parse_ScoreBelowThreshold_Filtered() {
    // 2 anchors. First anchor: faceScore 0.5 (below 0.7 threshold). Second: 0.9 (above).
    var scores = new[] {
      0.5f, 0.5f,    // anchor 0: [bg, face] — face 0.5 → filtered
      0.1f, 0.9f     // anchor 1: face 0.9 → kept
    };
    var boxes = new[] {
      0.10f, 0.10f, 0.20f, 0.20f,    // anchor 0
      0.30f, 0.30f, 0.50f, 0.55f     // anchor 1
    };

    var result = UltraFacePostProcess.Parse(scores, boxes, numAnchors: 2, scoreThreshold: 0.7f, iouThreshold: 0.3f);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(1));
      Assert.That(result[0].Confidence, Is.EqualTo(0.9f).Within(1e-4));
      Assert.That(result[0].Region.Box.X, Is.EqualTo(0.3f).Within(1e-4));
    });
  }

  [Test]
  public void Parse_DegenerateBox_Dropped() {
    // x2 <= x1 → zero area, drop before NMS.
    var scores = new[] { 0f, 0.95f };
    var boxes = new[] { 0.5f, 0.5f, 0.4f, 0.6f };

    var result = UltraFacePostProcess.Parse(scores, boxes, numAnchors: 1, scoreThreshold: 0.5f, iouThreshold: 0.3f);

    Assert.That(result, Is.Empty);
  }

  [Test]
  public void Parse_OverlappingDetections_NmsKeepsHighestScore() {
    // Two heavily overlapping anchors on the same face; NMS should keep one.
    var scores = new[] {
      0.01f, 0.95f,
      0.01f, 0.85f
    };
    var boxes = new[] {
      0.10f, 0.10f, 0.40f, 0.50f,
      0.12f, 0.11f, 0.41f, 0.51f
    };

    var result = UltraFacePostProcess.Parse(scores, boxes, numAnchors: 2, scoreThreshold: 0.5f, iouThreshold: 0.3f);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(1));
      Assert.That(result[0].Confidence, Is.EqualTo(0.95f).Within(1e-4));
    });
  }

  [Test]
  public void Parse_CoordinatesClampedToImage() {
    // Network can produce coords slightly outside [0,1] when faces hit the
    // edge — postprocess should clamp to the image rather than emit invalid
    // boxes downstream.
    var scores = new[] { 0f, 0.9f };
    var boxes = new[] { -0.1f, 0.3f, 1.2f, 0.8f };

    var result = UltraFacePostProcess.Parse(scores, boxes, numAnchors: 1, scoreThreshold: 0.5f, iouThreshold: 0.3f);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(1));
      var box = result[0].Region.Box;
      Assert.That(box.X, Is.GreaterThanOrEqualTo(0));
      Assert.That(box.X + box.Width, Is.LessThanOrEqualTo(1 + 1e-4));
    });
  }

  [Test]
  public void Iou_IdenticalBoxes_IsOne() {
    var box = (Score: 1f, X1: 0.1f, Y1: 0.1f, X2: 0.5f, Y2: 0.4f);
    Assert.That(UltraFacePostProcess.Iou(box, box), Is.EqualTo(1f).Within(1e-5));
  }
}
