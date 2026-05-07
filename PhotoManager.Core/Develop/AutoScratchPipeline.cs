using PhotoManager.Core.Models;
using PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Develop;

/// <summary>
/// Pure auto-scratch removal pipeline: repeatedly run BOPB scratch
/// detection (or the classical Frangi fallback when the model isn't
/// installed) and feed the detected mask through LaMa inpainting until
/// the residual mask drops below a convergence threshold or a hard
/// iteration cap is hit.
///
/// Exposes diagnostic state via the static <see cref="LastDiagnostic"/>
/// field so the UI can tell the user *why* scratches still appeared
/// even with auto-scratch enabled (no detections / detector failed /
/// LaMa unavailable / cleaned successfully).
///
/// <para>Lifted out of the UI's interactive auto-loop button so the
/// same logic runs as a regular pipeline stage in
/// <see cref="RestorationPipeline.Apply"/>: Save As / live preview now
/// remove scratches automatically before recolour / face restore /
/// upscale stages — matching the user's mental model that "scratches
/// are luminance-domain damage on the original, fix them first then
/// feed into the AI stages."</para>
///
/// <para>Returns a freshly allocated image. Caller owns it. Returns
/// null when the LaMa inpainter isn't installed (the auto stage cannot
/// produce output without it). Returns the source clone unchanged when
/// the very first detection pass already finds less than the
/// convergence threshold's worth of damage.</para>
/// </summary>
public static class AutoScratchPipeline {
  /// <summary>Last-run diagnostic — short human-readable summary of
  /// what the auto-scratch pipeline actually did. Useful for
  /// surfacing "scratches still visible — why?" in the UI status bar.</summary>
  public static string LastDiagnostic { get; private set; } = "(not run)";

  /// <summary>
  /// Run the detect→inpaint loop on <paramref name="source"/>.
  /// </summary>
  /// <param name="ct">Cooperatively cancels between iterations.</param>
  /// <param name="progress">Per-iteration progress; reports
  /// (DoneUnits=N-completed, TotalUnits=maxIterations) so the UI can
  /// show ETA and "iteration X / Y" — actual iteration count is
  /// usually below the cap because of the convergence test.</param>
  public static Image<Rgba32>? Apply(
      Image<Rgba32> source,
      int maxIterations,
      double convergenceThresholdPct,
      CancellationToken ct = default,
      IProgress<StageProgress>? progress = null,
      double detectorThreshold = 0.4,
      int maskDilationRadius = 0) {
    ArgumentNullException.ThrowIfNull(source);
    if (maxIterations < 1) {
      LastDiagnostic = "skipped (maxIterations<1)";
      return source.Clone();
    }

    // No-progress safety stop: if iteration-over-iteration improvement
    // is tiny, the detector is just oscillating on residual noise.
    const double noProgressDeltaPct = 0.05;
    // Detection sensitivity defaults to 0.4 (BOPB's wrapper default —
    // matches what the "Auto-detect" button uses). The earlier 0.25
    // was more aggressive (catches sub-pixel hairline tears) but
    // pulled fine real-image detail (eyelashes, hair, texture grain)
    // into the mask too — LaMa then "inpainted" those bits, leaving
    // subtle artifacts that DDColor paper-tiny is sensitive to: it
    // produced near-zero chroma on otherwise-fine photos. With 0.4
    // (passed in from RestorationSettings), auto-scratch and the
    // manual Auto-detect button produce equivalent masks, so the
    // recolour stage sees the same kind of input either way.
    // Mask dilation default is now 0 (no dilation) — matches what the
    // manual brush-inpaint stage does. Earlier 2-px dilation was
    // designed to cover the soft halo around scratches, but LaMa's
    // synthesis on the extra dilated pixels was confusing DDColor
    // paper-tiny enough to collapse its chroma prediction to ~0
    // (silent grayscale recolor). With 0 dilation, auto-scratch and
    // brush-inpaint produce equivalent inputs to recolor and
    // paper-tiny works in both cases. Caller can pass a positive
    // value to re-enable for noisy scans where the residual halo is
    // a worse problem than the chroma loss.

    var working = source.Clone();
    var totalPixels = (long)working.Width * working.Height;
    var prevPct = double.PositiveInfinity;
    var lamaCalls = 0;
    var firstDetectedPct = -1.0;
    var detectorUsed = "(none)";

    progress?.Report(new StageProgress("auto-scratch", 0, maxIterations));

    for (var iter = 1; iter <= maxIterations; iter++) {
      ct.ThrowIfCancellationRequested();

      // Detect: prefer BOPB neural net, fall back to Frangi when
      // the model isn't installed.
      Image<Rgba32>? detectedMask = null;
      try {
        if (ModelRegistry.BopbScratchDetector.IsInstalled())
          using (var bopb = new OnnxScratchDetectorBOPB()) {
            if (bopb.IsAvailable) {
              detectedMask = bopb.Detect(working, threshold: detectorThreshold, ct: ct);
              detectorUsed = "BOPB";
            }
          }
        if (detectedMask is null) {
          detectedMask = ScratchDetector.Detect(working);
          detectorUsed = "Frangi";
        }
      } catch (Exception ex) {
        LastDiagnostic = $"detector ({detectorUsed}) threw: {ex.GetType().Name} after {lamaCalls} inpaint(s)";
        return working;
      }

      long maskedPixels = 0;
      detectedMask.ProcessPixelRows(a => {
        for (var y = 0; y < a.Height; y++) {
          var row = a.GetRowSpan(y);
          for (var x = 0; x < row.Length; x++)
            if (row[x].R >= 128) maskedPixels++;
        }
      });
      var pct = 100.0 * maskedPixels / totalPixels;
      if (firstDetectedPct < 0)
        firstDetectedPct = pct;

      if (pct < convergenceThresholdPct) {
        detectedMask.Dispose();
        progress?.Report(new StageProgress("auto-scratch", maxIterations, maxIterations));
        LastDiagnostic = lamaCalls == 0
          ? $"converged on iter {iter}: {detectorUsed} found {pct:F2}% (< threshold {convergenceThresholdPct:F2}%) — no inpaint needed"
          : $"converged on iter {iter} after {lamaCalls} inpaint(s); residual {pct:F2}% < {convergenceThresholdPct:F2}%";
        return working;
      }
      if (!double.IsPositiveInfinity(prevPct) && (prevPct - pct) < noProgressDeltaPct) {
        detectedMask.Dispose();
        progress?.Report(new StageProgress("auto-scratch", maxIterations, maxIterations));
        LastDiagnostic = $"stalled on iter {iter}: {pct:F2}% (no progress); ran {lamaCalls} inpaint(s)";
        return working;
      }
      prevPct = pct;

      // Dilate the mask before inpainting — without this, LaMa fills
      // only the central pixels of each scratch and the 1-2 px halo
      // around the tear stays visible.
      Image<Rgba32>? dilatedMask = null;
      try {
        dilatedMask = DilateMask(detectedMask, maskDilationRadius);
        using var inpainter = new OnnxInpainter();
        if (!inpainter.IsAvailable) {
          LastDiagnostic = $"{detectorUsed} found {pct:F2}% but LaMa not installed — no inpaint";
          return working;
        }
        var inpainted = inpainter.Inpaint(working, dilatedMask);
        if (inpainted is null) {
          LastDiagnostic = $"{detectorUsed} found {pct:F2}% but LaMa returned null on iter {iter}";
          return working;
        }
        working.Dispose();
        working = inpainted;
        lamaCalls++;
      } finally {
        detectedMask.Dispose();
        dilatedMask?.Dispose();
      }
      progress?.Report(new StageProgress("auto-scratch", iter, maxIterations));
    }
    LastDiagnostic = $"max iter ({maxIterations}) reached; ran {lamaCalls} inpaint(s); first detection={firstDetectedPct:F2}%";
    return working;
  }

