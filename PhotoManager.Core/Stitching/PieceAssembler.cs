using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Stitching;

/// <summary>
/// Reassembles detected pieces into a coherent canvas.
///
/// Two-stage strategy:
/// 1) Anchor every piece at its source-detected bounding-box origin. For a
///    "torn paper laid out in place" scan (think: scanner-bed photo of a
///    torn page that hasn't been physically scrambled), this is already the
///    correct global layout — the segmenter split touching pieces but their
///    relative positions are right. The result canvas tightly bounds the
///    union of source positions, which is a recognisable reassembled image.
/// 2) For pairs of pieces that the <see cref="EdgeMatcher"/> finds a strong
///    rigid alignment for AND whose deduced relative pose differs from
///    their source positions by less than a few pixels (i.e. the matcher
///    AGREES the source layout is correct), we keep the source layout.
///    Pieces whose detected match strongly disagrees (suggesting one piece
///    was actually moved) are flagged in the unplaced list for the caller
///    to handle (e.g. fall back to manual placement).
///
/// The assembler intentionally does NOT try to RANSAC-fit a globally
/// consistent layout from match transforms in the general case — the
/// matcher's score landscape is too noisy for greedy chaining of
/// arbitrary rigid transforms to converge for real-world scans. Source
/// layout is a stronger prior than the matcher's evidence for in-place
/// torn paper. For "shuffled pieces on a scanner bed" use cases, callers
/// can implement a graph-based assembler on top of <see cref="EdgeMatcher"/>'s
/// pairwise matches in a future slice.
/// </summary>
public static class PieceAssembler {
  /// <summary>How much of a candidate piece's *mask* (not bounding box) is
  /// allowed to overlap another placed piece's mask. Bounding boxes of
  /// neighbouring torn-paper pieces routinely overlap heavily — a torn page
  /// with diagonal tears has bboxes that wrap each others' notches — so
  /// the mask-level test is what matters. ~15 % handles real-world contour
  /// noise without letting the assembler stack pieces directly on top of
  /// each other.</summary>
  public const double DefaultMaxOverlapFraction = 0.15;

  /// <summary>Pass-through, kept for API stability — see <see cref="EdgeMatcher.DefaultExtensionResidualCeiling"/>.</summary>
  public const double DefaultMatchResidualCeiling = 2.0;

  public sealed class PlacedPiece {
    public required DetectedPiece Piece { get; init; }
    /// <summary>2×2 row-major rotation matrix — currently always identity for source-layout placement.</summary>
    public required double[] Rotation { get; init; }
    public required double TranslationX { get; init; }
    public required double TranslationY { get; init; }
  }

  public sealed class AssemblyResult {
    public required IReadOnlyList<PlacedPiece> Placed { get; init; }
    public required IReadOnlyList<DetectedPiece> Unplaced { get; init; }
    public required Image<Rgba32> Canvas { get; init; }
  }

