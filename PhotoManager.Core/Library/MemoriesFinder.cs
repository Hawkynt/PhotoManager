using System.Globalization;

namespace Hawkynt.PhotoManager.Core.Library;

/// <summary>Time window granularity for a memory group.</summary>
public enum TimeWindow {
  Day,
  Week,
  Month,
  Year,
  Decade
}

/// <summary>
/// A group of photos sharing the same time-window match against a reference date.
/// For example: "On this day, 3 years ago" or "This month, 5 years ago".
/// </summary>
public sealed class MemoryGroup {
  public TimeWindow TimeWindow { get; init; }
  public int YearsAgo { get; init; }
  public DateOnly MatchDate { get; init; }
  public IReadOnlyList<FileInfo> Photos { get; init; } = Array.Empty<FileInfo>();

  /// <summary>Human-readable header for display.</summary>
  public string Header {
    get {
      var windowLabel = this.TimeWindow switch {
        TimeWindow.Day => "On this day",
        TimeWindow.Week => "This week",
        TimeWindow.Month => "This month",
        TimeWindow.Year => "This year",
        TimeWindow.Decade => "This decade",
        _ => "Memories"
      };

      var agoLabel = this.YearsAgo switch {
        1 => "1 year ago",
        _ => $"{this.YearsAgo} years ago"
      };

      return this.TimeWindow switch {
        TimeWindow.Year => $"{windowLabel} ({this.MatchDate.Year})",
        TimeWindow.Decade => $"{windowLabel}, {agoLabel} ({DecadeLabel(this.MatchDate.Year)})",
        _ => $"{windowLabel}, {agoLabel} ({this.MatchDate:MMMM d, yyyy})"
      };
    }
  }

  private static string DecadeLabel(int year) {
    var decadeStart = year / 10 * 10;
    return $"{decadeStart}s";
  }
}

