using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Stitching;

/// <summary>
/// One torn / cut piece pulled out of the source scan by the segmenter.
/// The cropped <see cref="Image"/> is RGBA with the surrounding background
/// punched to transparent so downstream rendering can rotate / blend it
/// without bleeding the scanner bed colour. <see cref="Contour"/> is the
/// closed boundary polyline traced clockwise from the top-most leftmost
/// pixel; coordinates are local to the cropped image (i.e. relative to
/// <see cref="BoundingBox"/>'s origin).
/// </summary>
public sealed class DetectedPiece {
  /// <summary>Index in the segmenter's output list, used for diagnostics.</summary>
  public required int Index { get; init; }

  /// <summary>Bounding box in the original source-image coordinate space.</summary>
  public required Rectangle BoundingBox { get; init; }

  /// <summary>Cropped piece with transparent background. Owned by the piece — the orchestrator disposes it.</summary>
  public required Image<Rgba32> Image { get; init; }

  /// <summary>Closed boundary polyline in the cropped image's coordinate space, walked clockwise.</summary>
  public required IReadOnlyList<PointF> Contour { get; init; }

  /// <summary>Number of foreground pixels inside the piece — handy for "biggest piece" anchoring.</summary>
  public required int Area { get; init; }
}
