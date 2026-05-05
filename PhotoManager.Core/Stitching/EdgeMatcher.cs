using SixLabors.ImageSharp;

namespace PhotoManager.Core.Stitching;

/// <summary>
/// For every pair of pieces, finds the best rigid transform that maps one
/// piece onto the other along a shared torn boundary segment.
///
/// Two-stage matcher:
/// 1) Coarse-grained sliding-window Kabsch search over both pieces' resampled
///    cyclic contours (piece B walked in REVERSE because their boundaries
///    trace opposite directions when correctly placed adjacent). For each
///    (offsetA, offsetB) pair we fit a small (default 16-sample) rigid
///    transform and check whether its residual is below a strict
///    "lock-on" threshold.
/// 2) For every candidate that locks on, greedily extend the matched arc
///    forward and backward as long as each new sample's residual stays
///    below the threshold. Re-fit Kabsch on the full extended set. The
///    score combines mean residual with a -log(arcLength) bonus so longer
///    matches outrank short coincidences.
///
/// The matcher only returns transforms — non-overlapping global layout is
/// the assembler's job.
/// </summary>
public static class EdgeMatcher {
  /// <summary>How many evenly-spaced contour points each piece is resampled to.</summary>
  public const int DefaultProfileSamples = 240;

  /// <summary>Initial Kabsch window for the coarse alignment search. Bigger
  /// seeds disambiguate rotation: a long curve segment has enough variation
  /// to lock the rotation, where short segments admit a continuum of
  /// near-equivalent fits.</summary>
  public const int DefaultSeedWindow = 60;

  /// <summary>Per-sample residual ceiling (px) the seed must beat to lock on.
  /// Tight enough to filter out coincidental shape similarity; loose enough
  /// to handle 1-2 px contour jitter from anti-aliasing.</summary>
  public const double DefaultSeedResidualCeiling = 1.5;

  /// <summary>Per-sample residual ceiling during arc extension. Sample is
  /// added to the matched arc only if its post-fit residual is below this.</summary>
  public const double DefaultExtensionResidualCeiling = 2.0;

  /// <summary>Minimum extended-arc length (in resampled samples) for a match
  /// to count. ~1/4th of the perimeter — true shared edges run substantially
  /// longer than coincidence matches that pass only a tiny seed window.</summary>
  public const int DefaultMinSupport = 60;

  public static EdgeProfile BuildProfile(DetectedPiece piece, int samples = DefaultProfileSamples) {
    ArgumentNullException.ThrowIfNull(piece);
    if (samples < 8)
      throw new ArgumentOutOfRangeException(nameof(samples), "Need at least 8 samples for a meaningful profile.");

    var contour = piece.Contour;
    if (contour.Count < 4)
      return new EdgeProfile { PieceIndex = piece.Index, Points = Array.Empty<PointF>(), ArcLength = 0 };

    // 1) Cumulative arc length around the closed contour.
    var cumulative = new double[contour.Count + 1];
    for (var i = 0; i < contour.Count; i++) {
      var next = contour[(i + 1) % contour.Count];
      var prev = contour[i];
      var dx = next.X - prev.X;
      var dy = next.Y - prev.Y;
      cumulative[i + 1] = cumulative[i] + Math.Sqrt(dx * dx + dy * dy);
    }
    var total = cumulative[^1];
    if (total <= 0)
      return new EdgeProfile { PieceIndex = piece.Index, Points = Array.Empty<PointF>(), ArcLength = 0 };

    // 2) Walk to evenly-spaced positions and linearly interpolate.
    var step = total / samples;
    var resampled = new PointF[samples];
    var seg = 0;
    for (var i = 0; i < samples; i++) {
      var target = i * step;
      while (seg < cumulative.Length - 1 && cumulative[seg + 1] < target) seg++;
      var segLen = cumulative[seg + 1] - cumulative[seg];
      var t = segLen <= 0 ? 0 : (target - cumulative[seg]) / segLen;
      var p0 = contour[seg % contour.Count];
      var p1 = contour[(seg + 1) % contour.Count];
      resampled[i] = new PointF(
          (float)(p0.X + (p1.X - p0.X) * t),
          (float)(p0.Y + (p1.Y - p0.Y) * t));
    }
    return new EdgeProfile { PieceIndex = piece.Index, Points = resampled, ArcLength = total };
  }

