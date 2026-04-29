using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Segmentation;

/// <summary>
/// Converts an alpha-matte segmentation result into a regular grid of
/// <see cref="BrushDab"/>s, ready to drop into a <see cref="LocalMask"/>.
///
/// Why a grid instead of a contour or one big dab: <see cref="ImageDeveloper"/>
/// evaluates brush dabs with a Wendland-quadratic falloff and clamps the
/// running weight 0..1. A regular grid of small overlapping dabs at modest
/// Flow sums into a smooth, tunable mask; a few large dabs at full Flow
/// would hard-clip and look blocky.
/// </summary>
public static class BrushDabsFromAlphaMask {
  /// <summary>
  /// Build a list of brush dabs covering the bright regions of the alpha
  /// matte. Each cell of a <paramref name="gridSize"/>×<paramref name="gridSize"/>
  /// grid contributes one dab whose Flow is the cell's mean alpha (after a
  /// <paramref name="threshold"/> cull to drop near-empty cells).
  /// </summary>
  public static IReadOnlyList<BrushDab> Build(Image<L8> alpha, int gridSize = 64, byte threshold = 8) {
    ArgumentNullException.ThrowIfNull(alpha);
    if (gridSize <= 0) throw new ArgumentOutOfRangeException(nameof(gridSize));

    var width = alpha.Width;
    var height = alpha.Height;
    if (width == 0 || height == 0)
      return Array.Empty<BrushDab>();

    var sums = new long[gridSize * gridSize];
    var counts = new long[gridSize * gridSize];

    alpha.ProcessPixelRows(accessor => {
      for (var y = 0; y < height; y++) {
        var row = accessor.GetRowSpan(y);
        var gy = Math.Min(gridSize - 1, y * gridSize / height);
        var rowOffset = gy * gridSize;
        for (var x = 0; x < width; x++) {
          var gx = Math.Min(gridSize - 1, x * gridSize / width);
          var idx = rowOffset + gx;
          sums[idx] += row[x].PackedValue;
          counts[idx]++;
        }
      }
    });

    var dabs = new List<BrushDab>(gridSize * gridSize);
    var radius = 1.0 / gridSize;
    for (var row = 0; row < gridSize; row++) {
      for (var col = 0; col < gridSize; col++) {
        var idx = row * gridSize + col;
        if (counts[idx] == 0)
          continue;
        var mean = (byte)(sums[idx] / counts[idx]);
        if (mean < threshold)
          continue;
        dabs.Add(new BrushDab(
          X: (col + 0.5) / gridSize,
          Y: (row + 0.5) / gridSize,
          Radius: radius,
          Flow: mean / 255.0));
      }
    }

    return dabs;
  }
}
