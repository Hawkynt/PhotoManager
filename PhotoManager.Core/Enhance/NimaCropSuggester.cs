using Hawkynt.PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.Core.Enhance;

/// <summary>
/// Smart crop suggestions ranked by NIMA aesthetic score. Generates a
/// fixed family of candidate crops in standard aspect ratios (1:1, 4:5,
/// 3:2, 16:9, 9:16) at varying positions, scores each through the
/// already-loaded NIMA model, and returns the top-K by score.
///
/// Cost: O(K_candidates × NIMA-inference) ≈ ~150 ms × 25 crops on a
/// modern NPU. Pure-aspect-ratio enumeration is intentional — it's a
/// strong baseline that respects how real prints / Instagram posts are
/// shaped without needing a separate saliency model.
/// </summary>
public static class NimaCropSuggester {
  /// <summary>The standard aspect ratios we generate candidates for.</summary>
  public static readonly IReadOnlyList<CropAspect> StandardAspects = [
    new("Square",      1.0, 1.0),
    new("Portrait 4:5", 4.0, 5.0),
    new("Landscape 3:2", 3.0, 2.0),
    new("Wide 16:9",   16.0, 9.0),
    new("Reel 9:16",    9.0, 16.0),
  ];

  /// <summary>
  /// Returns the top <paramref name="topK"/> crop suggestions ranked by
  /// NIMA score, descending. Returns an empty list if the NIMA model
  /// isn't installed / can't run.
  /// </summary>
  /// <param name="source">Full-resolution source image.</param>
  /// <param name="scorer">An OnnxAestheticScorer with NIMA loaded.</param>
  /// <param name="topK">Number of recommendations to return.</param>
  /// <param name="positionSteps">For each aspect, how many positions to evaluate
  /// per axis. 3 = corners + center on each side = 9 crops per aspect.</param>
  public static async Task<IReadOnlyList<AestheticCropSuggestion>> SuggestAsync(
      Image<Rgba32> source,
      OnnxAestheticScorer scorer,
      int topK = 3,
      int positionSteps = 3,
      CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(scorer);
    if (!scorer.IsAvailable)
      return Array.Empty<AestheticCropSuggestion>();
    if (positionSteps < 1)
      positionSteps = 1;
    if (topK < 1)
      topK = 1;

    var srcW = source.Width;
    var srcH = source.Height;

    // Build candidate rectangles. For each aspect, fit the largest rectangle
    // of that aspect inside the source; then translate it across positionSteps
    // positions on each axis. The full-frame original is always candidate #0
    // so the user can compare "auto" against "no crop".
    var candidates = new List<Rectangle> {
      new(0, 0, srcW, srcH)  // full frame baseline
    };

    foreach (var aspect in StandardAspects) {
      var targetW = srcW;
      var targetH = (int)Math.Round(srcW * aspect.Height / aspect.Width);
      if (targetH > srcH) {
        targetH = srcH;
        targetW = (int)Math.Round(srcH * aspect.Width / aspect.Height);
      }
      if (targetW < 32 || targetH < 32)
        continue;
      var freeX = srcW - targetW;
      var freeY = srcH - targetH;
      for (var sy = 0; sy < positionSteps; sy++) {
        var y = positionSteps == 1 ? freeY / 2 : (int)Math.Round(sy * (freeY / (double)(positionSteps - 1)));
        for (var sx = 0; sx < positionSteps; sx++) {
          var x = positionSteps == 1 ? freeX / 2 : (int)Math.Round(sx * (freeX / (double)(positionSteps - 1)));
          candidates.Add(new Rectangle(x, y, targetW, targetH));
        }
      }
    }

    // Score each candidate. Done sequentially because OnnxAestheticScorer's
    // session is shared; concurrent Score calls on the same session might
    // collide with the OnnxAcceleration cache's lock pattern.
    var scored = new List<AestheticCropSuggestion>(candidates.Count);
    foreach (var rect in candidates) {
      ct.ThrowIfCancellationRequested();
      using var crop = source.Clone(c => c.Crop(rect));
      var score = await scorer.ScoreAsync(crop, ct);
      if (score == null)
        continue;
      var aspectLabel = MatchAspectName(rect, srcW, srcH);
      scored.Add(new AestheticCropSuggestion(rect, score.Mean, score.StdDev, aspectLabel));
    }

    return scored
      .OrderByDescending(s => s.Score)
      .Take(topK)
      .ToList();
  }

  private static string MatchAspectName(Rectangle r, int srcW, int srcH) {
    if (r.Width == srcW && r.Height == srcH)
      return "Full frame";
    var rAspect = (double)r.Width / r.Height;
    string? best = null;
    var bestDelta = double.MaxValue;
    foreach (var a in StandardAspects) {
      var delta = Math.Abs(rAspect - a.Width / a.Height);
      if (delta < bestDelta) {
        bestDelta = delta;
        best = a.Name;
      }
    }
    return best ?? "Custom";
  }
}

/// <summary>Standard print/post aspect ratio (e.g. Square 1:1, Portrait 4:5).</summary>
public sealed record CropAspect(string Name, double Width, double Height);

/// <summary>One scored crop candidate.</summary>
public sealed record AestheticCropSuggestion(Rectangle Rectangle, double Score, double StdDev, string AspectName);
