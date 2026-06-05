using Hawkynt.PhotoManager.Core.Detection;

namespace Hawkynt.PhotoManager.Core.Metadata;

/// <summary>
/// A face detected or tagged in an image, stored in the sidecar via the MWG-RS
/// (Metadata Working Group Regions) schema — the same schema Lightroom and
/// digiKam use. <see cref="Box"/> is normalized to the image (0..1 center-based
/// per MWG spec). <see cref="PersonName"/> is null when the face is not yet
/// attributed to a person.
/// </summary>
public sealed record FaceRegion(
  NormalizedBoundingBox Box,
  string? PersonName = null
);