  /// <summary>
  /// Returns the best alignment between piece A and piece B (or null if
  /// nothing locks on). The transform maps points in B's local space into
  /// A's local space.
  /// </summary>
  public static EdgeMatch? FindBestMatch(
      EdgeProfile a, EdgeProfile b,
      int seedWindow = DefaultSeedWindow,
      double seedResidualCeiling = DefaultSeedResidualCeiling,
      double extensionResidualCeiling = DefaultExtensionResidualCeiling,
      int minSupport = DefaultMinSupport,
      double residualCeiling = double.MaxValue) {
    ArgumentNullException.ThrowIfNull(a);
    ArgumentNullException.ThrowIfNull(b);
    if (a.PieceIndex == b.PieceIndex)
      return null;
    if (a.Points.Count < seedWindow || b.Points.Count < seedWindow)
      return null;
    var n = a.Points.Count;
    if (b.Points.Count != n)
      return null;

    EdgeMatch? best = null;
    var bestScore = double.MaxValue;

    // Coarse search: try every (offsetA, offsetB). For each, fit a tight
    // seed window and, if it locks, grow the matched arc as far as it'll
    // extend.
    for (var oa = 0; oa < n; oa++) {
      for (var ob = 0; ob < n; ob++) {
        var seedResidual = FitWindow(a.Points, b.Points, oa, ob, seedWindow, n, out _, out _, out _);
        if (seedResidual >= seedResidualCeiling) continue;

        var support = ExtendMatch(a.Points, b.Points, oa, ob, seedWindow, n,
            extensionResidualCeiling, out var rotation, out var tx, out var ty,
            out var meanResidual);
        if (support < minSupport) continue;
        if (meanResidual >= residualCeiling) continue;

        // Score: longer match dominates a slightly higher residual. The 0.05
        // weighting was tuned so a 40-sample match at residual 1.5 beats a
        // 16-sample match at residual 0.4 — i.e. coverage matters more than
        // pristine fit. -log(support) keeps the scale comparable to mean
        // residual (px), so the "lower is better" semantics are preserved.
        var score = meanResidual - 0.05 * Math.Log(support);
        if (score >= bestScore) continue;
        bestScore = score;
        best = new EdgeMatch {
          PieceA = a.PieceIndex,
          PieceB = b.PieceIndex,
          Rotation = rotation,
          TranslationX = tx,
          TranslationY = ty,
          Score = meanResidual,
          Support = support
        };
      }
    }
    return best;
  }

