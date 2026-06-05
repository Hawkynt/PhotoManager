namespace Hawkynt.PhotoManager.Core.Geo;

/// <summary>
/// Result of geographic clustering: a center point with all member files.
/// When <see cref="Members"/> has exactly one entry the cluster represents a
/// single pin; when it has more, it represents a cluster pin that should
/// display the count.
/// </summary>
public sealed record GeoClusterResult(
  double CenterLat,
  double CenterLon,
  IReadOnlyList<FileInfo> Members
) {
  /// <summary>True when this cluster represents a single photo pin.</summary>
  public bool IsSinglePin => this.Members.Count == 1;
}

/// <summary>
/// Pure clustering engine that groups geographic points into a bounded number
/// of clusters using k-means on normalized lat/lon (Euclidean distance on
/// degree coordinates — sufficient for display clustering, not geodesic
/// precision).
///
/// The algorithm:
/// 1. If the number of input points is already &lt;= maxClusters, each point
///    becomes its own cluster (no aggregation needed).
/// 2. Otherwise, run k-means with <paramref name="maxClusters"/> centroids,
///    seeded via k-means++ initialization, for up to 50 iterations or until
///    centroids converge.
/// </summary>
public static class GeoCluster {
  /// <summary>Maximum k-means iterations before we declare convergence.</summary>
  private const int MaxIterations = 50;

  /// <summary>
  /// Convergence threshold — when the largest centroid shift (in degrees) is
  /// below this value we stop iterating.
  /// </summary>
  private const double ConvergenceThreshold = 1e-6;

  /// <summary>
  /// Cluster the supplied geographic points into at most <paramref name="maxClusters"/> groups.
  /// </summary>
  /// <param name="points">Input points with lat/lon and associated file.</param>
  /// <param name="maxClusters">Upper bound on the number of output clusters (default 50).</param>
  /// <returns>A list of cluster results, each containing a center and its member files.</returns>
  public static IReadOnlyList<GeoClusterResult> Cluster(
    IReadOnlyList<(double lat, double lon, FileInfo file)> points,
    int maxClusters = 50
  ) {
    if (points.Count == 0)
      return Array.Empty<GeoClusterResult>();

    if (maxClusters < 1)
      maxClusters = 1;

    // When we have fewer points than the max cluster count, each point is its
    // own cluster — no aggregation needed.
    if (points.Count <= maxClusters) {
      return points
        .Select(p => new GeoClusterResult(p.lat, p.lon, new[] { p.file }))
        .ToList();
    }

    // Deduplicate exact-same-location points so k-means doesn't waste
    // centroids on identical coordinates.
    var k = Math.Min(maxClusters, points.Count);

    // Seed centroids via k-means++.
    var centroids = KMeansPlusPlusSeed(points, k);

    // Assignment array: which centroid each point belongs to.
    var assignments = new int[points.Count];

    for (var iteration = 0; iteration < MaxIterations; iteration++) {
      // Assignment step: assign each point to its nearest centroid.
      for (var i = 0; i < points.Count; i++) {
        var bestDist = double.MaxValue;
        var bestK = 0;
        for (var c = 0; c < centroids.Length; c++) {
          var dist = DistSq(points[i].lat, points[i].lon, centroids[c].lat, centroids[c].lon);
          if (dist < bestDist) {
            bestDist = dist;
            bestK = c;
          }
        }
        assignments[i] = bestK;
      }

      // Update step: recompute centroids as the mean of assigned points.
      var newCentroids = new (double lat, double lon)[centroids.Length];
      var counts = new int[centroids.Length];

      for (var i = 0; i < points.Count; i++) {
        var c = assignments[i];
        newCentroids[c].lat += points[i].lat;
        newCentroids[c].lon += points[i].lon;
        counts[c]++;
      }

      var maxShift = 0.0;
      for (var c = 0; c < centroids.Length; c++) {
        if (counts[c] == 0) {
          // Empty cluster — keep the old centroid.
          newCentroids[c] = centroids[c];
        } else {
          newCentroids[c].lat /= counts[c];
          newCentroids[c].lon /= counts[c];
        }

        var shift = DistSq(centroids[c].lat, centroids[c].lon, newCentroids[c].lat, newCentroids[c].lon);
        if (shift > maxShift)
          maxShift = shift;
      }

      centroids = newCentroids;

      if (maxShift < ConvergenceThreshold * ConvergenceThreshold)
        break;
    }

    // Build result clusters.
    var groups = new List<FileInfo>[centroids.Length];
    for (var c = 0; c < centroids.Length; c++)
      groups[c] = new List<FileInfo>();

    for (var i = 0; i < points.Count; i++)
      groups[assignments[i]].Add(points[i].file);

    var results = new List<GeoClusterResult>();
    for (var c = 0; c < centroids.Length; c++) {
      if (groups[c].Count == 0)
        continue;
      results.Add(new GeoClusterResult(centroids[c].lat, centroids[c].lon, groups[c]));
    }

    return results;
  }

