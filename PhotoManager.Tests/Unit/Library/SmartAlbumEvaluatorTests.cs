using System.Text.Json;
using PhotoManager.Core.Detection;
using PhotoManager.Core.Library;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Regions;
using PhotoManager.UI.Models;
// Disambiguate: Core.Library defines a PickRejectFilterMode for smart-album
// rule clauses (Any/Picked/Rejected/Unflagged); UI.Models defines its own
// for the cull-filter chip (ShowAll/HideRejects/PicksOnly/RejectsOnly).
using PickRejectFilterMode = PhotoManager.Core.Library.PickRejectFilterMode;

namespace PhotoManager.Tests.Unit.Library;

[TestFixture]
public class SmartAlbumEvaluatorTests {
  private static FileInfo Fake(string name) => new(Path.Combine(Path.GetTempPath(), name));

  private static (FileInfo, FullMetadata) Pair(string name, FullMetadata md) => (Fake(name), md);

  private static TaggedRegion Person(string label) => new(
    new NormalizedBoundingBox(0, 0, 0.1f, 0.1f),
    RegionCategory.Person,
    Label: label
  );

  [Test]
  public void EmptyRule_MatchesEverything() {
    var snapshot = new[] {
      Pair("a.jpg", new FullMetadata()),
      Pair("b.jpg", new FullMetadata { Rating = 5 }),
    };

    var hits = SmartAlbumEvaluator.Evaluate(snapshot, new SmartAlbumRule());

    Assert.That(hits, Has.Count.EqualTo(2));
  }

  [Test]
  public void MinRatingClause_FiltersInclusive() {
    var snapshot = new[] {
      Pair("a.jpg", new FullMetadata { Rating = 5 }),
      Pair("b.jpg", new FullMetadata { Rating = 3 }),
      Pair("c.jpg", new FullMetadata { Rating = 1 }),
      Pair("d.jpg", new FullMetadata()),
    };

    var rule = new SmartAlbumRule {
      Name = "3+",
      Clauses = new RuleClause[] { new MinRatingClause(3) }
    };

    var hits = SmartAlbumEvaluator.Evaluate(snapshot, rule);

    Assert.That(hits.Select(h => h.File.Name), Is.EquivalentTo(new[] { "a.jpg", "b.jpg" }));
  }

  [Test]
  public void KeywordClause_CaseInsensitiveByDefault() {
    var snapshot = new[] {
      Pair("a.jpg", new FullMetadata { Keywords = new[] { "Vacation", "Beach" } }),
      Pair("b.jpg", new FullMetadata { Keywords = new[] { "work" } }),
    };

    var rule = new SmartAlbumRule { Clauses = new RuleClause[] { new KeywordClause("vacation") } };

    var hits = SmartAlbumEvaluator.Evaluate(snapshot, rule);

    Assert.That(hits, Has.Count.EqualTo(1));
    Assert.That(hits[0].File.Name, Is.EqualTo("a.jpg"));
  }

  [Test]
  public void KeywordClause_CaseSensitiveWhenRequested() {
    var snapshot = new[] {
      Pair("a.jpg", new FullMetadata { Keywords = new[] { "Vacation" } }),
      Pair("b.jpg", new FullMetadata { Keywords = new[] { "vacation" } }),
    };

    var rule = new SmartAlbumRule {
      Clauses = new RuleClause[] { new KeywordClause("vacation", CaseInsensitive: false) }
    };

    var hits = SmartAlbumEvaluator.Evaluate(snapshot, rule);

    Assert.That(hits.Select(h => h.File.Name), Is.EquivalentTo(new[] { "b.jpg" }));
  }