  /// <summary>Try to extend the matched arc starting from a seed window.
  /// Walks forward then backward, adding one sample at a time and re-checking
  /// the residual against the running fit. Returns the final support count
  /// and the Kabsch transform of the extended arc.</summary>
  private static int ExtendMatch(
      IReadOnlyList<PointF> a, IReadOnlyList<PointF> b,
      int oa, int ob, int seedWindow, int n,
      double extensionCeiling,
      out double[] rotation, out double tx, out double ty, out double meanResidual) {
    var support = seedWindow;
    var startA = oa;
    var startB = ob;

    // Refit on the seed first to get the working transform.
    var residual = FitWindow(a, b, startA, startB, support, n, out rotation, out tx, out ty);
    meanResidual = residual;

    // Extend forward (advance end on both sides).
    while (support < n) {
      var nextA = (startA + support) % n;
      var nextB = ((startB - support) % n + n) % n;
      var pa = a[nextA];
      var pb = b[nextB];
      var rx = rotation[0] * pb.X + rotation[1] * pb.Y + tx;
      var ry = rotation[2] * pb.X + rotation[3] * pb.Y + ty;
      var dx = rx - pa.X;
      var dy = ry - pa.Y;
      var d = Math.Sqrt(dx * dx + dy * dy);
      if (d > extensionCeiling) break;
      support++;
      // Refit every 4 samples so the transform tracks the extending arc.
      if ((support & 3) == 0)
        FitWindow(a, b, startA, startB, support, n, out rotation, out tx, out ty);
    }
    // Extend backward (move start backward on both sides).
    while (support < n) {
      var prevA = (startA - 1 + n) % n;
      var prevB = (startB + 1) % n;
      var pa = a[prevA];
      var pb = b[prevB];
      var rx = rotation[0] * pb.X + rotation[1] * pb.Y + tx;
      var ry = rotation[2] * pb.X + rotation[3] * pb.Y + ty;
      var dx = rx - pa.X;
      var dy = ry - pa.Y;
      var d = Math.Sqrt(dx * dx + dy * dy);
      if (d > extensionCeiling) break;
      startA = prevA;
      startB = prevB;
      support++;
      if ((support & 3) == 0)
        FitWindow(a, b, startA, startB, support, n, out rotation, out tx, out ty);
    }

    // Final refit + residual on the entire arc.
    meanResidual = FitWindow(a, b, startA, startB, support, n, out rotation, out tx, out ty);
    return support;
  }

  /// <summary>
  /// Closed-form 2D rigid Kabsch: take <paramref name="window"/> point pairs
  /// from A starting at <paramref name="offsetA"/> walking forward, and from
  /// B starting at <paramref name="offsetB"/> walking BACKWARD; fit
  /// <c>p_A = R · p_B + T</c>. Returns the mean residual in pixels.
  /// </summary>
  private static double FitWindow(
      IReadOnlyList<PointF> a, IReadOnlyList<PointF> b,
      int offsetA, int offsetB, int window, int n,
      out double[] rotation, out double tx, out double ty) {
    // Centroids first (single pass).
    double cax = 0, cay = 0, cbx = 0, cby = 0;
    for (var k = 0; k < window; k++) {
      var pa = a[(offsetA + k) % n];
      var pb = b[((offsetB - k) % n + n) % n];
      cax += pa.X; cay += pa.Y;
      cbx += pb.X; cby += pb.Y;
    }
    cax /= window; cay /= window;
    cbx /= window; cby /= window;

    // Cross-covariance: 2D rigid Kabsch reduces to atan2 of off-diagonal sums.
    double hxx = 0, hxy = 0, hyx = 0, hyy = 0;
    for (var k = 0; k < window; k++) {
      var pa = a[(offsetA + k) % n];
      var pb = b[((offsetB - k) % n + n) % n];
      var ax_ = pa.X - cax;
      var ay_ = pa.Y - cay;
      var bx_ = pb.X - cbx;
      var by_ = pb.Y - cby;
      hxx += bx_ * ax_;
      hxy += bx_ * ay_;
      hyx += by_ * ax_;
      hyy += by_ * ay_;
    }

    var theta = Math.Atan2(hxy - hyx, hxx + hyy);
    var c = Math.Cos(theta);
    var s = Math.Sin(theta);
    rotation = new[] { c, -s, s, c };
    tx = cax - (c * cbx - s * cby);
    ty = cay - (s * cbx + c * cby);

    // Mean residual.
    double sum = 0;
    for (var k = 0; k < window; k++) {
      var pa = a[(offsetA + k) % n];
      var pb = b[((offsetB - k) % n + n) % n];
      var rx = c * pb.X - s * pb.Y + tx;
      var ry = s * pb.X + c * pb.Y + ty;
      var dx = rx - pa.X;
      var dy = ry - pa.Y;
      sum += Math.Sqrt(dx * dx + dy * dy);
    }
    return sum / window;
  }
}