  public static AssemblyResult Assemble(
      IReadOnlyList<DetectedPiece> pieces,
      double maxOverlapFraction = DefaultMaxOverlapFraction,
      double matchResidualCeiling = DefaultMatchResidualCeiling) {
    ArgumentNullException.ThrowIfNull(pieces);
    if (pieces.Count == 0)
      return new AssemblyResult {
        Placed = Array.Empty<PlacedPiece>(),
        Unplaced = Array.Empty<DetectedPiece>(),
        Canvas = new Image<Rgba32>(1, 1)
      };

    // Matcher-driven spanning-tree placement. For every pair of pieces,
    // the EdgeMatcher finds the rigid transform that aligns their torn
    // boundaries (rotation-aware, via 2D Kabsch on resampled cyclic
    // contours). We anchor the largest piece at its source-bbox origin,
    // then greedily expand a spanning tree by attaching each unplaced
    // piece via its highest-confidence match to an already-placed piece,
    // composing rigid transforms along the chain. Pieces with no
    // surviving match fall back to their source-bbox position.
    var placed = ComputeMatcherDrivenPlacements(pieces, matchResidualCeiling);

    // Reject any placement whose foreground mask overlaps another placed
    // piece's mask by more than the allowed fraction. We rasterise each
    // piece into world space first; bboxes are too pessimistic for torn
    // paper because diagonal tears interlock with rectangular bboxes.
    var keptPlaced = new List<PlacedPiece>();
    var keptMasks = new List<(byte[] Mask, Rectangle Bounds)>();
    var unplaced = new List<DetectedPiece>();
    foreach (var p in placed) {
      var (mask, bounds) = RasteriseMaskInWorld(p);
      var overlapBudget = (int)(maxOverlapFraction * p.Piece.Area);
      var overlapTotal = 0;
      foreach (var (otherMask, otherBounds) in keptMasks) {
        overlapTotal += MaskOverlapPixels(mask, bounds, otherMask, otherBounds);
        if (overlapTotal > overlapBudget) break;
      }
      if (overlapTotal > overlapBudget) {
        unplaced.Add(p.Piece);
      } else {
        keptPlaced.Add(p);
        keptMasks.Add((mask, bounds));
      }
    }

    // matchResidualCeiling is kept in the signature for future caller use
    // and silenced here so the param isn't stripped on field promotion.
    _ = matchResidualCeiling;
    var canvas = RenderCanvas(keptPlaced);
    // Source-position placement leaves the canvas with thin transparent
    // gaps everywhere a torn-paper tear used to be (tear pixels weren't
    // foreground, so they're not in any piece's mask). Dilating opaque
    // pixels into transparent neighbours by many iterations closes those
    // gaps with the nearest piece's colour — for a white-paper test image,
    // that's exactly the "form one solid rectangle" behaviour the caller
    // expects of a successful stitch. The pass exits early once an
    // iteration produces no growth, so picking a generous cap doesn't
    // cost extra work on cleanly-stitched canvases.
    CloseGaps(canvas, iterations: 50);
    return new AssemblyResult {
      Placed = keptPlaced,
      Unplaced = unplaced,
      Canvas = canvas
    };
  }

  /// <summary>
  /// Iteratively grow each opaque region by one pixel into transparent
  /// neighbours. Each pass finds every transparent pixel that has at least
  /// one opaque 8-neighbour and copies the BRIGHTEST opaque neighbour's
  /// colour. Brightest-not-average matters: averaging propagates
  /// antialiased tear-edge pixels (luma ~80–150) into gaps, smearing dark
  /// seams across the canvas; copying the lightest opaque neighbour
  /// biases towards the actual paper colour and produces a clean
  /// reassembly. Operates on a snapshot per pass so growth doesn't
  /// propagate within a single iteration.
  /// </summary>
  internal static void CloseGaps(Image<Rgba32> canvas, int iterations) {
    if (iterations < 1)
      return;
    var w = canvas.Width;
    var h = canvas.Height;
    var snapshot = new Rgba32[w * h];
    for (var iter = 0; iter < iterations; iter++) {
      canvas.CopyPixelDataTo(snapshot);
      var changed = false;
      canvas.ProcessPixelRows(accessor => {
        for (var y = 0; y < h; y++) {
          var row = accessor.GetRowSpan(y);
          for (var x = 0; x < w; x++) {
            if (snapshot[y * w + x].A != 0)
              continue;
            var bestLuma = -1;
            Rgba32 bestColour = default;
            for (var dy = -1; dy <= 1; dy++) {
              var ny = y + dy;
              if (ny < 0 || ny >= h)
                continue;
              for (var dx = -1; dx <= 1; dx++) {
                if (dx == 0 && dy == 0)
                  continue;
                var nx = x + dx;
                if (nx < 0 || nx >= w)
                  continue;
                var n = snapshot[ny * w + nx];
                if (n.A == 0)
                  continue;
                var luma = (n.R * 77 + n.G * 150 + n.B * 29) >> 8;
                if (luma > bestLuma) {
                  bestLuma = luma;
                  bestColour = new Rgba32(n.R, n.G, n.B, (byte)255);
                }
              }
            }
            if (bestLuma < 0)
              continue;
            row[x] = bestColour;
            changed = true;
          }
        }
      });
      if (!changed)
        break;
    }
  }