/// <summary>
/// Pure query engine that surfaces "memories" — photos taken on the same
/// calendar date (or week/month/year/decade) in past years. No side effects,
/// no IO, fully deterministic given its inputs.
/// </summary>
public static class MemoriesFinder {
  /// <summary>
  /// Find memory groups from a flat list of (capturedDate, file) pairs,
  /// evaluated against <paramref name="referenceDate"/> (typically today).
  /// Returns groups sorted by recency (most recent matching year first),
  /// then by time-window granularity (day before week before month...).
  /// Empty groups are omitted.
  /// </summary>
  public static IReadOnlyList<MemoryGroup> Find(
    IEnumerable<(DateTime capturedDate, FileInfo file)> allPhotos,
    DateTime referenceDate
  ) {
    ArgumentNullException.ThrowIfNull(allPhotos);

    var refDate = DateOnly.FromDateTime(referenceDate);
    var refYear = refDate.Year;
    var refMonth = refDate.Month;
    var refDay = refDate.Day;
    var refWeek = IsoWeekNumber(referenceDate);
    var refDecade = refYear / 10;

    // Bucket photos by their matching time windows. A single photo can appear
    // in multiple buckets (e.g., same day AND same week AND same month).
    var dayBuckets = new Dictionary<int, List<FileInfo>>();      // key = year
    var weekBuckets = new Dictionary<int, List<FileInfo>>();
    var monthBuckets = new Dictionary<int, List<FileInfo>>();
    var yearBucket = new List<FileInfo>();                        // same year as reference
    var decadeBuckets = new Dictionary<int, List<FileInfo>>();    // key = decade start year (e.g. 2010)

    foreach (var (captured, file) in allPhotos) {
      var capYear = captured.Year;

      // Same day: same month+day, different year
      if (captured.Month == refMonth && captured.Day == refDay && capYear != refYear) {
        if (!dayBuckets.TryGetValue(capYear, out var list)) {
          list = new List<FileInfo>();
          dayBuckets[capYear] = list;
        }
        list.Add(file);
      }

      // Same week: same ISO week number, different year
      var capWeek = IsoWeekNumber(captured);
      if (capWeek == refWeek && capYear != refYear) {
        // Exclude photos already in the day bucket for this year to avoid
        // exact duplicates in the UI, but actually the spec says they CAN
        // appear in multiple windows, so we include them.
        if (!weekBuckets.TryGetValue(capYear, out var list)) {
          list = new List<FileInfo>();
          weekBuckets[capYear] = list;
        }
        list.Add(file);
      }

      // Same month: same month number, different year
      if (captured.Month == refMonth && capYear != refYear) {
        if (!monthBuckets.TryGetValue(capYear, out var list)) {
          list = new List<FileInfo>();
          monthBuckets[capYear] = list;
        }
        list.Add(file);
      }

      // Same year: shows a summary of the year's photos (only the reference year itself)
      if (capYear == refYear) {
        yearBucket.Add(file);
      }

      // Same decade: same decade, different decade than current
      var capDecade = capYear / 10;
      if (capDecade == refDecade) {
        // Skip — same decade as current, we already capture those under Year
      } else {
        // Only group by decade, not individual years within the decade
        var decadeStart = capDecade * 10;
        if (!decadeBuckets.TryGetValue(decadeStart, out var list)) {
          list = new List<FileInfo>();
          decadeBuckets[decadeStart] = list;
        }
        list.Add(file);
      }
    }

    var results = new List<MemoryGroup>();

    // Day groups
    foreach (var (year, photos) in dayBuckets) {
      results.Add(new MemoryGroup {
        TimeWindow = TimeWindow.Day,
        YearsAgo = refYear - year,
        MatchDate = new DateOnly(year, refMonth, refDay),
        Photos = photos
      });
    }

    // Week groups
    foreach (var (year, photos) in weekBuckets) {
      results.Add(new MemoryGroup {
        TimeWindow = TimeWindow.Week,
        YearsAgo = refYear - year,
        MatchDate = new DateOnly(year, refMonth, Math.Min(refDay, DateTime.DaysInMonth(year, refMonth))),
        Photos = photos
      });
    }

    // Month groups
    foreach (var (year, photos) in monthBuckets) {
      results.Add(new MemoryGroup {
        TimeWindow = TimeWindow.Month,
        YearsAgo = refYear - year,
        MatchDate = new DateOnly(year, refMonth, 1),
        Photos = photos
      });
    }

    // Year group (current year summary)
    if (yearBucket.Count > 0) {
      results.Add(new MemoryGroup {
        TimeWindow = TimeWindow.Year,
        YearsAgo = 0,
        MatchDate = new DateOnly(refYear, 1, 1),
        Photos = yearBucket
      });
    }

    // Decade groups
    foreach (var (decadeStart, photos) in decadeBuckets) {
      var midDecade = decadeStart + 5;
      results.Add(new MemoryGroup {
        TimeWindow = TimeWindow.Decade,
        YearsAgo = refYear - midDecade,
        MatchDate = new DateOnly(decadeStart, 1, 1),
        Photos = photos
      });
    }

    // Sort: most recent first within each time-window, then by granularity
    results.Sort((a, b) => {
      var windowOrder = WindowSortOrder(a.TimeWindow).CompareTo(WindowSortOrder(b.TimeWindow));
      if (windowOrder != 0)
        return windowOrder;
      return a.YearsAgo.CompareTo(b.YearsAgo); // fewer years ago = more recent = first
    });

    return results;
  }

  /// <summary>
  /// Filter groups to only those matching the specified time window.
  /// </summary>
  public static IReadOnlyList<MemoryGroup> Filter(IReadOnlyList<MemoryGroup> groups, TimeWindow window) =>
    groups.Where(g => g.TimeWindow == window).ToList();

  /// <summary>ISO 8601 week number.</summary>
  internal static int IsoWeekNumber(DateTime date) =>
    ISOWeek.GetWeekOfYear(date);

  private static int WindowSortOrder(TimeWindow w) => w switch {
    TimeWindow.Day => 0,
    TimeWindow.Week => 1,
    TimeWindow.Month => 2,
    TimeWindow.Year => 3,
    TimeWindow.Decade => 4,
    _ => 99
  };
}
