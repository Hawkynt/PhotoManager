namespace Hawkynt.PhotoManager.Core.Detection;

/// <summary>
/// One detected entity in an image — either an object class (car, dog) or
/// a named person (from face clustering). Bounding box is optional and
/// normalized to 0..1 coordinates relative to the image so it survives
/// downstream resizes. Confidence is 0..1.
/// </summary>
public sealed record DetectionLabel(
  string Name,
  float Confidence,
  DetectionKind Kind,
  NormalizedBoundingBox? Region = null
);

public enum DetectionKind {
  Object,
  Face
}

/// <summary>
/// All four values are fractions of the image's width/height so the region
/// is resolution-independent. X/Y is the top-left corner.
/// </summary>
public readonly record struct NormalizedBoundingBox(float X, float Y, float Width, float Height);
