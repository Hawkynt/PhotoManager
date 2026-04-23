using PhotoManager.Core.Detection;

namespace PhotoManager.Core.Regions;

/// <summary>
/// Adapter that converts detection results (object labels + bounding boxes)
/// into <see cref="TaggedRegion"/>s at <see cref="RegionStatus.Proposed"/>
/// status. The UI surfaces proposed regions with Accept/Discard buttons so
/// the user decides what ends up in the sidecar.
///
/// COCO classes are mapped to the closest broad category — persons go in
/// Person, animals go in Animal, everything else in Item. Unrecognized
/// labels fall through to <see cref="RegionCategory.Other"/>.
/// </summary>
public sealed class YoloRegionProposer {
  private readonly IDetector _detector;
  private readonly float _minConfidence;

  public YoloRegionProposer(IDetector detector, float minConfidence = 0.3f) {
    this._detector = detector ?? throw new ArgumentNullException(nameof(detector));
    this._minConfidence = minConfidence;
  }

  public async Task<IReadOnlyList<TaggedRegion>> ProposeAsync(FileInfo imageFile, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(imageFile);

    var result = await this._detector.DetectAsync(imageFile, cancellationToken);

    return result.Labels
      .Where(l => l.Confidence >= this._minConfidence && l.Region is not null)
      .Select(l => new TaggedRegion(
        l.Region!.Value,
        CategoryFor(l.Name),
        Label: l.Name,
        Status: RegionStatus.Proposed,
        Source: TaggedRegion.YoloSource
      ))
      .ToArray();
  }

  /// <summary>
  /// Maps a COCO class label to the broad <see cref="RegionCategory"/> the
  /// app exposes. Persons and animals get their own bucket; everything else
  /// (furniture, food, vehicles, utensils) falls into Item.
  /// </summary>
  internal static RegionCategory CategoryFor(string label) {
    if (string.Equals(label, "person", StringComparison.OrdinalIgnoreCase))
      return RegionCategory.Person;

    if (AnimalLabels.Contains(label))
      return RegionCategory.Animal;

    if (!CocoKnown.Contains(label))
      return RegionCategory.Other;

    return RegionCategory.Item;
  }

  private static readonly HashSet<string> AnimalLabels = new(StringComparer.OrdinalIgnoreCase) {
    "bird", "cat", "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe"
  };

  // The full COCO-80 set; anything not in here is treated as user-defined.
  private static readonly HashSet<string> CocoKnown = new(StringComparer.OrdinalIgnoreCase) {
    "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
    "traffic light", "fire hydrant", "stop sign", "parking meter", "bench",
    "bird", "cat", "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe",
    "backpack", "umbrella", "handbag", "tie", "suitcase",
    "frisbee", "skis", "snowboard", "sports ball", "kite", "baseball bat",
    "baseball glove", "skateboard", "surfboard", "tennis racket",
    "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl",
    "banana", "apple", "sandwich", "orange", "broccoli", "carrot",
    "hot dog", "pizza", "donut", "cake",
    "chair", "couch", "potted plant", "bed", "dining table", "toilet",
    "tv", "laptop", "mouse", "remote", "keyboard", "cell phone",
    "microwave", "oven", "toaster", "sink", "refrigerator",
    "book", "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush"
  };
}
