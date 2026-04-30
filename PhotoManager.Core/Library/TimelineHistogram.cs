namespace PhotoManager.Core.Library;

/// <summary>
/// Granularity of a timeline histogram bucket. Auto-picked from the span
/// of the input dates: tight spans get a daily breakdown, wider spans
/// roll up to weeks or months so the bar count stays manageable.
/// </summary>
public enum TimelineGranularity {
  Day,
  Week,
  Month
}

/// <summary>
/// One bar on the timeline scrubber. <see cref="BucketStart"/> is the first
/// instant of the bucket (inclusive); <see cref="Count"/> is how many photos
/// fall in [BucketStart, next bucket start).
/// </summary>
public readonly record struct TimelineBar(DateTime BucketStart, int Count, TimelineGranularity Granularity);

/// <summary>
/// Pure-logic helper that rolls a list of photo timestamps into bucketed
/// counts for the timeline scrubber control. Granularity auto-scales: short
/// libraries get daily detail, long ones get monthly bars so the strip
/// doesn't explode into thousands of pixels per year.
/// </summary>
public static class TimelineHistogram {
  /// <summary>
  /// Build a histogram from photo timestamps. Granularity is auto-picked
  /// from the data span: &lt; 1 year = Day, &lt; 5 years = Week, otherwise
  /// Month. Buckets without photos are still emitted (count 0) so the
  /// scrubber renders a continuous strip with visible gaps.
  /// </summary>
  public static IReadOnlyList<TimelineBar> Build(IEnumerable<(DateTime, FileInfo)> photos, TimelineGranularity? forcedGranularity = null) {
    ArgumentNullException.ThrowIfNull(photos);

    var dates = photos.Select(p => p.Item1.Date).Where(d => d > DateTime.MinValue).ToList();
    if (dates.Count == 0)
      return Array.Empty<TimelineBar>();

    dates.Sort();
    var first = dates[0];
    var last = dates[^1];
    var granularity = forcedGranularity ?? PickGranularity(first, last);

    var buckets = new SortedDictionary<DateTime, int>();
    foreach (var d in dates) {
      var key = BucketStart(d, granularity);
      buckets[key] = buckets.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    // Densify: emit zero buckets between the first and last so the strip
    // shows lulls instead of squashing busy periods together.
    var result = new List<TimelineBar>(buckets.Count);
    var cursor = BucketStart(first, granularity);
    var endCursor = BucketStart(last, granularity);
    while (cursor <= endCursor) {
      buckets.TryGetValue(cursor, out var count);
      result.Add(new TimelineBar(cursor, count, granularity));
      cursor = NextBucket(cursor, granularity);
    }
    return result;
  }

  /// <summary>
  /// Auto-pick a granularity. &lt; 1 year span = Day, &lt; 5 years = Week,
  /// otherwise Month. Picked so the resulting bar count stays in the
  /// hundreds — too few looks sparse, too many overflows the scrubber.
  /// </summary>
  public static TimelineGranularity PickGranularity(DateTime first, DateTime last) {
    var span = last - first;
    if (span < TimeSpan.FromDays(365))
      return TimelineGranularity.Day;
    if (span < TimeSpan.FromDays(365 * 5))
      return TimelineGranularity.Week;
    return TimelineGranularity.Month;
  }

  private static DateTime BucketStart(DateTime date, TimelineGranularity g) => g switch {
    TimelineGranularity.Day => date.Date,
    TimelineGranularity.Week => StartOfIsoWeek(date),
    TimelineGranularity.Month => new DateTime(date.Year, date.Month, 1),
    _ => date.Date
  };

  private static DateTime NextBucket(DateTime bucketStart, TimelineGranularity g) => g switch {
    TimelineGranularity.Day => bucketStart.AddDays(1),
    TimelineGranularity.Week => bucketStart.AddDays(7),
    TimelineGranularity.Month => bucketStart.AddMonths(1),
    _ => bucketStart.AddDays(1)
  };

  private static DateTime StartOfIsoWeek(DateTime date) {
    // ISO week starts on Monday; back the date up to Monday of its week.
    var diff = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
    return date.Date.AddDays(-diff);
  }
}
