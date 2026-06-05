namespace Hawkynt.PhotoManager.Core.Faces;

/// <summary>
/// A single year bucket in a person's timeline, recording how many photos
/// that person appeared in during that year.
/// </summary>
public sealed record YearBucket(int Year, int PhotoCount);

/// <summary>
/// Aggregated timeline data for a single person: when they first and last
/// appeared in the library, plus a per-year photo count breakdown.
/// </summary>
public sealed record PersonTimelineData(
  string PersonName,
  DateOnly FirstSeen,
  DateOnly LastSeen,
  IReadOnlyList<YearBucket> YearBuckets
);

/// <summary>
/// Pure query that builds a per-year histogram for one person's appearances
/// in the library. No IO, no side effects — callers supply the already-known
/// (date, file) pairs and get back a sorted breakdown.
/// </summary>
public static class PersonTimeline {
  /// <summary>
  /// Build timeline data for a single person from their photo appearances.
  /// Returns <c>null</c> when <paramref name="photosWithPerson"/> is empty.
  /// Year buckets are sorted ascending by year.
  /// </summary>
  public static PersonTimelineData? Build(string personName, IEnumerable<(DateTime date, FileInfo file)> photosWithPerson) {
    ArgumentNullException.ThrowIfNull(personName);
    ArgumentNullException.ThrowIfNull(photosWithPerson);

    var photos = photosWithPerson.ToList();
    if (photos.Count == 0)
      return null;

    var firstDate = photos.Min(p => p.date);
    var lastDate = photos.Max(p => p.date);

    var buckets = photos
      .GroupBy(p => p.date.Year)
      .OrderBy(g => g.Key)
      .Select(g => new YearBucket(g.Key, g.Count()))
      .ToList();

    return new PersonTimelineData(
      personName,
      DateOnly.FromDateTime(firstDate),
      DateOnly.FromDateTime(lastDate),
      buckets
    );
  }
}
