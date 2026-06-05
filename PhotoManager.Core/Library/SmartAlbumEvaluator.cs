using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.Core.Regions;

namespace Hawkynt.PhotoManager.Core.Library;

/// <summary>
/// Pure-logic engine for smart-album rules. No IO; every input arrives via
/// the snapshot enumerable from <see cref="MetadataCache.Snapshot"/>.
/// </summary>
public static class SmartAlbumEvaluator {
  /// <summary>
  /// Filter the supplied (file, metadata) snapshot against <paramref name="rule"/>.
  /// An empty rule matches everything; clauses are AND-combined when
  /// <see cref="SmartAlbumRule.LogicOp"/> is <see cref="LogicalOp.And"/>,
  /// OR-combined otherwise. Order is preserved; the caller decides sorting.
  /// </summary>
  public static IReadOnlyList<(FileInfo File, FullMetadata Metadata)> Evaluate(
    IEnumerable<(FileInfo File, FullMetadata Metadata)> source,
    SmartAlbumRule rule
  ) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(rule);

    var clauses = rule.Clauses ?? Array.Empty<RuleClause>();
    var matches = new List<(FileInfo, FullMetadata)>();

    foreach (var (file, metadata) in source) {
      if (Matches(metadata, clauses, rule.LogicOp))
        matches.Add((file, metadata));
    }

    return matches;
  }

  /// <summary>True when <paramref name="metadata"/> satisfies the supplied clauses under the chosen logic.</summary>
  public static bool Matches(FullMetadata metadata, IReadOnlyList<RuleClause> clauses, LogicalOp op = LogicalOp.And) {
    ArgumentNullException.ThrowIfNull(metadata);
    ArgumentNullException.ThrowIfNull(clauses);

    if (clauses.Count == 0)
      return true;

    if (op == LogicalOp.Or) {
      foreach (var clause in clauses)
        if (MatchesClause(metadata, clause))
          return true;
      return false;
    }

    foreach (var clause in clauses)
      if (!MatchesClause(metadata, clause))
        return false;
    return true;
  }

  private static bool MatchesClause(FullMetadata md, RuleClause clause) => clause switch {
    MinRatingClause c => (md.Rating ?? 0) >= c.MinStars,
    KeywordClause c => MatchesKeyword(md, c),
    PersonClause c => MatchesPerson(md, c.Person),
    LocationClause c => MatchesLocation(md, c.CityOrCountry),
    ColorLabelClause c => string.Equals(md.ColorLabel, c.Label, StringComparison.OrdinalIgnoreCase),
    PickStateClause c => MatchesPickState(md, c.Mode),
    DateRangeClause c => MatchesDateRange(md, c.From, c.To),
    GpsBoxClause c => MatchesGpsBox(md, c),
    _ => false
  };

  private static bool MatchesKeyword(FullMetadata md, KeywordClause c) {
    if (string.IsNullOrWhiteSpace(c.Keyword))
      return true;
    var cmp = c.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    foreach (var kw in md.Keywords)
      if (kw.Contains(c.Keyword, cmp))
        return true;
    return false;
  }

  private static bool MatchesPerson(FullMetadata md, string needle) {
    if (string.IsNullOrWhiteSpace(needle))
      return true;
    foreach (var region in md.Regions) {
      if (region.Category != RegionCategory.Person)
        continue;
      if (region.Label is { } label && label.Contains(needle, StringComparison.OrdinalIgnoreCase))
        return true;
    }
    foreach (var p in md.PersonsShown)
      if (p.Contains(needle, StringComparison.OrdinalIgnoreCase))
        return true;
    return false;
  }

  private static bool MatchesLocation(FullMetadata md, string needle) {
    if (string.IsNullOrWhiteSpace(needle))
      return true;
    var fields = new[] { md.Location, md.City, md.State, md.Country, md.CountryCode };
    foreach (var f in fields)
      if (f is { } v && v.Contains(needle, StringComparison.OrdinalIgnoreCase))
        return true;
    return false;
  }

  private static bool MatchesPickState(FullMetadata md, PickRejectFilterMode mode) => mode switch {
    PickRejectFilterMode.Any => true,
    PickRejectFilterMode.Rejected => md.Rating == -1,
    PickRejectFilterMode.Picked => (md.Rating ?? 0) >= 0,
    PickRejectFilterMode.Unflagged => md.Rating is null or 0,
    _ => true
  };

  private static bool MatchesDateRange(FullMetadata md, DateTime? from, DateTime? to) {
    if (md.DateCreated is not { } captured)
      return false;
    if (from is { } f && captured < f)
      return false;
    if (to is { } t && captured > t)
      return false;
    return true;
  }

  private static bool MatchesGpsBox(FullMetadata md, GpsBoxClause box) {
    if (md.Gps is not { IsValid: true } gps)
      return false;
    return gps.Latitude >= box.MinLat
        && gps.Latitude <= box.MaxLat
        && gps.Longitude >= box.MinLon
        && gps.Longitude <= box.MaxLon;
  }
}