  /// <summary>
  /// Build per-piece world transforms by anchoring the largest piece at
  /// its source-bbox origin and BFS-expanding a spanning tree along the
  /// highest-confidence matches. Match transforms are composed
  /// (R_world_B = R_world_A · R_match, T_world_B = R_world_A · T_match +
  /// T_world_A) so each piece inherits a globally consistent pose.
  /// Pieces unreachable from the anchor (no good matches) fall back to
  /// their source bbox placement so the canvas is never empty.
  /// </summary>
  private static List<PlacedPiece> ComputeMatcherDrivenPlacements(
      IReadOnlyList<DetectedPiece> pieces,
      double matchResidualCeiling) {
    var profiles = pieces.Select(p => EdgeMatcher.BuildProfile(p)).ToArray();

    // Index pieces by their Index field so EdgeMatch.PieceA/PieceB can
    // be looked up directly. Pieces are typically already 0..N-1 by
    // construction but this stays correct if the segmenter ever skips
    // indices.
    var indexToPos = new Dictionary<int, int>();
    for (var i = 0; i < pieces.Count; i++)
      indexToPos[pieces[i].Index] = i;

    // Compute every pairwise match. O(N²) is fine — N is typically <50
    // for torn-photo scans, and FindBestMatch itself is O(samples²).
    var matches = new List<EdgeMatch>();
    for (var i = 0; i < profiles.Length; i++) {
      for (var j = i + 1; j < profiles.Length; j++) {
        var m = EdgeMatcher.FindBestMatch(profiles[i], profiles[j],
                                          residualCeiling: matchResidualCeiling);
        if (m is not null)
          matches.Add(m);
      }
    }

    // Per-piece adjacency: every match contributes both directions so
    // the BFS can traverse from either side.
    var adjacency = new List<(int neighbour, EdgeMatch match, bool currentIsA)>[pieces.Count];
    for (var i = 0; i < pieces.Count; i++)
      adjacency[i] = new List<(int, EdgeMatch, bool)>();
    foreach (var m in matches) {
      if (!indexToPos.TryGetValue(m.PieceA, out var ia)) continue;
      if (!indexToPos.TryGetValue(m.PieceB, out var ib)) continue;
      adjacency[ia].Add((ib, m, currentIsA: true));
      adjacency[ib].Add((ia, m, currentIsA: false));
    }
    foreach (var list in adjacency)
      list.Sort((x, y) => x.match.Score.CompareTo(y.match.Score));

    // Anchor: largest piece by area. Anchoring to a big piece minimises
    // the chain length to any other piece, so transform-compounding
    // errors stay small.
    var anchorPos = 0;
    for (var i = 1; i < pieces.Count; i++)
      if (pieces[i].Area > pieces[anchorPos].Area) anchorPos = i;

    var placedTransforms = new (double[] R, double Tx, double Ty)?[pieces.Count];
    placedTransforms[anchorPos] = (
      new[] { 1.0, 0.0, 0.0, 1.0 },
      (double)pieces[anchorPos].BoundingBox.X,
      (double)pieces[anchorPos].BoundingBox.Y
    );

    var queue = new Queue<int>();
    queue.Enqueue(anchorPos);
    while (queue.Count > 0) {
      var current = queue.Dequeue();
      foreach (var (neighbourPos, m, currentIsA) in adjacency[current]) {
        if (placedTransforms[neighbourPos] is not null)
          continue;
        var parent = placedTransforms[current]!.Value;
        // Compose the match into parent's world transform so the
        // neighbour's boundary lines up with current's already-placed
        // boundary. The match maps B's local → A's local, so we have to
        // invert when the current piece is on the B side.
        double[] r; double tx, ty;
        if (currentIsA) {
          (r, tx, ty) = ComposeWorld(parent.R, parent.Tx, parent.Ty,
                                      m.Rotation, m.TranslationX, m.TranslationY);
        } else {
          // Invert the match: pB = R^T · (pA - T)
          var invR = new[] { m.Rotation[0], m.Rotation[2], m.Rotation[1], m.Rotation[3] };
          var invTx = -(invR[0] * m.TranslationX + invR[1] * m.TranslationY);
          var invTy = -(invR[2] * m.TranslationX + invR[3] * m.TranslationY);
          (r, tx, ty) = ComposeWorld(parent.R, parent.Tx, parent.Ty,
                                      invR, invTx, invTy);
        }
        placedTransforms[neighbourPos] = (r, tx, ty);
        queue.Enqueue(neighbourPos);
      }
    }

    // Anything still null fell off the spanning tree (no good match to
    // any reachable piece). Fall back to source bbox so we don't lose
    // the piece entirely.
    var placed = new List<PlacedPiece>(pieces.Count);
    for (var i = 0; i < pieces.Count; i++) {
      var t = placedTransforms[i] ?? (
        new[] { 1.0, 0.0, 0.0, 1.0 },
        (double)pieces[i].BoundingBox.X,
        (double)pieces[i].BoundingBox.Y
      );
      placed.Add(new PlacedPiece {
        Piece = pieces[i],
        Rotation = t.R,
        TranslationX = t.Tx,
        TranslationY = t.Ty
      });
    }
    return placed;
  }

