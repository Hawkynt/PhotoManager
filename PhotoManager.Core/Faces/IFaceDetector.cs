using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Faces;

/// <summary>
/// Detects faces in an image. Implementations may return naked regions
/// (no embedding) or regions with <paramref name="Embedding"/> populated,
/// in which case callers can match them against <see cref="PeopleRegistry"/>
/// to auto-assign names.
/// </summary>
public interface IFaceDetector {
  Task<IReadOnlyList<DetectedFace>> DetectAsync(FileInfo imageFile, CancellationToken cancellationToken = default);
}

/// <summary>
/// A face as returned from a detector. <see cref="Region"/> carries the
/// bounding box (and optionally a pre-assigned name, e.g. from a prior
/// tag). <see cref="Embedding"/> is the face descriptor — null if the
/// detector doesn't produce one.
/// </summary>
public sealed record DetectedFace(
  FaceRegion Region,
  float Confidence,
  float[]? Embedding = null
);
