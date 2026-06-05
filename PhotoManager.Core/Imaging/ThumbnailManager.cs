using System.Collections.Concurrent;
using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;

namespace Hawkynt.PhotoManager.Core.Imaging;

/// <summary>
/// Adaptive resize service. Callers hand it a source image and target
/// dimensions; it picks a resampling algorithm balancing quality against
/// observed throughput on this machine, then performs the resize.
///
/// <para>The choice is per-call: scale factor decides which family of
/// algorithms produces the best quality (Box/Triangle for downscale,
/// Bicubic/Triangle for upscale), and a sliding window of measured
/// pixels-per-second per algorithm decides which member of that family
/// fits the 1-second target. A consistently slow CPU naturally degrades
/// to the fastest option (NearestNeighbor) over a few resizes; once the
/// CPU recovers (e.g. another model finishes inference and frees cores)
/// the next resize automatically promotes back up the quality ladder.</para>
///
/// <para>Throughput is tracked per algorithm so the estimator doesn't
/// blame Box for what was actually a Bicubic-shaped slowdown. Numbers
/// start at +∞ until the first measurement so the first call always
/// gets the best-quality option (no cold-start regression).</para>
/// </summary>
public static class ThumbnailManager {
  /// <summary>The wall-clock budget for a single resize. The
  /// estimator picks the highest-quality algorithm whose recent
  /// average runtime fits inside this budget for the requested
  /// pixel count. Set to 1 second per the original design intent —
  /// previews should never block the UI for longer than that.</summary>
  private const double BudgetSeconds = 1.0;

  /// <summary>EWMA weight for new throughput measurements. 0.3 means
  /// the latest measurement contributes 30% to the running estimate;
  /// the rest comes from prior history. Keeps the estimator stable
  /// against one-off outliers (GC pause, background work) but still
  /// adapts within a handful of resizes when conditions change.</summary>
  private const double EwmaAlpha = 0.3;

  // Per-algorithm throughput in (work) pixels per second. "Work" pixels =
  // max(srcPixels, dstPixels) — the algorithm's cost is dominated by
  // the larger of input or output footprint depending on direction.
  // Concurrent dictionary so multiple Resize calls from background
  // tasks (e.g. parallel thumbnail extraction) don't trample each
  // other's measurements.
  private static readonly ConcurrentDictionary<string, double> _throughput = new();

  /// <summary>Resize <paramref name="source"/> to (<paramref name="targetW"/>,
  /// <paramref name="targetH"/>) using an adaptively-chosen resampler.
  /// Returns a freshly allocated image; caller owns it. Source is not
  /// mutated.</summary>
  public static Image<Rgba32> Resize(Image<Rgba32> source, int targetW, int targetH) {
    ArgumentNullException.ThrowIfNull(source);
    if (targetW <= 0 || targetH <= 0)
      throw new ArgumentOutOfRangeException(nameof(targetW), $"Target dimensions must be positive ({targetW}x{targetH}).");

    var srcPixels = (long)source.Width * source.Height;
    var dstPixels = (long)targetW * targetH;

    // Identity-resize shortcut — common when the caller doesn't know
    // ahead of time whether scaling is needed. Clone is still required
    // to satisfy the "freshly allocated, caller owns" contract.
    if (source.Width == targetW && source.Height == targetH)
      return source.Clone();

    var resampler = ChooseResampler(srcPixels, dstPixels);
    var sw = Stopwatch.StartNew();
    var output = source.Clone(c => c.Resize(targetW, targetH, resampler));
    sw.Stop();
    RecordThroughput(resampler, Math.Max(srcPixels, dstPixels), sw.Elapsed.TotalSeconds);
    return output;
  }

  /// <summary>Diagnostic — returns the per-algorithm throughput
  /// observed so far in this process. Useful for the UI status bar
  /// or test assertions.</summary>
  public static IReadOnlyDictionary<string, double> ObservedThroughput => _throughput;

  /// <summary>Walk the quality-ranked candidates for this scale
  /// direction and pick the highest-quality one whose estimated
  /// runtime for <paramref name="workPixels"/> fits the budget. Falls
  /// through to the cheapest candidate when even Box / Triangle would
  /// blow the budget — that's the "CPU is slow, accept lower quality"
  /// path.</summary>
  private static IResampler ChooseResampler(long srcPixels, long dstPixels) {
    var workPixels = Math.Max(srcPixels, dstPixels);
    var candidates = CandidatesByQuality(srcPixels, dstPixels);
    IResampler? cheapest = null;
    foreach (var candidate in candidates) {
      cheapest = candidate;
      var estimate = EstimateSeconds(candidate, workPixels);
      if (estimate <= BudgetSeconds)
        return candidate;
    }
    // Even the cheapest candidate (NearestNeighbor) was estimated to
    // bust the budget — there's nothing faster, so just go with it.
    return cheapest ?? KnownResamplers.NearestNeighbor;
  }

  /// <summary>Best-quality-first list of candidate resamplers for a
  /// given scale direction. Downscale prefers Box (anti-aliased
  /// averaging — both fastest AND best quality for shrinking) over
  /// Triangle / NearestNeighbor. Upscale prefers Bicubic (sharper
  /// reconstruction) over Triangle / NearestNeighbor.</summary>
  private static IEnumerable<IResampler> CandidatesByQuality(long srcPixels, long dstPixels) {
    if (dstPixels < srcPixels) {
      // Downscale: Box AVERAGES source pixels into target — it's the
      // textbook anti-aliased downsampler. Bicubic on heavy downscale
      // can introduce ringing and isn't quality-superior here.
      yield return KnownResamplers.Box;
      yield return KnownResamplers.Triangle;
      yield return KnownResamplers.NearestNeighbor;
    } else {
      // Upscale: Bicubic preserves edge sharpness; Triangle is its
      // softer/faster substitute; NearestNeighbor is the last resort.
      yield return KnownResamplers.Bicubic;
      yield return KnownResamplers.Triangle;
      yield return KnownResamplers.NearestNeighbor;
    }
  }

  /// <summary>Estimate seconds for <paramref name="resampler"/> to
  /// process <paramref name="workPixels"/>. Returns 0 (assume
  /// instant) when no measurement exists yet — the first call to a
  /// given algorithm always gets it as a candidate.</summary>
  private static double EstimateSeconds(IResampler resampler, long workPixels) {
    var name = resampler.GetType().Name;
    if (!_throughput.TryGetValue(name, out var rate) || rate <= 0)
      return 0.0;
    return workPixels / rate;
  }

  private static void RecordThroughput(IResampler resampler, long workPixels, double seconds) {
    if (seconds <= 0)
      return;
    var rate = workPixels / seconds;
    var name = resampler.GetType().Name;
    _throughput.AddOrUpdate(
      name,
      rate,
      (_, prev) => prev * (1 - EwmaAlpha) + rate * EwmaAlpha);
  }

  /// <summary>Test hook: clear the throughput history so subsequent
  /// resizes start from a clean slate. NOT for production code.</summary>
  internal static void ResetThroughputForTests() => _throughput.Clear();
}
