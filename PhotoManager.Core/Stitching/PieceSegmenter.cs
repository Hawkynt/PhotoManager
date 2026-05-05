using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Stitching;

/// <summary>
/// Pulls individual torn / cut pieces out of a flatbed scan. Strategy:
/// 1) Pick a binarisation threshold based on the requested or auto-detected
///    background colour. 2) Connected-components flood fill to label each
///    piece. 3) Drop tiny noise specks. 4) For each surviving component,
///    crop the piece, punch the background to transparent, and trace its
///    clockwise boundary contour for the matcher to consume.
/// </summary>
public static class PieceSegmenter {
  /// <summary>Components below this many foreground pixels are dropped as scanner noise.</summary>
  public const int DefaultMinPieceArea = 200;

  /// <summary>Foreground pixels are those whose luminance differs from the
  /// background by at least this much (0..255). 80 keeps antialiased
  /// tear-edge gradients out of the piece masks (which would otherwise
  /// render as dark outlines around every piece) while still picking up
  /// plenty of headroom on real photos where background contrast is high.</summary>
  public const int DefaultLuminanceDelta = 80;

  public static IReadOnlyList<DetectedPiece> Segment(
      Image<Rgba32> source,
      ScannerBackground background = ScannerBackground.Auto,
      int minPieceArea = DefaultMinPieceArea,
      int luminanceDelta = DefaultLuminanceDelta) {
    ArgumentNullException.ThrowIfNull(source);
    var w = source.Width;
    var h = source.Height;
    if (w < 4 || h < 4)
      return Array.Empty<DetectedPiece>();

    var lum = ExtractLuminance(source, w, h);
    var bgIsDark = ResolveBackground(background, lum, w, h);

    // Foreground bitmap: true = part of a piece, false = scanner bed.
    var fg = new bool[w * h];
    if (bgIsDark) {
      for (var i = 0; i < lum.Length; i++)
        fg[i] = lum[i] >= luminanceDelta;
    } else {
      for (var i = 0; i < lum.Length; i++)
        fg[i] = lum[i] <= 255 - luminanceDelta;
    }

    // Erode the foreground mask several times to drop the antialiased
    // tear-edge gradient. A 2–4 pixel band along every torn boundary
    // contains pixels with luma 80–230 — above the foreground threshold
    // but visibly darker than the piece's pure-white interior. Without
    // aggressive erosion these darker pixels render as visible seams in
    // the assembled output even when the matcher places pieces edge-to-
    // edge; with 3 erosion passes only deep-interior content survives,
    // and the assembler's gap-fill propagates that clean interior colour
    // across the (now wider) tear gaps.
    fg = ErodeForeground(fg, w, h);
    fg = ErodeForeground(fg, w, h);
    fg = ErodeForeground(fg, w, h);

    var labels = LabelConnectedComponents(fg, w, h, out var componentCount);
    var stats = CollectStats(labels, w, h, componentCount);

    var pieces = new List<DetectedPiece>();
    var keptIndex = 0;
    for (var label = 1; label <= componentCount; label++) {
      var stat = stats[label];
      if (stat.Area < minPieceArea)
        continue;

      var piece = BuildPiece(source, labels, label, stat, keptIndex, w, h);
      if (piece is null)
        continue;
      pieces.Add(piece);
      keptIndex++;
    }
    return pieces;
  }

