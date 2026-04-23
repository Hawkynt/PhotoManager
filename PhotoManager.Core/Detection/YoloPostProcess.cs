namespace PhotoManager.Core.Detection;

/// <summary>
/// Pure math for turning a YOLO v8 detection tensor into a list of bounding
/// boxes in the original image's coordinate space. Separated from the ONNX
/// session so it can be tested without a real model or native runtime.
///
/// YOLO v8 output layout (for 80-class object detection): <c>[1, 84, 8400]</c>
/// where 84 = 4 box params (cx, cy, w, h) + 80 class scores, and 8400 is the
/// number of candidate anchor cells. We transpose virtually (indexing by
/// channel × cell), pick the best class per cell, filter by confidence,
/// then reduce overlaps with non-max suppression.
/// </summary>
internal static class YoloPostProcess {
  /// <summary>
  /// Parses the raw output tensor and returns the retained detections after
  /// confidence-filtering and non-max suppression. Coordinates are in the
  /// <paramref name="letterbox"/> canvas and are converted to the original
  /// image's pixel coordinates before return.
  /// </summary>
  public static List<YoloDetection> Parse(
    float[] tensor,
    int numChannels,
    int numCells,
    LetterboxInfo letterbox,
    float scoreThreshold,
    float iouThreshold
  ) {
    var classCount = numChannels - 4;
    var candidates = new List<YoloDetection>();

    for (var cell = 0; cell < numCells; cell++) {
      var (bestClass, bestScore) = BestClass(tensor, numCells, cell, classCount);
      if (bestScore < scoreThreshold)
        continue;

      var cx = tensor[0 * numCells + cell];
      var cy = tensor[1 * numCells + cell];
      var w = tensor[2 * numCells + cell];
      var h = tensor[3 * numCells + cell];

      var pixel = UnmapLetterbox(cx, cy, w, h, letterbox);
      candidates.Add(new YoloDetection(bestClass, bestScore, pixel));
    }

    return NonMaxSuppress(candidates, iouThreshold);
  }

  private static (int BestClass, float BestScore) BestClass(float[] tensor, int numCells, int cell, int classCount) {
    var best = -1;
    var bestScore = float.NegativeInfinity;
    for (var c = 0; c < classCount; c++) {
      var score = tensor[(4 + c) * numCells + cell];
      if (score > bestScore) {
        bestScore = score;
        best = c;
      }
    }
    return (best, bestScore);
  }

  /// <summary>
  /// Un-does the letterbox transform applied during preprocessing and clamps
  /// the result to the original image's pixel bounds.
  /// </summary>
  internal static PixelBox UnmapLetterbox(float cx, float cy, float w, float h, LetterboxInfo lb) {
    // YOLO outputs cx,cy,w,h in the 640×640 letterbox canvas.
    // Reverse: subtract pad, divide by scale → image-coordinate pixels.
    var ix = (cx - lb.PadX) / lb.Scale;
    var iy = (cy - lb.PadY) / lb.Scale;
    var iw = w / lb.Scale;
    var ih = h / lb.Scale;

    var left = Math.Clamp(ix - iw / 2, 0, lb.OriginalWidth);
    var top = Math.Clamp(iy - ih / 2, 0, lb.OriginalHeight);
    var right = Math.Clamp(ix + iw / 2, 0, lb.OriginalWidth);
    var bottom = Math.Clamp(iy + ih / 2, 0, lb.OriginalHeight);

    return new PixelBox(left, top, right - left, bottom - top);
  }

  /// <summary>
  /// Greedy per-class non-max suppression: sort by score, keep each detection
  /// that doesn't overlap a stronger one of the same class above IoU threshold.
  /// </summary>
  internal static List<YoloDetection> NonMaxSuppress(List<YoloDetection> candidates, float iouThreshold) {
    var sorted = candidates
      .OrderByDescending(d => d.Score)
      .ToList();

    var kept = new List<YoloDetection>();

    while (sorted.Count > 0) {
      var head = sorted[0];
      kept.Add(head);
      sorted.RemoveAt(0);

      sorted.RemoveAll(other =>
        other.ClassId == head.ClassId &&
        Iou(head.Box, other.Box) > iouThreshold);
    }

    return kept;
  }

  internal static float Iou(PixelBox a, PixelBox b) {
    var ax2 = a.X + a.Width;
    var ay2 = a.Y + a.Height;
    var bx2 = b.X + b.Width;
    var by2 = b.Y + b.Height;

    var interLeft = Math.Max(a.X, b.X);
    var interTop = Math.Max(a.Y, b.Y);
    var interRight = Math.Min(ax2, bx2);
    var interBottom = Math.Min(ay2, by2);

    if (interRight <= interLeft || interBottom <= interTop)
      return 0f;

    var interArea = (interRight - interLeft) * (interBottom - interTop);
    var unionArea = a.Width * a.Height + b.Width * b.Height - interArea;
    return unionArea <= 0 ? 0f : interArea / unionArea;
  }
}

/// <summary>
/// Metadata about the letterbox preprocessing step: how a source image of
/// <see cref="OriginalWidth"/>×<see cref="OriginalHeight"/> was scaled by
/// <see cref="Scale"/> and padded by (<see cref="PadX"/>, <see cref="PadY"/>)
/// to fit a square model input.
/// </summary>
internal readonly record struct LetterboxInfo(
  int OriginalWidth,
  int OriginalHeight,
  float Scale,
  float PadX,
  float PadY
);

internal readonly record struct PixelBox(float X, float Y, float Width, float Height);

internal readonly record struct YoloDetection(int ClassId, float Score, PixelBox Box);
