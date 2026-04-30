using PhotoManager.Core.Library;

namespace PhotoManager.Tests.Unit.Library;

[TestFixture]
public class TimelineHistogramTests {
  private static (DateTime, FileInfo) Sample(DateTime d) => (d, new FileInfo($"test_{d:yyyyMMdd_HHmmss}.jpg"));

  [Test]
  public void Build_EmptyInput_ReturnsEmpty() {
    var bars = TimelineHistogram.Build(Array.Empty<(DateTime, FileInfo)>());
    Assert.That(bars, Is.Empty);
  }

  [Test]
  public void Build_DailySpan_UsesDayGranularity() {
    var photos = new[] {
      Sample(new DateTime(2024, 1, 5, 10, 0, 0)),
      Sample(new DateTime(2024, 1, 5, 14, 0, 0)),
      Sample(new DateTime(2024, 1, 7, 9, 0, 0))
    };
    var bars = TimelineHistogram.Build(photos);
    Assert.That(bars, Is.Not.Empty);
    Assert.That(bars[0].Granularity, Is.EqualTo(TimelineGranularity.Day));
    // Spans 2024-01-05 .. 2024-01-07 inclusive = 3 daily buckets.
    Assert.That(bars, Has.Count.EqualTo(3));
    Assert.That(bars[0].Count, Is.EqualTo(2));   // two on Jan 5
    Assert.That(bars[1].Count, Is.EqualTo(0));   // Jan 6 — gap
    Assert.That(bars[2].Count, Is.EqualTo(1));   // Jan 7
  }

  [Test]
  public void Build_FiveYearSpan_UsesMonthGranularity() {
    var photos = new[] {
      Sample(new DateTime(2018, 1, 1)),
      Sample(new DateTime(2024, 6, 15))
    };
    var bars = TimelineHistogram.Build(photos);
    Assert.That(bars, Is.Not.Empty);
    Assert.That(bars[0].Granularity, Is.EqualTo(TimelineGranularity.Month));
  }

  [Test]
  public void Build_TwoYearSpan_UsesWeekGranularity() {
    var photos = new[] {
      Sample(new DateTime(2022, 1, 1)),
      Sample(new DateTime(2024, 1, 1))
    };
    var bars = TimelineHistogram.Build(photos);
    Assert.That(bars[0].Granularity, Is.EqualTo(TimelineGranularity.Week));
  }

  [Test]
  public void PickGranularity_OneMonthSpan_IsDay() {
    var g = TimelineHistogram.PickGranularity(new DateTime(2024, 1, 1), new DateTime(2024, 2, 1));
    Assert.That(g, Is.EqualTo(TimelineGranularity.Day));
  }

  [Test]
  public void Build_BucketCounts_SumToInputSize() {
    var rng = new Random(42);
    var photos = Enumerable.Range(0, 50)
      .Select(_ => Sample(new DateTime(2024, 1, 1).AddDays(rng.Next(0, 100))))
      .ToList();
    var bars = TimelineHistogram.Build(photos);
    Assert.That(bars.Sum(b => b.Count), Is.EqualTo(50));
  }
}