  [Test]
  public void PersonClause_MatchesPersonRegionAndPersonsShown() {
    var snapshot = new[] {
      Pair("a.jpg", new FullMetadata { Regions = new[] { Person("Alice") } }),
      Pair("b.jpg", new FullMetadata { PersonsShown = new[] { "Alice Cooper" } }),
      Pair("c.jpg", new FullMetadata()),
    };

    var rule = new SmartAlbumRule { Clauses = new RuleClause[] { new PersonClause("Alice") } };

    var hits = SmartAlbumEvaluator.Evaluate(snapshot, rule);

    Assert.That(hits.Select(h => h.File.Name), Is.EquivalentTo(new[] { "a.jpg", "b.jpg" }));
  }

  [Test]
  public void LocationClause_ChecksAnyPlaceField() {
    var snapshot = new[] {
      Pair("a.jpg", new FullMetadata { City = "Berlin" }),
      Pair("b.jpg", new FullMetadata { Country = "Germany" }),
      Pair("c.jpg", new FullMetadata { Keywords = new[] { "Berlin" } }),
    };

    var rule = new SmartAlbumRule { Clauses = new RuleClause[] { new LocationClause("Berlin") } };

    var hits = SmartAlbumEvaluator.Evaluate(snapshot, rule);

    Assert.That(hits.Select(h => h.File.Name), Is.EquivalentTo(new[] { "a.jpg" }));
  }

  [Test]
  public void ColorLabelClause_ExactMatchCaseInsensitive() {
    var snapshot = new[] {
      Pair("a.jpg", new FullMetadata { ColorLabel = "Green" }),
      Pair("b.jpg", new FullMetadata { ColorLabel = "Red" }),
      Pair("c.jpg", new FullMetadata()),
    };

    var rule = new SmartAlbumRule { Clauses = new RuleClause[] { new ColorLabelClause("green") } };

    var hits = SmartAlbumEvaluator.Evaluate(snapshot, rule);

    Assert.That(hits.Select(h => h.File.Name), Is.EquivalentTo(new[] { "a.jpg" }));
  }

  [Test]
  public void PickStateClause_RejectedMatchesOnlyMinusOne() {
    var snapshot = new[] {
      Pair("a.jpg", new FullMetadata { Rating = -1 }),
      Pair("b.jpg", new FullMetadata { Rating = 0 }),
      Pair("c.jpg", new FullMetadata { Rating = 4 }),
      Pair("d.jpg", new FullMetadata()),
    };

    var rule = new SmartAlbumRule {
      Clauses = new RuleClause[] { new PickStateClause(PickRejectFilterMode.Rejected) }
    };

    var hits = SmartAlbumEvaluator.Evaluate(snapshot, rule);

    Assert.That(hits.Select(h => h.File.Name), Is.EquivalentTo(new[] { "a.jpg" }));
  }

  [Test]
  public void PickStateClause_UnflaggedMatchesNullOrZero() {
    var snapshot = new[] {
      Pair("a.jpg", new FullMetadata { Rating = -1 }),
      Pair("b.jpg", new FullMetadata { Rating = 0 }),
      Pair("c.jpg", new FullMetadata()),
    };

    var rule = new SmartAlbumRule {
      Clauses = new RuleClause[] { new PickStateClause(PickRejectFilterMode.Unflagged) }
    };

    var hits = SmartAlbumEvaluator.Evaluate(snapshot, rule);

    Assert.That(hits.Select(h => h.File.Name), Is.EquivalentTo(new[] { "b.jpg", "c.jpg" }));
  }

  [Test]
  public void DateRangeClause_FiltersInclusiveBounds() {
    var snapshot = new[] {
      Pair("a.jpg", new FullMetadata { DateCreated = new DateTime(2024, 6, 1) }),
      Pair("b.jpg", new FullMetadata { DateCreated = new DateTime(2023, 12, 31) }),
      Pair("c.jpg", new FullMetadata()),
    };

    var rule = new SmartAlbumRule {
      Clauses = new RuleClause[] {
        new DateRangeClause(new DateTime(2024, 1, 1), new DateTime(2024, 12, 31))
      }
    };

    var hits = SmartAlbumEvaluator.Evaluate(snapshot, rule);

    Assert.That(hits.Select(h => h.File.Name), Is.EquivalentTo(new[] { "a.jpg" }));
  }

