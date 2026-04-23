using PhotoManager.Core.Detection;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Regions;

namespace PhotoManager.Core.Faces;

/// <summary>
/// Decodes UltraFace's raw output into detected face regions. Unlike YOLO,
/// UltraFace produces two parallel tensors — scores and box offsets — each
/// shaped <c>[1, N, *]</c> where N is the number of anchor candidates
/// (17640 for the 320×240 RFB variant; 4420 for the slim variant). Each
/// score entry is a 2-vector [background, face]; boxes are [x1, y1, x2, y2]
/// already in normalized (0..1) image coordinates.
/// Separated from the ONNX session so the post-processing math is testable
/// without the native runtime.
/// </summary>
internal static class UltraFacePostProcess {
  public static List<DetectedFace> Parse(
    float[] scores,
    float[] boxes,
    int numAnchors,
    float scoreThreshold,
    float iouThreshold
  ) {
    var candidates = new List<(float Score, float X1, float Y1, float X2, float Y2)>();

    for (var i = 0; i < numAnchors; i++) {
      // Scores: [bg, face] per anchor.
      var faceScore = scores[i * 2 + 1];
      if (faceScore < scoreThreshold)
        continue;

      var x1 = boxes[i * 4 + 0];
      var y1 = boxes[i * 4 + 1];
      var x2 = boxes[i * 4 + 2];
      var y2 = boxes[i * 4 + 3];

      // Degenerate boxes can happen near anchors clipped against the image
      // edge — drop them before NMS instead of passing junk downstream.
      if (x2 <= x1 || y2 <= y1)
        continue;

      candidates.Add((faceScore, x1, y1, x2, y2));
    }

    var kept = NonMaxSuppress(candidates, iouThreshold);

    return kept
      .Select(c => new DetectedFace(
        new FaceRegion(
          new NormalizedBoundingBox(
            Math.Clamp(c.X1, 0, 1),
            Math.Clamp(c.Y1, 0, 1),
            Math.Clamp(c.X2 - c.X1, 0, 1),
            Math.Clamp(c.Y2 - c.Y1, 0, 1)
          )
        ),
        Confidence: c.Score
      ))
      .ToList();
  }

  internal static List<(float Score, float X1, float Y1, float X2, float Y2)> NonMaxSuppress(
    List<(float Score, float X1, float Y1, float X2, float Y2)> candidates,
    float iouThreshold
  ) {
    var sorted = candidates.OrderByDescending(c => c.Score).ToList();
    var kept = new List<(float, float, float, float, float)>();

    while (sorted.Count > 0) {
      var head = sorted[0];
      kept.Add(head);
      sorted.RemoveAt(0);

      sorted.RemoveAll(other => Iou(head, other) > iouThreshold);
    }

    return kept;
  }

  internal static float Iou(
    (float Score, float X1, float Y1, float X2, float Y2) a,
    (float Score, float X1, float Y1, float X2, float Y2) b
  ) {
    var interLeft = Math.Max(a.X1, b.X1);
    var interTop = Math.Max(a.Y1, b.Y1);
    var interRight = Math.Min(a.X2, b.X2);
    var interBottom = Math.Min(a.Y2, b.Y2);

    if (interRight <= interLeft || interBottom <= interTop)
      return 0f;

    var inter = (interRight - interLeft) * (interBottom - interTop);
    var areaA = Math.Max(0, a.X2 - a.X1) * Math.Max(0, a.Y2 - a.Y1);
    var areaB = Math.Max(0, b.X2 - b.X1) * Math.Max(0, b.Y2 - b.Y1);
    var union = areaA + areaB - inter;
    return union <= 0 ? 0f : inter / union;
  }
}
