using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Develop;

/// <summary>
/// Saliency-driven crop-aspect suggester. For each requested aspect ratio,
/// slides a window across the image and picks the position whose Sobel-edge
/// density is highest. The integral image lets every candidate window's
/// score be computed in O(1) so the search stays cheap even on 4k previews.
///
/// "Saliency" here is content-importance via edge density — a fast,
/// dependency-free proxy for "interesting stuff in this region". It's not
/// a deep-learning saliency model; it's good enough to bias crops toward
/// the subject instead of flat sky / blank wall.
/// </summary>
public static class AutoCropSuggester {
  /// <summary>
  /// Default aspect ratios, in width/height order: 1:1, 4:5, 3:2, 16:9, 2:3,
  /// 5:7. Mix of square, portrait, landscape, cinematic.
  /// </summary>
  public static readonly double[] DefaultAspectRatios = {
    1.0, 0.8, 1.5, 1.778, 0.667, 1.5 / (5.0 / 7.0)
  };

  /// <summary>
  /// Compute one suggested crop per aspect ratio. Coordinates are normalised
  /// 0..1 so the result drops directly into <see cref="DevelopSettings"/>'s
  /// CropLeft/Top/Right/Bottom.
  /// </summary>
  public static IReadOnlyList<CropSuggestion> Suggest(Image<Rgba32> source, double[] aspectRatios) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(aspectRatios);

    var width = source.Width;
    var height = source.Height;
    if (width < 4 || height < 4 || aspectRatios.Length == 0)
      return Array.Empty<CropSuggestion>();

    // 1) Edge magnitude per pixel via 3x3 Sobel.
    var edges = ComputeEdgeMagnitudes(source, width, height);

    // 2) Integral image of the edge map. Each cell holds the cumulative
    //    sum of edges in [0,x] × [0,y]. integral has size (W+1) × (H+1)
    //    with a zero border so window queries don't need bounds checks.
    var integral = BuildIntegralImage(edges, width, height);