  [Test]
  public void DateRangeClause_OpenEndedFromAcceptsAnythingBeforeTo() {
    var snapshot = new[] {
      Pair("a.jpg", new FullMetadata { DateCreated = new DateTime(1990, 1, 1) }),
      Pair("b.jpg", new FullMetadata { DateCreated = new DateTime(2030, 1, 1) }),
    };

    var rule = new SmartAlbumRule {
      Clauses = new RuleClause[] { new DateRangeClause(null, new DateTime(2024, 12, 31)) }
    };

    var hits = SmartAlbumEvaluator.Evaluate(snapshot, rule);

    Assert.That(hits.Select(h => h.File.Name), Is.EquivalentTo(new[] { "a.jpg" }));
  }

  [Test]
  public void GpsBoxClause_MatchesPointsInsideBoundingBox() {
    var snapshot = new[] {
      Pair("inside.jpg", new FullMetadata { Gps = new GpsCoordinate(52.5, 13.4) }),     // Berlin
      Pair("outside.jpg", new FullMetadata { Gps = new GpsCoordinate(40.7, -74.0) }),    // NYC
      Pair("nogps.jpg", new FullMetadata()),
    };

    var rule = new SmartAlbumRule {
      Clauses = new RuleClause[] { new GpsBoxClause(50.0, 55.0, 10.0, 15.0) }
    };

    var hits = SmartAlbumEvaluator.Evaluate(snapshot, rule);

    Assert.That(hits.Select(h => h.File.Name), Is.EquivalentTo(new[] { "inside.jpg" }));
  }

  [Test]
  public void MultipleClauses_AndCombines() {
    var snapshot = new[] {
      Pair("a.jpg", new FullMetadata { Rating = 4, Keywords = new[] { "vacation" }, City = "Berlin" }),
      Pair("b.jpg", new FullMetadata { Rating = 4, Keywords = new[] { "work" }, City = "Berlin" }),
      Pair("c.jpg", new FullMetadata { Rating = 1, Keywords = new[] { "vacation" }, City = "Berlin" }),
    };

    var rule = new SmartAlbumRule {
      Clauses = new RuleClause[] {
        new MinRatingClause(3),
        new KeywordClause("vacation"),
        new LocationClause("Berlin")
      }
    };

    var hits = SmartAlbumEvaluator.Evaluate(snapshot, rule);

    Assert.That(hits.Select(h => h.File.Name), Is.EquivalentTo(new[] { "a.jpg" }));
  }

  [Test]
  public void OrLogicCombines() {
    var snapshot = new[] {
      Pair("a.jpg", new FullMetadata { Rating = 5 }),
      Pair("b.jpg", new FullMetadata { ColorLabel = "Red" }),
      Pair("c.jpg", new FullMetadata { Rating = 1 }),
    };

    var rule = new SmartAlbumRule {
      LogicOp = LogicalOp.Or,
      Clauses = new RuleClause[] {
        new MinRatingClause(5),
        new ColorLabelClause("Red")
      }
    };

    var hits = SmartAlbumEvaluator.Evaluate(snapshot, rule);

    Assert.That(hits.Select(h => h.File.Name), Is.EquivalentTo(new[] { "a.jpg", "b.jpg" }));
  }

