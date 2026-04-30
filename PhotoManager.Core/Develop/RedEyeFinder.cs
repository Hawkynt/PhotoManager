using PhotoManager.Core.Detection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Develop;

/// <summary>
/// Locates compact red blobs that look like flash-induced red-eye. The
/// classifier is intentionally simple: a pixel is "red" when its red channel
/// exceeds 1.5× the maximum of green / blue AND R > 100. Adjacent red pixels
/// are flood-filled into clusters; clusters whose pixel count falls inside
/// the typical red-eye size band (4..200 px) are returned as normalised
/// bounding boxes.
///
/// When a list of face bounding boxes is provided, the search is restricted
/// to those regions so we don't fire on red lipstick / wine glasses elsewhere
/// in the frame.
/// </summary>
public static class RedEyeFinder {
  private const int MinClusterPixels = 4;
  private const int MaxClusterPixels = 200;

  public static IReadOnlyList<NormalizedBoundingBox> Find(
    Image<Rgba32> source,
    IReadOnlyList<NormalizedBoundingBox>? faceBounds = null) {
    ArgumentNullException.ThrowIfNull(source);

    var w = source.Width;
    var h = source.Height;
    if (w <= 0 || h <= 0)
      return Array.Empty<NormalizedBoundingBox>();

    var redMask = new bool[w * h];
    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < w; x++) {
          var px = row[x];
          var maxGB = Math.Max(px.G, px.B);
          if (px.R > 100 && px.R > maxGB * 1.5)
            redMask[y * w + x] = true;
        }
      }
    });

    var searchRects = ResolveSearchRects(faceBounds, w, h);
    var visited = new bool[w * h];
    var results = new List<NormalizedBoundingBox>();

    foreach (var rect in searchRects) {
      var x0 = Math.Clamp(rect.X0, 0, w);
      var y0 = Math.Clamp(rect.Y0, 0, h);
      var x1 = Math.Clamp(rect.X1, 0, w);
      var y1 = Math.Clamp(rect.Y1, 0, h);
      for (var y = y0; y < y1; y++) {
        for (var x = x0; x < x1; x++) {
          var idx = y * w + x;
          if (visited[idx] || !redMask[idx])
            continue;
          var cluster = FloodFill(redMask, visited, w, h, x, y, x0, y0, x1, y1);
          if (cluster.Count < MinClusterPixels || cluster.Count > MaxClusterPixels)
            continue;
          results.Add(ToNormalizedBox(cluster, w, h));
        }
      }
    }

    return results;
  }

  private readonly record struct PixelRect(int X0, int Y0, int X1, int Y1);

  private static IReadOnlyList<PixelRect> ResolveSearchRects(
    IReadOnlyList<NormalizedBoundingBox>? faceBounds, int w, int h) {
    if (faceBounds is null || faceBounds.Count == 0)
      return new[] { new PixelRect(0, 0, w, h) };

    var rects = new List<PixelRect>(faceBounds.Count);
    foreach (var box in faceBounds) {
      var x0 = (int)Math.Round(box.X * w);
      var y0 = (int)Math.Round(box.Y * h);
      var x1 = (int)Math.Round((box.X + box.Width) * w);
      var y1 = (int)Math.Round((box.Y + box.Height) * h);
      if (x1 <= x0 || y1 <= y0)
        continue;
      rects.Add(new PixelRect(x0, y0, x1, y1));
    }
    return rects.Count == 0 ? new[] { new PixelRect(0, 0, w, h) } : rects;
  }

  private static List<(int X, int Y)> FloodFill(
    bool[] mask, bool[] visited, int w, int h,
    int seedX, int seedY,
    int boundX0, int boundY0, int boundX1, int boundY1) {
    // Mark every connected red pixel as visited even when the cluster
    // exceeds the size band — otherwise the leftover unvisited rim seeds
    // a new (smaller) cluster on the next scan, producing phantom hits
    // inside a giant red region.
    var cluster = new List<(int X, int Y)>();
    var stack = new Stack<(int X, int Y)>();
    stack.Push((seedX, seedY));
    while (stack.Count > 0) {
      var (x, y) = stack.Pop();
      if (x < boundX0 || y < boundY0 || x >= boundX1 || y >= boundY1)
        continue;
      var idx = y * w + x;
      if (visited[idx] || !mask[idx])
        continue;
      visited[idx] = true;
      cluster.Add((x, y));
      stack.Push((x + 1, y));
      stack.Push((x - 1, y));
      stack.Push((x, y + 1));
      stack.Push((x, y - 1));
    }
    return cluster;
  }

  private static NormalizedBoundingBox ToNormalizedBox(List<(int X, int Y)> cluster, int w, int h) {
    var minX = int.MaxValue; var minY = int.MaxValue;
    var maxX = int.MinValue; var maxY = int.MinValue;
    foreach (var (x, y) in cluster) {
      if (x < minX) minX = x;
      if (y < minY) minY = y;
      if (x > maxX) maxX = x;
      if (y > maxY) maxY = y;
    }
    // Pad by 2 pixels so the heal step covers any anti-aliased rim.
    minX = Math.Max(0, minX - 2);
    minY = Math.Max(0, minY - 2);
    maxX = Math.Min(w - 1, maxX + 2);
    maxY = Math.Min(h - 1, maxY + 2);
    return new NormalizedBoundingBox(
      X: minX / (float)w,
      Y: minY / (float)h,
      Width: (maxX - minX + 1) / (float)w,
      Height: (maxY - minY + 1) / (float)h);
  }
}
