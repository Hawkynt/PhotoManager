using Hawkynt.PhotoManager.Core.Geo;

namespace Hawkynt.PhotoManager.Tests.Unit.Geo;

[TestFixture]
public class GeoClusterTests {
  /// <summary>Helper to create a dummy FileInfo (the file need not exist on disk).</summary>
  private static FileInfo F(string name) => new(Path.Combine(Path.GetTempPath(), name));

  // --- Cluster (k-means) tests ---

  [Test]
  public void Cluster_EmptyInput_ReturnsEmpty() {
    var result = GeoCluster.Cluster(Array.Empty<(double, double, FileInfo)>());
    Assert.That(result, Is.Empty);
  }

  [Test]
  public void Cluster_SinglePoint_OneClusterOfOne() {
    var points = new[] { (48.8566, 2.3522, F("paris.jpg")) };
    var clusters = GeoCluster.Cluster(points);

    Assert.That(clusters, Has.Count.EqualTo(1));
    Assert.That(clusters[0].Members, Has.Count.EqualTo(1));
    Assert.That(clusters[0].IsSinglePin, Is.True);
    Assert.That(clusters[0].CenterLat, Is.EqualTo(48.8566).Within(0.001));
    Assert.That(clusters[0].CenterLon, Is.EqualTo(2.3522).Within(0.001));
  }

  [Test]
  public void Cluster_TwoDistantPoints_TwoClusters() {
    // Paris and Tokyo — should never merge into one cluster.
    var points = new[] {
      (48.8566, 2.3522, F("paris.jpg")),
      (35.6762, 139.6503, F("tokyo.jpg"))
    };
    var clusters = GeoCluster.Cluster(points, maxClusters: 50);

    Assert.That(clusters, Has.Count.EqualTo(2));
    Assert.That(clusters.All(c => c.Members.Count == 1), Is.True);
  }

  [Test]
  public void Cluster_ManyPointsAtSameLocation_OneCluster() {
    // 100 points all at the Colosseum — should collapse into one cluster.
    const double lat = 41.8902;
    const double lon = 12.4922;
    var points = Enumerable.Range(0, 100)
      .Select(i => (lat, lon, F($"rome_{i:000}.jpg")))
      .ToList();

    var clusters = GeoCluster.Cluster(points, maxClusters: 50);

    Assert.That(clusters, Has.Count.EqualTo(1));
    Assert.That(clusters[0].Members, Has.Count.EqualTo(100));
    Assert.That(clusters[0].IsSinglePin, Is.False);
    Assert.That(clusters[0].CenterLat, Is.EqualTo(lat).Within(0.001));
    Assert.That(clusters[0].CenterLon, Is.EqualTo(lon).Within(0.001));
  }

  [Test]
  public void Cluster_ThreeDistinctGroups_ThreeClusters() {
    // Three well-separated cities: Paris, Tokyo, New York.
    var points = new List<(double lat, double lon, FileInfo file)>();
    // Paris area
    for (var i = 0; i < 10; i++)
      points.Add((48.85 + i * 0.001, 2.35 + i * 0.001, F($"paris_{i}.jpg")));
    // Tokyo area
    for (var i = 0; i < 10; i++)
      points.Add((35.67 + i * 0.001, 139.65 + i * 0.001, F($"tokyo_{i}.jpg")));
    // New York area
    for (var i = 0; i < 10; i++)
      points.Add((40.71 + i * 0.001, -74.00 + i * 0.001, F($"nyc_{i}.jpg")));

    var clusters = GeoCluster.Cluster(points, maxClusters: 3);

    Assert.That(clusters, Has.Count.EqualTo(3));
    // All 30 points accounted for.
    Assert.That(clusters.Sum(c => c.Members.Count), Is.EqualTo(30));
    // Each cluster should have 10 members (one per city).
    Assert.That(clusters.All(c => c.Members.Count == 10), Is.True);
  }

