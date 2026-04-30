using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Segmentation;

/// <summary>
/// Cheap heuristic sky detector. No ML model required — classifies pixels by
/// blue-dominance, low saturation, and low local-edge-magnitude (Sobel
/// response) in the top 60% of the image. The same code path will be reused
/// when an ONNX segmentation model is available; this implementation just
/// fills in the "no model" fallback so the UI button always does something
/// reasonable.
/// </summary>
public static class HeuristicSkyMask {
  /// <summary>
  /// Build a list of brush dabs covering the heuristically-detected sky.
  /// Returns an empty list when no pixels survive the classifier (e.g. an
  /// indoor / red / black image).
  /// </summary>
  public static IReadOnlyList<BrushDab> Build(Image<Rgba32> source) {
    ArgumentNullException.ThrowIfNull(source);
    using var alpha = BuildAlphaMask(source);
    return BrushDabsFromAlphaMask.Build(alpha);
  }

  /// <summary>
  /// Build the binary alpha mask used by <see cref="Build"/>. Exposed for
  /// tests and for callers that want to pipe the mask to other consumers.
  /// </summary>
  internal static Image<L8> BuildAlphaMask(Image<Rgba32> source) {
    var w = source.Width;
    var h = source.Height;
    var mask = new Image<L8>(w, h);
    if (w < 3 || h < 3)
      return mask;

    var topRows = (int)Math.Round(h * 0.6);
    if (topRows < 3) topRows = Math.Min(3, h);

    // Snapshot the source into flat buffers once so the Sobel pass + the
    // sky classifier can both read without nesting ProcessPixelRows calls
    // (the row accessor is a ref struct and can't cross lambdas).
    var srcPixels = new Rgba32[w * h];
    source.CopyPixelDataTo(srcPixels);
    var lum = new byte[w * h];
    for (var i = 0; i < srcPixels.Length; i++) {
      var px = srcPixels[i];
      lum[i] = (byte)Math.Clamp((int)(0.299 * px.R + 0.587 * px.G + 0.114 * px.B), 0, 255);
    }

    mask.ProcessPixelRows(accessor => {
      for (var y = 0; y < topRows; y++) {
        var dstRow = accessor.GetRowSpan(y);
        for (var x = 0; x < w; x++) {
          var px = srcPixels[y * w + x];
          if (!IsSkyCandidate(px))
            continue;
          if (EdgeMagnitude(lum, w, h, x, y) > 60)
            continue;
          dstRow[x] = new L8(255);
        }
      }
    });

    return mask;
  }

  private static bool IsSkyCandidate(Rgba32 px) {
    if (px.B <= px.R) return false;
    if (px.B <= px.G) return false;
    var max = Math.Max(px.R, Math.Max(px.G, px.B));
    var min = Math.Min(px.R, Math.Min(px.G, px.B));
    if (max == 0) return false;
    var saturation = (max - min) / (double)max;
    if (saturation > 0.6) return false;
    return px.B >= 80;
  }

  private static int EdgeMagnitude(byte[] lum, int w, int h, int x, int y) {
    if (x <= 0 || y <= 0 || x >= w - 1 || y >= h - 1)
      return 0;
    int p00 = lum[(y - 1) * w + (x - 1)], p01 = lum[(y - 1) * w + x], p02 = lum[(y - 1) * w + (x + 1)];
    int p10 = lum[y * w + (x - 1)],                                  p12 = lum[y * w + (x + 1)];
    int p20 = lum[(y + 1) * w + (x - 1)], p21 = lum[(y + 1) * w + x], p22 = lum[(y + 1) * w + (x + 1)];
    var gx = -p00 - 2 * p10 - p20 + p02 + 2 * p12 + p22;
    var gy = -p00 - 2 * p01 - p02 + p20 + 2 * p21 + p22;
    return Math.Abs(gx) + Math.Abs(gy);
  }
}
