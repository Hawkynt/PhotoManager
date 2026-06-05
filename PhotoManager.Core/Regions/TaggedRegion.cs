using Hawkynt.PhotoManager.Core.Detection;

namespace Hawkynt.PhotoManager.Core.Regions;

/// <summary>
/// A labeled rectangular area in an image — generalized from the
/// face-only model. Handles faces, objects, animals, places, or
/// anything else the user wants to tag.
///
/// <see cref="Box"/> is normalized (0..1) so the region is resolution-
/// independent. <see cref="Label"/> is free-form — a person's name
/// for Person regions, an animal type (cat/dog) for Animal, etc.
/// <see cref="Source"/> identifies who proposed the region so UIs can
/// show "this was suggested by the object detector" vs "you drew this".
/// <see cref="Embedding"/> is only populated for Person regions that have
/// been run through a face embedder — it's what the clustering view uses
/// to group unknown faces.
/// </summary>
public sealed record TaggedRegion(
  NormalizedBoundingBox Box,
  RegionCategory Category,
  string? Label = null,
  RegionStatus Status = RegionStatus.Accepted,
  string? Source = null,
  float[]? Embedding = null
) {
  public const string ManualSource = "manual";
  public const string YoloSource = "yolo";
  public const string FaceDetectorSource = "face-detector";

  public bool IsNamed => !string.IsNullOrWhiteSpace(this.Label);
}