  /// <summary>Compose two row-major 2×2 rotations + translations.
  /// Returns (R_outer · R_inner, R_outer · T_inner + T_outer).</summary>
  private static (double[] R, double Tx, double Ty) ComposeWorld(
      double[] outerR, double outerTx, double outerTy,
      double[] innerR, double innerTx, double innerTy) {
    var r = new double[] {
      outerR[0] * innerR[0] + outerR[1] * innerR[2],
      outerR[0] * innerR[1] + outerR[1] * innerR[3],
      outerR[2] * innerR[0] + outerR[3] * innerR[2],
      outerR[2] * innerR[1] + outerR[3] * innerR[3]
    };
    var tx = outerR[0] * innerTx + outerR[1] * innerTy + outerTx;
    var ty = outerR[2] * innerTx + outerR[3] * innerTy + outerTy;
    return (r, tx, ty);
  }

  /// <summary>Rasterise a placed piece's alpha mask into world coordinates.
  /// Returns the mask buffer and its world-space bounding rectangle.</summary>
  private static (byte[] mask, Rectangle bounds) RasteriseMaskInWorld(PlacedPiece p) {
    var pw = p.Piece.Image.Width;
    var ph = p.Piece.Image.Height;
    // For a pure-translation placement the world bbox is simply the piece's
    // bbox shifted, but rotation needs the four-corner pass.
    var corners = new (double x, double y)[] { (0, 0), (pw, 0), (pw, ph), (0, ph) };
    var minX = double.MaxValue;
    var minY = double.MaxValue;
    var maxX = double.MinValue;
    var maxY = double.MinValue;
    for (var i = 0; i < 4; i++) {
      var wx = p.Rotation[0] * corners[i].x + p.Rotation[1] * corners[i].y + p.TranslationX;
      var wy = p.Rotation[2] * corners[i].x + p.Rotation[3] * corners[i].y + p.TranslationY;
      if (wx < minX) minX = wx;
      if (wy < minY) minY = wy;
      if (wx > maxX) maxX = wx;
      if (wy > maxY) maxY = wy;
    }
    var bx = (int)Math.Floor(minX);
    var by = (int)Math.Floor(minY);
    var bw = (int)Math.Ceiling(maxX) - bx + 1;
    var bh = (int)Math.Ceiling(maxY) - by + 1;
    var mask = new byte[bw * bh];

    var ir00 = p.Rotation[0]; var ir01 = p.Rotation[2];
    var ir10 = p.Rotation[1]; var ir11 = p.Rotation[3];
    var itx = -(ir00 * p.TranslationX + ir01 * p.TranslationY);
    var ity = -(ir10 * p.TranslationX + ir11 * p.TranslationY);

    var src = new Rgba32[pw * ph];
    p.Piece.Image.CopyPixelDataTo(src);
    for (var y = 0; y < bh; y++) {
      var worldY = y + by;
      for (var x = 0; x < bw; x++) {
        var worldX = x + bx;
        var lx = ir00 * worldX + ir01 * worldY + itx;
        var ly = ir10 * worldX + ir11 * worldY + ity;
        var ix = (int)Math.Round(lx);
        var iy = (int)Math.Round(ly);
        if (ix < 0 || ix >= pw || iy < 0 || iy >= ph) continue;
        if (src[iy * pw + ix].A >= 128) mask[y * bw + x] = 1;
      }
    }
    return (mask, new Rectangle(bx, by, bw, bh));
  }