  [Test]
  public void RoundTrip_PolymorphicJson_PreservesAllClauseTypes() {
    var rule = new SmartAlbumRule {
      Name = "Big rule",
      LogicOp = LogicalOp.And,
      Clauses = new RuleClause[] {
        new MinRatingClause(4),
        new KeywordClause("vacation", CaseInsensitive: false),
        new PersonClause("Alice"),
        new LocationClause("Berlin"),
        new ColorLabelClause("Green"),
        new PickStateClause(PickRejectFilterMode.Picked),
        new DateRangeClause(new DateTime(2024, 1, 1), new DateTime(2024, 12, 31)),
        new GpsBoxClause(50.0, 55.0, 10.0, 15.0),
      }
    };

    var json = SmartAlbumRuleJson.Serialize(rule);
    var roundTripped = SmartAlbumRuleJson.Deserialize(json);

    Assert.That(roundTripped, Is.Not.Null);
    Assert.That(roundTripped!.Name, Is.EqualTo("Big rule"));
    Assert.That(roundTripped.LogicOp, Is.EqualTo(LogicalOp.And));
    Assert.That(roundTripped.Clauses, Has.Length.EqualTo(8));
    Assert.Multiple(() => {
      Assert.That(roundTripped.Clauses[0], Is.TypeOf<MinRatingClause>());
      Assert.That(((MinRatingClause)roundTripped.Clauses[0]).MinStars, Is.EqualTo(4));
      Assert.That(roundTripped.Clauses[1], Is.TypeOf<KeywordClause>());
      Assert.That(((KeywordClause)roundTripped.Clauses[1]).CaseInsensitive, Is.False);
      Assert.That(roundTripped.Clauses[2], Is.TypeOf<PersonClause>());
      Assert.That(roundTripped.Clauses[3], Is.TypeOf<LocationClause>());
      Assert.That(roundTripped.Clauses[4], Is.TypeOf<ColorLabelClause>());
      Assert.That(roundTripped.Clauses[5], Is.TypeOf<PickStateClause>());
      Assert.That(((PickStateClause)roundTripped.Clauses[5]).Mode, Is.EqualTo(PickRejectFilterMode.Picked));
      Assert.That(roundTripped.Clauses[6], Is.TypeOf<DateRangeClause>());
      Assert.That(roundTripped.Clauses[7], Is.TypeOf<GpsBoxClause>());
    });
  }

  [Test]
  public void UserSettingsData_MissingSmartAlbumsField_DeserializesToEmptyList() {
    // Pre-A3 settings JSON had no SmartAlbums field; round-trip must not throw
    // and must produce an empty list so users on older settings load cleanly.
    const string legacyJson = """
      {
        "LastSourceDirectory": "C:\\Photos",
        "LastDestinationDirectory": "C:\\Library",
        "DuplicateHandling": 0,
        "Recursive": true,
        "PreserveOriginals": false,
        "TreeViewPaths": [],
        "SavedSearches": []
      }
    """;

    var settings = JsonSerializer.Deserialize<UserSettingsData>(legacyJson);

    Assert.That(settings, Is.Not.Null);
    Assert.That(settings!.SmartAlbums, Is.Not.Null);
    Assert.That(settings.SmartAlbums, Is.Empty);
  }

  [Test]
  public void UserSettingsData_RoundTripWithSmartAlbums_PreservesPolymorphicClauses() {
    var original = new UserSettingsData {
      SmartAlbums = new List<SmartAlbumRule> {
        new() {
          Name = "Family-Berlin",
          Clauses = new RuleClause[] {
            new PersonClause("Alice"),
            new LocationClause("Berlin"),
            new MinRatingClause(3)
          }
        }
      }
    };

    var json = JsonSerializer.Serialize(original);
    var roundTripped = JsonSerializer.Deserialize<UserSettingsData>(json);

    Assert.That(roundTripped, Is.Not.Null);
    Assert.That(roundTripped!.SmartAlbums, Has.Count.EqualTo(1));
    Assert.That(roundTripped.SmartAlbums[0].Name, Is.EqualTo("Family-Berlin"));
    Assert.That(roundTripped.SmartAlbums[0].Clauses, Has.Length.EqualTo(3));
    Assert.That(roundTripped.SmartAlbums[0].Clauses[0], Is.TypeOf<PersonClause>());
    Assert.That(roundTripped.SmartAlbums[0].Clauses[1], Is.TypeOf<LocationClause>());
    Assert.That(roundTripped.SmartAlbums[0].Clauses[2], Is.TypeOf<MinRatingClause>());
  }
}