    var suggestions = new List<CropSuggestion>(aspectRatios.Length);
    foreach (var aspect in aspectRatios) {
      if (aspect <= 0 || double.IsNaN(aspect) || double.IsInfinity(aspect))
        continue;

      var best = FindBestCropForAspect(integral, width, height, aspect);
      suggestions.Add(best);
    }
    return suggestions;
  }

  private static double[] ComputeEdgeMagnitudes(Image<Rgba32> source, int width, int height) {
    var pixels = new Rgba32[width * height];
    source.CopyPixelDataTo(pixels);
    var edges = new double[width * height];

    for (var y = 1; y < height - 1; y++) {
      for (var x = 1; x < width - 1; x++) {
        var tl = Lum(pixels[(y - 1) * width + (x - 1)]);
        var tc = Lum(pixels[(y - 1) * width + x]);
        var tr = Lum(pixels[(y - 1) * width + (x + 1)]);
        var ml = Lum(pixels[y * width + (x - 1)]);
        var mr = Lum(pixels[y * width + (x + 1)]);
        var bl = Lum(pixels[(y + 1) * width + (x - 1)]);
        var bc = Lum(pixels[(y + 1) * width + x]);
        var br = Lum(pixels[(y + 1) * width + (x + 1)]);

        var gx = (tr + 2 * mr + br) - (tl + 2 * ml + bl);
        var gy = (bl + 2 * bc + br) - (tl + 2 * tc + tr);
        edges[y * width + x] = Math.Sqrt(gx * gx + gy * gy);
      }
    }
    return edges;
  }

  private static double[] BuildIntegralImage(double[] edges, int width, int height) {
    var stride = width + 1;
    var integral = new double[stride * (height + 1)];
    for (var y = 1; y <= height; y++) {
      var rowSum = 0.0;
      var rowOffset = y * stride;
      var prevRowOffset = (y - 1) * stride;
      var srcRow = (y - 1) * width;
      for (var x = 1; x <= width; x++) {
        rowSum += edges[srcRow + (x - 1)];
        integral[rowOffset + x] = integral[prevRowOffset + x] + rowSum;
      }
    }
    return integral;
  }

  private static double IntegralSum(double[] integral, int stride, int x0, int y0, int x1, int y1) {
    return integral[y1 * stride + x1]
         - integral[y0 * stride + x1]
         - integral[y1 * stride + x0]
         + integral[y0 * stride + x0];
  }

  private static CropSuggestion FindBestCropForAspect(double[] integral, int width, int height, double aspect) {
    // Compute the largest crop window that fits in the image at this aspect.
    var (cropW, cropH) = LargestWindowForAspect(width, height, aspect);
    if (cropW < 2 || cropH < 2)
      return new CropSuggestion(0, 0, 1, 1, aspect, 0);

    // Slide window across the image with a coarse step (≈ 5% of the
    // shorter side) so the search stays O(1/step^2). Final answer is then
    // refined with a finer step in a small neighbourhood of the winner.
    var coarseStep = Math.Max(1, Math.Min(cropW, cropH) / 20);
    var (bestX, bestY, bestScore) = SearchBestWindow(integral, width, height, cropW, cropH, 0, 0, width - cropW, height - cropH, coarseStep);

    // Refine inside a small box around the coarse winner.
    var refineStep = Math.Max(1, coarseStep / 4);
    var x0 = Math.Max(0, bestX - coarseStep);
    var y0 = Math.Max(0, bestY - coarseStep);
    var x1 = Math.Min(width - cropW, bestX + coarseStep);
    var y1 = Math.Min(height - cropH, bestY + coarseStep);
    var refined = SearchBestWindow(integral, width, height, cropW, cropH, x0, y0, x1, y1, refineStep);
    if (refined.Score > bestScore) {
      bestX = refined.X; bestY = refined.Y; bestScore = refined.Score;
    }

    var left = (double)bestX / width;
    var top = (double)bestY / height;
    var right = (double)(bestX + cropW) / width;
    var bottom = (double)(bestY + cropH) / height;
    var area = (double)cropW * cropH;
    var density = area > 0 ? bestScore / area : 0;
    return new CropSuggestion(left, top, right, bottom, aspect, density);
  }

  private static (int X, int Y, double Score) SearchBestWindow(
    double[] integral, int width, int height, int cropW, int cropH,
    int xStart, int yStart, int xEnd, int yEnd, int step
  ) {
    var stride = width + 1;
    var bestScore = -1.0;
    var bestX = xStart;
    var bestY = yStart;
    var bestDistFromCentre = double.PositiveInfinity;
    var imageCx = width / 2.0;
    var imageCy = height / 2.0;
    for (var y = yStart; y <= yEnd; y += step) {
      for (var x = xStart; x <= xEnd; x += step) {
        var sum = IntegralSum(integral, stride, x, y, x + cropW, y + cropH);
        var winCx = x + cropW / 2.0;
        var winCy = y + cropH / 2.0;
        var distSq = (winCx - imageCx) * (winCx - imageCx) + (winCy - imageCy) * (winCy - imageCy);
        // Tie-break toward image centre: when two positions score equally
        // (common with fully-contained subjects on a flat background), pick
        // the one whose window centre sits closest to the image centre.
        if (sum > bestScore + 1e-9
            || (Math.Abs(sum - bestScore) <= 1e-9 && distSq < bestDistFromCentre)) {
          bestScore = sum;
          bestX = x;
          bestY = y;
          bestDistFromCentre = distSq;
        }
      }
    }
    if (bestScore < 0)
      bestScore = 0;
    return (bestX, bestY, bestScore);
  }

  private static (int W, int H) LargestWindowForAspect(int imageWidth, int imageHeight, double aspect) {
    // aspect = width/height. Try fitting full height first, fall back to width.
    var widthFromHeight = imageHeight * aspect;
    if (widthFromHeight <= imageWidth)
      return ((int)Math.Floor(widthFromHeight), imageHeight);
    var heightFromWidth = imageWidth / aspect;
    return (imageWidth, (int)Math.Floor(heightFromWidth));
  }

  private static double Lum(Rgba32 p) => 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
}

/// <summary>
/// One auto-crop result. Coordinates are normalised 0..1 so callers can
/// drop them straight into <see cref="DevelopSettings.CropLeft"/> etc.
/// </summary>
public sealed record CropSuggestion(double Left, double Top, double Right, double Bottom, double AspectRatio, double Score);