  /// <summary>
  /// Cluster points using a viewport-aware distance threshold instead of a
  /// fixed cluster count. Two points that are closer together than
  /// <paramref name="thresholdDegrees"/> (in lat/lon space) get merged. This
  /// is the overload the map UI calls on every zoom change — the threshold is
  /// derived from the current map resolution so clusters split apart as the
  /// user zooms in.
  /// </summary>
  public static IReadOnlyList<GeoClusterResult> ClusterByDistance(
    IReadOnlyList<(double lat, double lon, FileInfo file)> points,
    double thresholdDegrees
  ) {
    if (points.Count == 0)
      return Array.Empty<GeoClusterResult>();

    if (thresholdDegrees <= 0)
      return points.Select(p => new GeoClusterResult(p.lat, p.lon, new[] { p.file })).ToList();

    // Simple greedy grid-based clustering: sort points into grid cells whose
    // size equals thresholdDegrees, then merge each occupied cell into one
    // cluster. This runs in O(n) and produces visually stable clusters that
    // don't jump around when zooming incrementally.
    var thresholdSq = thresholdDegrees * thresholdDegrees;
    var cellSize = thresholdDegrees;

    // Group points by grid cell.
    var cells = new Dictionary<(int, int), List<int>>();
    for (var i = 0; i < points.Count; i++) {
      var cellX = (int)Math.Floor(points[i].lon / cellSize);
      var cellY = (int)Math.Floor(points[i].lat / cellSize);
      var key = (cellX, cellY);
      if (!cells.TryGetValue(key, out var list)) {
        list = new List<int>();
        cells[key] = list;
      }
      list.Add(i);
    }

    var results = new List<GeoClusterResult>();
    foreach (var (_, indices) in cells) {
      var sumLat = 0.0;
      var sumLon = 0.0;
      var members = new List<FileInfo>(indices.Count);
      foreach (var i in indices) {
        sumLat += points[i].lat;
        sumLon += points[i].lon;
        members.Add(points[i].file);
      }
      results.Add(new GeoClusterResult(
        sumLat / indices.Count,
        sumLon / indices.Count,
        members));
    }

    return results;
  }

  /// <summary>
  /// Squared Euclidean distance on degree coordinates. Using squared distance
  /// avoids a sqrt in the inner loop — fine for comparison purposes.
  /// </summary>
  private static double DistSq(double lat1, double lon1, double lat2, double lon2) {
    var dLat = lat2 - lat1;
    var dLon = lon2 - lon1;
    return dLat * dLat + dLon * dLon;
  }

  /// <summary>
  /// K-means++ seeding: pick the first centroid uniformly at random, then
  /// each subsequent centroid with probability proportional to its squared
  /// distance from the nearest already-chosen centroid. This yields better
  /// initial placement than pure random and converges faster.
  /// </summary>
  private static (double lat, double lon)[] KMeansPlusPlusSeed(
    IReadOnlyList<(double lat, double lon, FileInfo file)> points,
    int k
  ) {
    var rng = new Random(42); // deterministic seed for reproducibility
    var centroids = new (double lat, double lon)[k];

    // Pick the first centroid at random.
    var first = rng.Next(points.Count);
    centroids[0] = (points[first].lat, points[first].lon);

    // Distances from each point to the nearest chosen centroid.
    var minDist = new double[points.Count];
    for (var i = 0; i < points.Count; i++)
      minDist[i] = DistSq(points[i].lat, points[i].lon, centroids[0].lat, centroids[0].lon);

    for (var c = 1; c < k; c++) {
      // Build cumulative distribution from minDist.
      var total = 0.0;
      for (var i = 0; i < points.Count; i++)
        total += minDist[i];

      // Pick the next centroid.
      if (total <= 0) {
        // All remaining points are at existing centroids — degenerate case.
        centroids[c] = centroids[c - 1];
      } else {
        var threshold = rng.NextDouble() * total;
        var cumulative = 0.0;
        var chosen = 0;
        for (var i = 0; i < points.Count; i++) {
          cumulative += minDist[i];
          if (cumulative >= threshold) {
            chosen = i;
            break;
          }
        }
        centroids[c] = (points[chosen].lat, points[chosen].lon);
      }

      // Update minDist.
      for (var i = 0; i < points.Count; i++) {
        var d = DistSq(points[i].lat, points[i].lon, centroids[c].lat, centroids[c].lon);
        if (d < minDist[i])
          minDist[i] = d;
      }
    }

    return centroids;
  }
}