  private static byte[] ExtractLuminance(Image<Rgba32> source, int w, int h) {
    var lum = new byte[w * h];
    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var rowOffset = y * w;
        for (var x = 0; x < row.Length; x++) {
          var p = row[x];
          // ITU-R BT.601 luma: cheap, good enough for a scanner threshold.
          lum[rowOffset + x] = (byte)((p.R * 77 + p.G * 150 + p.B * 29) >> 8);
        }
      }
    });
    return lum;
  }

  private static bool ResolveBackground(ScannerBackground hint, byte[] lum, int w, int h) {
    if (hint == ScannerBackground.Black) return true;
    if (hint == ScannerBackground.White) return false;
    // Auto: sample the four corners (3x3 block each) and pick by median.
    var samples = new List<byte>(36);
    SampleBlock(lum, w, h, 0, 0, samples);
    SampleBlock(lum, w, h, w - 3, 0, samples);
    SampleBlock(lum, w, h, 0, h - 3, samples);
    SampleBlock(lum, w, h, w - 3, h - 3, samples);
    samples.Sort();
    var median = samples[samples.Count / 2];
    return median < 128;
  }

  private static void SampleBlock(byte[] lum, int w, int h, int x, int y, List<byte> sink) {
    for (var dy = 0; dy < 3; dy++) {
      for (var dx = 0; dx < 3; dx++) {
        var px = Math.Clamp(x + dx, 0, w - 1);
        var py = Math.Clamp(y + dy, 0, h - 1);
        sink.Add(lum[py * w + px]);
      }
    }
  }

  /// <summary>One pixel of 4-connected erosion: a foreground pixel survives
  /// only if all four orthogonal neighbours are also foreground. Bottom and
  /// right edges of the image are conservatively kept foreground so we
  /// don't accidentally chew off pieces that touch the canvas border.</summary>
  private static bool[] ErodeForeground(bool[] fg, int w, int h) {
    var dst = new bool[fg.Length];
    for (var y = 0; y < h; y++) {
      for (var x = 0; x < w; x++) {
        var idx = y * w + x;
        if (!fg[idx])
          continue;
        var keep = true;
        if (x > 0     && !fg[idx - 1])     keep = false;
        if (x < w - 1 && !fg[idx + 1])     keep = false;
        if (y > 0     && !fg[idx - w])     keep = false;
        if (y < h - 1 && !fg[idx + w])     keep = false;
        dst[idx] = keep;
      }
    }
    return dst;
  }

  /// <summary>Iterative 4-connected flood fill labelling. Returns label[] (0 = background).</summary>
  private static int[] LabelConnectedComponents(bool[] fg, int w, int h, out int labelCount) {
    var labels = new int[w * h];
    var current = 0;
    var stack = new Stack<(int x, int y)>();

    for (var y = 0; y < h; y++) {
      for (var x = 0; x < w; x++) {
        var idx = y * w + x;
        if (!fg[idx] || labels[idx] != 0)
          continue;
        current++;
        stack.Push((x, y));
        while (stack.Count > 0) {
          var (sx, sy) = stack.Pop();
          var sIdx = sy * w + sx;
          if (sx < 0 || sx >= w || sy < 0 || sy >= h) continue;
          if (!fg[sIdx] || labels[sIdx] != 0) continue;
          labels[sIdx] = current;
          stack.Push((sx + 1, sy));
          stack.Push((sx - 1, sy));
          stack.Push((sx, sy + 1));
          stack.Push((sx, sy - 1));
        }
      }
    }
    labelCount = current;
    return labels;
  }

  private sealed class ComponentStat {
    public int MinX = int.MaxValue;
    public int MinY = int.MaxValue;
    public int MaxX = int.MinValue;
    public int MaxY = int.MinValue;
    public int Area;
  }

  private static ComponentStat[] CollectStats(int[] labels, int w, int h, int count) {
    var stats = new ComponentStat[count + 1];
    for (var i = 0; i <= count; i++) stats[i] = new ComponentStat();
    for (var y = 0; y < h; y++) {
      for (var x = 0; x < w; x++) {
        var l = labels[y * w + x];
        if (l == 0) continue;
        var s = stats[l];
        if (x < s.MinX) s.MinX = x;
        if (y < s.MinY) s.MinY = y;
        if (x > s.MaxX) s.MaxX = x;
        if (y > s.MaxY) s.MaxY = y;
        s.Area++;
      }
    }
    return stats;
  }

  private static DetectedPiece? BuildPiece(
      Image<Rgba32> source, int[] labels, int label,
      ComponentStat stat, int keptIndex, int srcW, int srcH) {
    var bx = stat.MinX;
    var by = stat.MinY;
    var bw = stat.MaxX - stat.MinX + 1;
    var bh = stat.MaxY - stat.MinY + 1;
    if (bw < 3 || bh < 3)
      return null;

    var cropped = new Image<Rgba32>(bw, bh);
    var maskBytes = new byte[bw * bh];
    source.ProcessPixelRows(cropped, (src, dst) => {
      for (var y = 0; y < bh; y++) {
        var srcRow = src.GetRowSpan(by + y);
        var dstRow = dst.GetRowSpan(y);
        for (var x = 0; x < bw; x++) {
          var srcIdx = (by + y) * srcW + (bx + x);
          if (labels[srcIdx] == label) {
            var p = srcRow[bx + x];
            dstRow[x] = new Rgba32(p.R, p.G, p.B, 255);
            maskBytes[y * bw + x] = 1;
          } else {
            dstRow[x] = new Rgba32(0, 0, 0, 0);
          }
        }
      }
    });

    var contour = TraceContour(maskBytes, bw, bh);
    if (contour.Count < 8) {
      cropped.Dispose();
      return null;
    }

    return new DetectedPiece {
      Index = keptIndex,
      BoundingBox = new Rectangle(bx, by, bw, bh),
      Image = cropped,
      Contour = contour,
      Area = stat.Area
    };
  }

  /// <summary>
  /// Moore-neighbour boundary tracing. Walks clockwise starting at the top-most
  /// leftmost foreground pixel. Returns the sequence of boundary pixel centres
  /// (PointF for downstream sub-pixel use). Reasonably small wrt component
  /// area for typical torn-paper shapes.
  /// </summary>
  private static IReadOnlyList<PointF> TraceContour(byte[] mask, int w, int h) {
    // Find start pixel: top row first, leftmost foreground.
    var startX = -1;
    var startY = -1;
    for (var y = 0; y < h && startY < 0; y++) {
      for (var x = 0; x < w; x++) {
        if (mask[y * w + x] != 0) { startX = x; startY = y; break; }
      }
    }
    if (startX < 0)
      return Array.Empty<PointF>();

    // 8-connected Moore neighbourhood, clockwise from East. Each entry is (dx,dy).
    int[] dx = { 1, 1, 0, -1, -1, -1, 0, 1 };
    int[] dy = { 0, 1, 1, 1, 0, -1, -1, -1 };

    var result = new List<PointF>();
    result.Add(new PointF(startX, startY));

    // Backtrack initially to "west" (index 4) — convention so we start scanning
    // at "north-west" relative to the start pixel.
    var bIdx = 4;
    var cx = startX;
    var cy = startY;
    var safety = 0;
    var maxIters = w * h * 8;
    while (safety++ < maxIters) {
      // Look around the current pixel starting from the position after the
      // backtrack direction (clockwise).
      var found = false;
      for (var k = 0; k < 8; k++) {
        var dirIdx = (bIdx + 1 + k) & 7;
        var nx = cx + dx[dirIdx];
        var ny = cy + dy[dirIdx];
        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
        if (mask[ny * w + nx] == 0) continue;
        // Step onto this neighbour. New backtrack direction is opposite of the
        // direction we came from.
        bIdx = (dirIdx + 4) & 7;
        cx = nx;
        cy = ny;
        result.Add(new PointF(cx, cy));
        found = true;
        break;
      }
      if (!found) break; // Isolated pixel.
      // Stop when we've returned to the start having taken at least one step
      // (Moore boundary tracing termination: returning to start with same prev).
      if (cx == startX && cy == startY && result.Count > 2)
        break;
    }
    if (result.Count > 1 && result[^1].X == startX && result[^1].Y == startY)
      result.RemoveAt(result.Count - 1); // Don't double the start pixel.
    return result;
  }
}
