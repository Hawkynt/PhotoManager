using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hawkynt.PhotoManager.Core.Library;

/// <summary>
/// How clauses are combined. Currently only <see cref="And"/> is honoured by
/// the evaluator — <see cref="Or"/> is reserved for a future stretch where
/// nested groups can be expressed.
/// </summary>
public enum LogicalOp {
  And = 0,
  Or = 1
}

/// <summary>
/// Pick / reject filter as used by Lightroom-style flags. Maps onto the
/// XMP rating convention where -1 means rejected; 0+ means picked-or-unset.
/// </summary>
public enum PickRejectFilterMode {
  /// <summary>Don't filter on pick state (anything passes).</summary>
  Any = 0,
  /// <summary>Only files with rating &gt;= 0 (i.e. not rejected).</summary>
  Picked = 1,
  /// <summary>Only files with rating == -1.</summary>
  Rejected = 2,
  /// <summary>Only unrated files (rating is null or 0).</summary>
  Unflagged = 3
}

/// <summary>
/// One clause inside a <see cref="SmartAlbumRule"/>. Sealed records by
/// concrete type — JSON discriminator is the type name (see
/// <see cref="SmartAlbumRuleJson"/> for round-trip details).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MinRatingClause), nameof(MinRatingClause))]
[JsonDerivedType(typeof(KeywordClause), nameof(KeywordClause))]
[JsonDerivedType(typeof(PersonClause), nameof(PersonClause))]
[JsonDerivedType(typeof(LocationClause), nameof(LocationClause))]
[JsonDerivedType(typeof(ColorLabelClause), nameof(ColorLabelClause))]
[JsonDerivedType(typeof(PickStateClause), nameof(PickStateClause))]
[JsonDerivedType(typeof(DateRangeClause), nameof(DateRangeClause))]
[JsonDerivedType(typeof(GpsBoxClause), nameof(GpsBoxClause))]
public abstract record RuleClause;

public sealed record MinRatingClause(int MinStars) : RuleClause;
public sealed record KeywordClause(string Keyword, bool CaseInsensitive = true) : RuleClause;
public sealed record PersonClause(string Person) : RuleClause;
public sealed record LocationClause(string CityOrCountry) : RuleClause;
public sealed record ColorLabelClause(string Label) : RuleClause;
public sealed record PickStateClause(PickRejectFilterMode Mode) : RuleClause;
public sealed record DateRangeClause(DateTime? From, DateTime? To) : RuleClause;
public sealed record GpsBoxClause(double MinLat, double MaxLat, double MinLon, double MaxLon) : RuleClause;

/// <summary>
/// A named, multi-clause rule. Evaluated by <see cref="SmartAlbumEvaluator"/>;
/// persisted as JSON inside the user settings so a saved smart album survives
/// across sessions. <see cref="LogicOp"/> defaults to <see cref="LogicalOp.And"/>;
/// nested OR groups are a future enhancement.
/// </summary>
public sealed record SmartAlbumRule {
  public string Name { get; init; } = string.Empty;
  public RuleClause[] Clauses { get; init; } = Array.Empty<RuleClause>();
  public LogicalOp LogicOp { get; init; } = LogicalOp.And;
}

/// <summary>
/// Convenience helpers for the polymorphic JSON round-trip. Lives on the
/// rule itself so callers don't have to wire <see cref="JsonSerializerOptions"/>
/// for every read/write.
/// </summary>
public static class SmartAlbumRuleJson {
  public static readonly JsonSerializerOptions Options = new() {
    WriteIndented = true,
    PropertyNameCaseInsensitive = true
  };

  public static string Serialize(SmartAlbumRule rule)
    => JsonSerializer.Serialize(rule, Options);

  public static SmartAlbumRule? Deserialize(string json)
    => JsonSerializer.Deserialize<SmartAlbumRule>(json, Options);
}