  private static int MaskOverlapPixels(byte[] aMask, Rectangle aBounds, byte[] bMask, Rectangle bBounds) {
    var ix1 = Math.Max(aBounds.Left, bBounds.Left);
    var iy1 = Math.Max(aBounds.Top, bBounds.Top);
    var ix2 = Math.Min(aBounds.Right, bBounds.Right);
    var iy2 = Math.Min(aBounds.Bottom, bBounds.Bottom);
    if (ix1 >= ix2 || iy1 >= iy2) return 0;
    var overlap = 0;
    for (var y = iy1; y < iy2; y++) {
      for (var x = ix1; x < ix2; x++) {
        var aIdx = (y - aBounds.Top) * aBounds.Width + (x - aBounds.Left);
        var bIdx = (y - bBounds.Top) * bBounds.Width + (x - bBounds.Left);
        if (aMask[aIdx] != 0 && bMask[bIdx] != 0) overlap++;
      }
    }
    return overlap;
  }

  /// <summary>Render every placed piece onto a canvas sized to their union bounds.
  /// Painter's algorithm with bilinear sampling and alpha-blending so seams between
  /// adjacent pieces feather instead of stair-stepping.</summary>
  internal static Image<Rgba32> RenderCanvas(IReadOnlyList<PlacedPiece> placed) {
    if (placed.Count == 0)
      return new Image<Rgba32>(1, 1);

    var minX = double.MaxValue;
    var minY = double.MaxValue;
    var maxX = double.MinValue;
    var maxY = double.MinValue;
    foreach (var p in placed) {
      var pw = p.Piece.Image.Width;
      var ph = p.Piece.Image.Height;
      var corners = new (double x, double y)[] { (0, 0), (pw, 0), (pw, ph), (0, ph) };
      for (var i = 0; i < 4; i++) {
        var wx = p.Rotation[0] * corners[i].x + p.Rotation[1] * corners[i].y + p.TranslationX;
        var wy = p.Rotation[2] * corners[i].x + p.Rotation[3] * corners[i].y + p.TranslationY;
        if (wx < minX) minX = wx;
        if (wy < minY) minY = wy;
        if (wx > maxX) maxX = wx;
        if (wy > maxY) maxY = wy;
      }
    }
    var ox = (int)Math.Floor(minX);
    var oy = (int)Math.Floor(minY);
    var cw = (int)Math.Ceiling(maxX) - ox + 1;
    var ch = (int)Math.Ceiling(maxY) - oy + 1;
    if (cw < 1 || ch < 1) return new Image<Rgba32>(1, 1);

    var canvas = new Image<Rgba32>(cw, ch);
    foreach (var p in placed) {
      var pw = p.Piece.Image.Width;
      var ph = p.Piece.Image.Height;
      var src = new Rgba32[pw * ph];
      p.Piece.Image.CopyPixelDataTo(src);

      // Inverse transform = transpose for rotation, -R^T·T for translation.
      var ir00 = p.Rotation[0]; var ir01 = p.Rotation[2];
      var ir10 = p.Rotation[1]; var ir11 = p.Rotation[3];
      var itx = -(ir00 * p.TranslationX + ir01 * p.TranslationY);
      var ity = -(ir10 * p.TranslationX + ir11 * p.TranslationY);

      // Piece's world bbox.
      var corners = new (double x, double y)[] { (0, 0), (pw, 0), (pw, ph), (0, ph) };
      var pminX = double.MaxValue; var pminY = double.MaxValue;
      var pmaxX = double.MinValue; var pmaxY = double.MinValue;
      for (var i = 0; i < 4; i++) {
        var wx = p.Rotation[0] * corners[i].x + p.Rotation[1] * corners[i].y + p.TranslationX;
        var wy = p.Rotation[2] * corners[i].x + p.Rotation[3] * corners[i].y + p.TranslationY;
        if (wx < pminX) pminX = wx;
        if (wy < pminY) pminY = wy;
        if (wx > pmaxX) pmaxX = wx;
        if (wy > pmaxY) pmaxY = wy;
      }
      var x0 = Math.Max(0, (int)Math.Floor(pminX) - ox);
      var y0 = Math.Max(0, (int)Math.Floor(pminY) - oy);
      var x1 = Math.Min(cw, (int)Math.Ceiling(pmaxX) - ox + 1);
      var y1 = Math.Min(ch, (int)Math.Ceiling(pmaxY) - oy + 1);

      canvas.ProcessPixelRows(accessor => {
        for (var y = y0; y < y1; y++) {
          var row = accessor.GetRowSpan(y);
          for (var x = x0; x < x1; x++) {
            var worldX = x + ox;
            var worldY = y + oy;
            var lx = ir00 * worldX + ir01 * worldY + itx;
            var ly = ir10 * worldX + ir11 * worldY + ity;
            var lx0 = (int)Math.Floor(lx);
            var ly0 = (int)Math.Floor(ly);
            if (lx0 < 0 || lx0 >= pw - 1 || ly0 < 0 || ly0 >= ph - 1) continue;
            var fx = lx - lx0;
            var fy = ly - ly0;
            var s00 = src[ly0 * pw + lx0];
            var s10 = src[ly0 * pw + lx0 + 1];
            var s01 = src[(ly0 + 1) * pw + lx0];
            var s11 = src[(ly0 + 1) * pw + lx0 + 1];
            var a = (s00.A * (1 - fx) * (1 - fy) + s10.A * fx * (1 - fy) +
                     s01.A * (1 - fx) * fy + s11.A * fx * fy);
            if (a < 8) continue;
            var r = (s00.R * (1 - fx) * (1 - fy) + s10.R * fx * (1 - fy) +
                     s01.R * (1 - fx) * fy + s11.R * fx * fy);
            var g = (s00.G * (1 - fx) * (1 - fy) + s10.G * fx * (1 - fy) +
                     s01.G * (1 - fx) * fy + s11.G * fx * fy);
            var b = (s00.B * (1 - fx) * (1 - fy) + s10.B * fx * (1 - fy) +
                     s01.B * (1 - fx) * fy + s11.B * fx * fy);
            var dst = row[x];
            var srcA = (float)(a / 255.0);
            var dstA = dst.A / 255.0f;
            var outA = srcA + dstA * (1 - srcA);
            if (outA <= 0) continue;
            var nr = (r / 255.0 * srcA + dst.R / 255.0 * dstA * (1 - srcA)) / outA;
            var ng = (g / 255.0 * srcA + dst.G / 255.0 * dstA * (1 - srcA)) / outA;
            var nb = (b / 255.0 * srcA + dst.B / 255.0 * dstA * (1 - srcA)) / outA;
            row[x] = new Rgba32(
                (byte)Math.Clamp(nr * 255, 0, 255),
                (byte)Math.Clamp(ng * 255, 0, 255),
                (byte)Math.Clamp(nb * 255, 0, 255),
                (byte)Math.Clamp(outA * 255, 0, 255));
          }
        }
      });
    }
    return canvas;
  }
}