  [Test]
  public void Cluster_FewerPointsThanMaxClusters_EachPointIsOwnCluster() {
    var points = new[] {
      (48.8566, 2.3522, F("a.jpg")),
      (35.6762, 139.6503, F("b.jpg")),
      (40.7128, -74.0060, F("c.jpg"))
    };
    var clusters = GeoCluster.Cluster(points, maxClusters: 50);

    Assert.That(clusters, Has.Count.EqualTo(3));
    Assert.That(clusters.All(c => c.IsSinglePin), Is.True);
  }

  [Test]
  public void Cluster_MaxClustersOne_AllInSingleCluster() {
    var points = new[] {
      (48.8566, 2.3522, F("a.jpg")),
      (35.6762, 139.6503, F("b.jpg")),
      (40.7128, -74.0060, F("c.jpg"))
    };
    var clusters = GeoCluster.Cluster(points, maxClusters: 1);

    Assert.That(clusters, Has.Count.EqualTo(1));
    Assert.That(clusters[0].Members, Has.Count.EqualTo(3));
  }

  // --- ClusterByDistance tests ---

  [Test]
  public void ClusterByDistance_EmptyInput_ReturnsEmpty() {
    var result = GeoCluster.ClusterByDistance(Array.Empty<(double, double, FileInfo)>(), 1.0);
    Assert.That(result, Is.Empty);
  }

  [Test]
  public void ClusterByDistance_LargeThreshold_MergesNearbyPoints() {
    // Two points ~0.01 degrees apart — with threshold 1.0 they should merge.
    var points = new[] {
      (48.856, 2.352, F("a.jpg")),
      (48.857, 2.353, F("b.jpg"))
    };
    var clusters = GeoCluster.ClusterByDistance(points, 1.0);

    Assert.That(clusters, Has.Count.EqualTo(1));
    Assert.That(clusters[0].Members, Has.Count.EqualTo(2));
  }

  [Test]
  public void ClusterByDistance_SmallThreshold_SplitsDistantPoints() {
    // Two points in different cities with a tiny threshold — should produce 2 clusters.
    var points = new[] {
      (48.8566, 2.3522, F("paris.jpg")),
      (35.6762, 139.6503, F("tokyo.jpg"))
    };
    var clusters = GeoCluster.ClusterByDistance(points, 0.001);

    Assert.That(clusters, Has.Count.EqualTo(2));
  }

  [Test]
  public void ClusterByDistance_ZeroThreshold_EachPointItsOwnCluster() {
    var points = new[] {
      (48.856, 2.352, F("a.jpg")),
      (48.857, 2.353, F("b.jpg"))
    };
    var clusters = GeoCluster.ClusterByDistance(points, 0);

    Assert.That(clusters, Has.Count.EqualTo(2));
    Assert.That(clusters.All(c => c.IsSinglePin), Is.True);
  }

  [Test]
  public void ClusterByDistance_DifferentThresholds_ProduceDifferentCounts() {
    // Simulate re-clustering at different zoom levels: a tight cluster of
    // points should merge at coarse zoom but split at fine zoom.
    var points = new List<(double lat, double lon, FileInfo file)>();
    // 5 points spread across ~0.5 degrees
    for (var i = 0; i < 5; i++)
      points.Add((48.85 + i * 0.1, 2.35 + i * 0.1, F($"photo_{i}.jpg")));

    var coarse = GeoCluster.ClusterByDistance(points, 10.0);  // huge cells — all merge
    var fine = GeoCluster.ClusterByDistance(points, 0.01);    // tiny cells — all separate

    Assert.That(coarse.Count, Is.LessThan(fine.Count),
      "Coarse (zoomed-out) clustering should produce fewer clusters than fine (zoomed-in)");
    Assert.That(fine.Count, Is.EqualTo(5));
    Assert.That(coarse.Count, Is.EqualTo(1));
  }
}
