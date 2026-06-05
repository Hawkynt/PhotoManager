namespace Hawkynt.PhotoManager.Core.Stitching;

/// <summary>
/// One candidate alignment between two pieces. The transform takes a point
/// in piece B's local coordinate space (relative to its own bounding-box
/// origin) and maps it into piece A's local coordinate space:
/// <c>p_in_A = R · p_in_B + T</c>. <see cref="Score"/> is "lower is better"
/// — it's the mean residual (in pixels) over the matched correspondence
/// pairs, so a perfect alignment scores ~0.
/// </summary>
public sealed class EdgeMatch {
  public required int PieceA { get; init; }
  public required int PieceB { get; init; }

  /// <summary>2×2 rotation matrix row-major: [r00, r01, r10, r11].</summary>
  public required double[] Rotation { get; init; }
  public required double TranslationX { get; init; }
  public required double TranslationY { get; init; }

  /// <summary>Mean residual (px) over the correspondence pairs. Lower is better.</summary>
  public required double Score { get; init; }

  /// <summary>How many contour samples participated in the fit. More = stronger.</summary>
  public required int Support { get; init; }
}
