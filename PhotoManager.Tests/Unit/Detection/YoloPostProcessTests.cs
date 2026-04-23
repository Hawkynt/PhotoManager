using PhotoManager.Core.Detection;

namespace PhotoManager.Tests.Unit.Detection;

[TestFixture]
public class YoloPostProcessTests {
  [Test]
  public void Iou_IdenticalBoxes_EqualsOne() {
    var box = new PixelBox(10, 20, 40, 30);
    Assert.That(YoloPostProcess.Iou(box, box), Is.EqualTo(1f).Within(1e-6));
  }

  [Test]
  public void Iou_Disjoint_EqualsZero() {
    var a = new PixelBox(0, 0, 10, 10);
    var b = new PixelBox(100, 100, 10, 10);
    Assert.That(YoloPostProcess.Iou(a, b), Is.Zero);
  }

  [Test]
  public void Iou_HalfOverlap_MatchesExpected() {
    var a = new PixelBox(0, 0, 10, 10);
    var b = new PixelBox(5, 0, 10, 10);

    // Intersection = 5*10 = 50; union = 100+100-50 = 150; IoU = 50/150 = 1/3
    Assert.That(YoloPostProcess.Iou(a, b), Is.EqualTo(1f / 3f).Within(1e-4));
  }

  [Test]
  public void NonMaxSuppress_KeepsHighestScoringOverlap() {
    var candidates = new List<YoloDetection> {
      new(ClassId: 0, Score: 0.9f, Box: new PixelBox(0, 0, 100, 100)),
      new(ClassId: 0, Score: 0.8f, Box: new PixelBox(5, 5, 100, 100)),  // heavy overlap, lower score
      new(ClassId: 0, Score: 0.7f, Box: new PixelBox(500, 500, 50, 50)), // disjoint
    };

    var kept = YoloPostProcess.NonMaxSuppress(candidates, iouThreshold: 0.45f);

    Assert.Multiple(() => {
      Assert.That(kept.Count, Is.EqualTo(2));
      Assert.That(kept[0].Score, Is.EqualTo(0.9f));
      Assert.That(kept[1].Score, Is.EqualTo(0.7f));
    });
  }

  [Test]
  public void NonMaxSuppress_DifferentClasses_NotSuppressed() {
    // Two boxes overlap heavily but have different class IDs — both should survive.
    var candidates = new List<YoloDetection> {
      new(ClassId: 0, Score: 0.9f, Box: new PixelBox(0, 0, 100, 100)),
      new(ClassId: 1, Score: 0.8f, Box: new PixelBox(5, 5, 100, 100)),
    };

    var kept = YoloPostProcess.NonMaxSuppress(candidates, iouThreshold: 0.45f);

    Assert.That(kept.Count, Is.EqualTo(2), "NMS should be per-class");
  }

  [Test]
  public void UnmapLetterbox_NoPadNoScale_ReturnsSourceCoordinates() {
    var lb = new LetterboxInfo(OriginalWidth: 640, OriginalHeight: 640, Scale: 1f, PadX: 0, PadY: 0);
    var box = YoloPostProcess.UnmapLetterbox(cx: 320, cy: 320, w: 100, h: 50, lb);

    Assert.Multiple(() => {
      Assert.That(box.X, Is.EqualTo(270).Within(1e-4));
      Assert.That(box.Y, Is.EqualTo(295).Within(1e-4));
      Assert.That(box.Width, Is.EqualTo(100).Within(1e-4));
      Assert.That(box.Height, Is.EqualTo(50).Within(1e-4));
    });
  }

  [Test]
  public void UnmapLetterbox_WithScaleAndPad_ReversesCorrectly() {
    // Original 1280×720 → scale 0.5 → fits into 640×360 → pad top/bottom 140.
    var lb = new LetterboxInfo(OriginalWidth: 1280, OriginalHeight: 720, Scale: 0.5f, PadX: 0, PadY: 140);

    // A box at (320, 320) in letterbox space with size (100, 50) should map back
    // to (640, 360) center in the original with size (200, 100).
    var box = YoloPostProcess.UnmapLetterbox(cx: 320, cy: 320, w: 100, h: 50, lb);
    var cxOriginal = box.X + box.Width / 2;
    var cyOriginal = box.Y + box.Height / 2;

    Assert.Multiple(() => {
      Assert.That(cxOriginal, Is.EqualTo(640).Within(1e-3));
      Assert.That(cyOriginal, Is.EqualTo(360).Within(1e-3));
      Assert.That(box.Width, Is.EqualTo(200).Within(1e-3));
      Assert.That(box.Height, Is.EqualTo(100).Within(1e-3));
    });
  }

  [Test]
  public void UnmapLetterbox_ClampsToOriginalImageBounds() {
    var lb = new LetterboxInfo(OriginalWidth: 100, OriginalHeight: 100, Scale: 1f, PadX: 0, PadY: 0);

    // Box straddling the top-left corner — the negative part should be clamped to 0.
    var box = YoloPostProcess.UnmapLetterbox(cx: 10, cy: 10, w: 40, h: 40, lb);

    Assert.Multiple(() => {
      Assert.That(box.X, Is.GreaterThanOrEqualTo(0));
      Assert.That(box.Y, Is.GreaterThanOrEqualTo(0));
      Assert.That(box.X + box.Width, Is.LessThanOrEqualTo(100 + 1e-3));
      Assert.That(box.Y + box.Height, Is.LessThanOrEqualTo(100 + 1e-3));
    });
  }

  [Test]
  public void Parse_BelowConfidenceThreshold_Filtered() {
    // 5 channels (4 bbox + 1 class), 2 cells. First cell score 0.1 (below
    // threshold), second cell score 0.9 (above).
    var numChannels = 5;
    var numCells = 2;
    var tensor = new float[numChannels * numCells];

    // cell 0: low confidence
    tensor[0 * numCells + 0] = 320; tensor[1 * numCells + 0] = 320;
    tensor[2 * numCells + 0] = 10;  tensor[3 * numCells + 0] = 10;
    tensor[4 * numCells + 0] = 0.1f;

    // cell 1: high confidence
    tensor[0 * numCells + 1] = 100; tensor[1 * numCells + 1] = 200;
    tensor[2 * numCells + 1] = 50;  tensor[3 * numCells + 1] = 60;
    tensor[4 * numCells + 1] = 0.9f;

    var lb = new LetterboxInfo(640, 640, 1f, 0, 0);
    var result = YoloPostProcess.Parse(tensor, numChannels, numCells, lb, scoreThreshold: 0.25f, iouThreshold: 0.45f);

    Assert.Multiple(() => {
      Assert.That(result.Count, Is.EqualTo(1));
      Assert.That(result[0].Score, Is.EqualTo(0.9f).Within(1e-4));
      Assert.That(result[0].ClassId, Is.Zero);
    });
  }
}
