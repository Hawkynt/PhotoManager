using SixLabors.ImageSharp;

namespace Hawkynt.PhotoManager.Core.Stitching;

/// <summary>
/// Resampled, evenly-spaced version of a piece's <see cref="DetectedPiece.Contour"/>.
/// Contour points walked clockwise become a closed cycle; resampling to a fixed
/// N points lets the matcher correlate arc segments by index without dealing
/// with variable-stride sampling artefacts. <see cref="ArcLength"/> records
/// the original perimeter length so the matcher can derate matches whose
/// physical length disagrees by more than a few percent.
/// </summary>
public sealed class EdgeProfile {
  public required int PieceIndex { get; init; }
  public required IReadOnlyList<PointF> Points { get; init; }
  public required double ArcLength { get; init; }
}