  /// <summary>
  /// 8-neighbour binary dilation by <paramref name="radius"/> pixels.
  /// A pixel becomes part of the mask if any pixel within the radius
  /// is already in the mask. Used to expand the scratch detector's
  /// tight outline so LaMa inpaints the soft halo around each
  /// detected tear, not just the centerline.
  /// </summary>
  private static Image<Rgba32> DilateMask(Image<Rgba32> mask, int radius) {
    if (radius < 1)
      return mask.Clone();
    var w = mask.Width;
    var h = mask.Height;
    var result = mask.Clone();
    var snap = new byte[w * h];
    for (var iter = 0; iter < radius; iter++) {
      result.ProcessPixelRows(a => {
        for (var y = 0; y < h; y++) {
          var row = a.GetRowSpan(y);
          for (var x = 0; x < w; x++)
            snap[y * w + x] = (byte)(row[x].R >= 128 ? 1 : 0);
        }
      });
      result.ProcessPixelRows(a => {
        for (var y = 0; y < h; y++) {
          var row = a.GetRowSpan(y);
          for (var x = 0; x < w; x++) {
            if (snap[y * w + x] == 1)
              continue;
            var hit = false;
            for (var dy = -1; dy <= 1 && !hit; dy++) {
              var ny = y + dy;
              if (ny < 0 || ny >= h) continue;
              for (var dx = -1; dx <= 1; dx++) {
                if (dx == 0 && dy == 0) continue;
                var nx = x + dx;
                if (nx < 0 || nx >= w) continue;
                if (snap[ny * w + nx] == 1) { hit = true; break; }
              }
            }
            if (hit)
              row[x] = new Rgba32((byte)255, (byte)0, (byte)0, (byte)200);
          }
        }
      });
    }
    return result;
  }
}
