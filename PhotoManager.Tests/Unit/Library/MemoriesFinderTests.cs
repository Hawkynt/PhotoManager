using PhotoManager.Core.Library;

namespace PhotoManager.Tests.Unit.Library;

[TestFixture]
public class MemoriesFinderTests {
  private static (DateTime, FileInfo) Photo(string name, DateTime captured) =>
    (captured, new FileInfo(name));

  [Test]
  public void SameDayDifferentYear_FoundInDayGroup() {
    var reference = new DateTime(2026, 5, 27);
    var photos = new[] {
      Photo("old.jpg", new DateTime(2023, 5, 27, 14, 30, 0)),
      Photo("other.jpg", new DateTime(2023, 6, 15))
    };

    var groups = MemoriesFinder.Find(photos, reference);

    var dayGroups = groups.Where(g => g.TimeWindow == TimeWindow.Day).ToList();
    Assert.That(dayGroups, Has.Count.EqualTo(1));
    Assert.That(dayGroups[0].YearsAgo, Is.EqualTo(3));
    Assert.That(dayGroups[0].Photos.Select(f => f.Name), Does.Contain("old.jpg"));
  }

  [Test]
  public void SameWeekDifferentYear_FoundInWeekGroup() {
    var reference = new DateTime(2026, 5, 27); // ISO week 22
    var isoWeek = MemoriesFinder.IsoWeekNumber(reference);

    // Find a date in 2024 that falls in the same ISO week
    var candidate = Enumerable.Range(0, 7)
      .Select(i => new DateTime(2024, 5, 27).AddDays(i - 3))
      .First(d => MemoriesFinder.IsoWeekNumber(d) == isoWeek);

    var photos = new[] {
      Photo("weekmatch.jpg", candidate),
      Photo("nomatch.jpg", new DateTime(2024, 1, 15))
    };

    var groups = MemoriesFinder.Find(photos, reference);

    var weekGroups = groups.Where(g => g.TimeWindow == TimeWindow.Week).ToList();
    Assert.That(weekGroups, Has.Count.GreaterThanOrEqualTo(1));
    var matching = weekGroups.First(g => g.YearsAgo == 2);
    Assert.That(matching.Photos.Select(f => f.Name), Does.Contain("weekmatch.jpg"));
  }

  [Test]
  public void SameMonthDifferentYear_FoundInMonthGroup() {
    var reference = new DateTime(2026, 5, 27);
    var photos = new[] {
      Photo("may2020.jpg", new DateTime(2020, 5, 3)),
      Photo("june2020.jpg", new DateTime(2020, 6, 3))
    };

    var groups = MemoriesFinder.Find(photos, reference);

    var monthGroups = groups.Where(g => g.TimeWindow == TimeWindow.Month).ToList();
    Assert.That(monthGroups, Has.Count.GreaterThanOrEqualTo(1));
    var may2020 = monthGroups.FirstOrDefault(g => g.YearsAgo == 6);
    Assert.That(may2020, Is.Not.Null);
    Assert.That(may2020!.Photos.Select(f => f.Name), Does.Contain("may2020.jpg"));
    Assert.That(may2020.Photos.Select(f => f.Name), Does.Not.Contain("june2020.jpg"));
  }

  [Test]
  public void SameDecade_FoundInDecadeGroup() {
    var reference = new DateTime(2026, 5, 27); // decade = 2020s
    var photos = new[] {
      Photo("from2015.jpg", new DateTime(2015, 8, 10)),
      Photo("from2018.jpg", new DateTime(2018, 3, 20)),
      Photo("from2023.jpg", new DateTime(2023, 5, 27))   // same decade as reference → NOT in decade group
    };

    var groups = MemoriesFinder.Find(photos, reference);

    var decadeGroups = groups.Where(g => g.TimeWindow == TimeWindow.Decade).ToList();
    Assert.That(decadeGroups, Has.Count.GreaterThanOrEqualTo(1));
    // 2015 and 2018 are in the 2010s decade, which differs from 2020s
    var decade2010 = decadeGroups.FirstOrDefault(g => g.MatchDate.Year == 2010);
    Assert.That(decade2010, Is.Not.Null);
    Assert.That(decade2010!.Photos.Select(f => f.Name), Does.Contain("from2015.jpg"));
    Assert.That(decade2010.Photos.Select(f => f.Name), Does.Contain("from2018.jpg"));
    // 2023 is same decade (2020s) so should NOT appear in decade groups
    Assert.That(decadeGroups.SelectMany(g => g.Photos).Select(f => f.Name), Does.Not.Contain("from2023.jpg"));
  }

  [Test]
  public void CurrentYear_ExcludedFromDayWeekMonth() {
    var reference = new DateTime(2026, 5, 27);
    var photos = new[] {
      Photo("today.jpg", new DateTime(2026, 5, 27, 10, 0, 0)),  // same year as reference
      Photo("past.jpg", new DateTime(2024, 5, 27, 10, 0, 0))     // different year
    };

    var groups = MemoriesFinder.Find(photos, reference);

    var dayGroups = groups.Where(g => g.TimeWindow == TimeWindow.Day).ToList();
    // today.jpg is in 2026 which = reference year → excluded from Day
    Assert.That(dayGroups.SelectMany(g => g.Photos).Select(f => f.Name), Does.Not.Contain("today.jpg"));
    // past.jpg is in 2024 → included
    Assert.That(dayGroups.SelectMany(g => g.Photos).Select(f => f.Name), Does.Contain("past.jpg"));

    // Same for week and month — 2026 photos excluded
    var weekGroups = groups.Where(g => g.TimeWindow == TimeWindow.Week).ToList();
    Assert.That(weekGroups.SelectMany(g => g.Photos).Select(f => f.Name), Does.Not.Contain("today.jpg"));

    var monthGroups = groups.Where(g => g.TimeWindow == TimeWindow.Month).ToList();
    Assert.That(monthGroups.SelectMany(g => g.Photos).Select(f => f.Name), Does.Not.Contain("today.jpg"));
  }

  [Test]
  public void CurrentYear_AppearsInYearGroup() {
    var reference = new DateTime(2026, 5, 27);
    var photos = new[] {
      Photo("thisyear.jpg", new DateTime(2026, 3, 15))
    };

    var groups = MemoriesFinder.Find(photos, reference);

    var yearGroups = groups.Where(g => g.TimeWindow == TimeWindow.Year).ToList();
    Assert.That(yearGroups, Has.Count.EqualTo(1));
    Assert.That(yearGroups[0].YearsAgo, Is.EqualTo(0));
    Assert.That(yearGroups[0].Photos.Select(f => f.Name), Does.Contain("thisyear.jpg"));
  }

  [Test]
  public void NoMatches_ReturnsEmpty() {
    var reference = new DateTime(2026, 5, 27);
    var photos = Array.Empty<(DateTime, FileInfo)>();

    var groups = MemoriesFinder.Find(photos, reference);

    Assert.That(groups, Is.Empty);
  }

  [Test]
  public void MultipleYearsOfMatches_SortedByRecency() {
    var reference = new DateTime(2026, 5, 27);
    var photos = new[] {
      Photo("2020.jpg", new DateTime(2020, 5, 27)),
      Photo("2023.jpg", new DateTime(2023, 5, 27)),
      Photo("2024.jpg", new DateTime(2024, 5, 27))
    };

    var groups = MemoriesFinder.Find(photos, reference);

    var dayGroups = groups.Where(g => g.TimeWindow == TimeWindow.Day).ToList();
    Assert.That(dayGroups, Has.Count.EqualTo(3));
    // Sorted by YearsAgo ascending = most recent first
    Assert.That(dayGroups[0].YearsAgo, Is.EqualTo(2));  // 2024
    Assert.That(dayGroups[1].YearsAgo, Is.EqualTo(3));  // 2023
    Assert.That(dayGroups[2].YearsAgo, Is.EqualTo(6));  // 2020
  }

  [Test]
  public void LeapDay_Feb29_NoMatchInNonLeapYear() {
    // Feb 29 only exists in leap years. In non-leap years there is no
    // Feb 29 photo so the day group should simply be empty — the finder
    // does exact month+day matching, so Feb 29 won't accidentally match
    // Feb 28 or Mar 1.
    var reference = new DateTime(2024, 2, 29); // 2024 is a leap year
    var photos = new[] {
      // 2023 is NOT a leap year — no Feb 29 photos possible from real
      // cameras, but if someone manually set one it should match.
      Photo("feb28_2023.jpg", new DateTime(2023, 2, 28)),
      Photo("mar01_2023.jpg", new DateTime(2023, 3, 1)),
      // 2020 IS a leap year
      Photo("feb29_2020.jpg", new DateTime(2020, 2, 29))
    };

    var groups = MemoriesFinder.Find(photos, reference);

    var dayGroups = groups.Where(g => g.TimeWindow == TimeWindow.Day).ToList();
    // Only feb29_2020.jpg should match (exact day = 29, month = 2)
    var dayPhotos = dayGroups.SelectMany(g => g.Photos).Select(f => f.Name).ToList();
    Assert.That(dayPhotos, Does.Contain("feb29_2020.jpg"));
    Assert.That(dayPhotos, Does.Not.Contain("feb28_2023.jpg"));
    Assert.That(dayPhotos, Does.Not.Contain("mar01_2023.jpg"));
  }

  [Test]
  public void Filter_ReturnsOnlyMatchingWindow() {
    var reference = new DateTime(2026, 5, 27);
    var photos = new[] {
      Photo("day.jpg", new DateTime(2023, 5, 27)),
      Photo("month.jpg", new DateTime(2023, 5, 10))
    };

    var allGroups = MemoriesFinder.Find(photos, reference);

    var dayOnly = MemoriesFinder.Filter(allGroups, TimeWindow.Day);
    Assert.That(dayOnly.All(g => g.TimeWindow == TimeWindow.Day), Is.True);

    var monthOnly = MemoriesFinder.Filter(allGroups, TimeWindow.Month);
    Assert.That(monthOnly.All(g => g.TimeWindow == TimeWindow.Month), Is.True);
  }

  [Test]
  public void Header_FormatsCorrectly() {
    var group = new MemoryGroup {
      TimeWindow = TimeWindow.Day,
      YearsAgo = 3,
      MatchDate = new DateOnly(2023, 5, 27),
      Photos = new[] { new FileInfo("test.jpg") }
    };

    Assert.That(group.Header, Does.Contain("On this day"));
    Assert.That(group.Header, Does.Contain("3 years ago"));
    Assert.That(group.Header, Does.Contain("2023"));
  }

  [Test]
  public void Header_SingleYear_UsesCorrectGrammar() {
    var group = new MemoryGroup {
      TimeWindow = TimeWindow.Week,
      YearsAgo = 1,
      MatchDate = new DateOnly(2025, 5, 27),
      Photos = new[] { new FileInfo("test.jpg") }
    };

    Assert.That(group.Header, Does.Contain("1 year ago"));
    Assert.That(group.Header, Does.Not.Contain("1 years ago"));
  }

  [Test]
  public void DecadeHeader_ShowsDecadeLabel() {
    var group = new MemoryGroup {
      TimeWindow = TimeWindow.Decade,
      YearsAgo = 15,
      MatchDate = new DateOnly(2010, 1, 1),
      Photos = new[] { new FileInfo("test.jpg") }
    };

    Assert.That(group.Header, Does.Contain("This decade"));
    Assert.That(group.Header, Does.Contain("2010s"));
  }

  [Test]
  public void YearHeader_ShowsYearOnly() {
    var group = new MemoryGroup {
      TimeWindow = TimeWindow.Year,
      YearsAgo = 0,
      MatchDate = new DateOnly(2026, 1, 1),
      Photos = new[] { new FileInfo("test.jpg") }
    };

    Assert.That(group.Header, Does.Contain("This year"));
    Assert.That(group.Header, Does.Contain("2026"));
  }

  [Test]
  public void GroupsSortedByWindowThenRecency() {
    var reference = new DateTime(2026, 5, 27);
    var photos = new[] {
      Photo("day2024.jpg", new DateTime(2024, 5, 27)),
      Photo("day2020.jpg", new DateTime(2020, 5, 27)),
      Photo("month2022.jpg", new DateTime(2022, 5, 10)),
      Photo("decade2015.jpg", new DateTime(2015, 8, 1))
    };

    var groups = MemoriesFinder.Find(photos, reference);

    // Day groups should come before Week, Week before Month, etc.
    var windows = groups.Select(g => g.TimeWindow).ToList();
    for (var i = 1; i < windows.Count; i++) {
      Assert.That(windows[i], Is.GreaterThanOrEqualTo(windows[i - 1]),
        $"Group at index {i} ({windows[i]}) should not precede {windows[i - 1]}");
    }

    // Within each window type, YearsAgo should be ascending (most recent first)
    foreach (var window in new[] { TimeWindow.Day, TimeWindow.Week, TimeWindow.Month }) {
      var yearsAgo = groups.Where(g => g.TimeWindow == window).Select(g => g.YearsAgo).ToList();
      for (var i = 1; i < yearsAgo.Count; i++) {
        Assert.That(yearsAgo[i], Is.GreaterThanOrEqualTo(yearsAgo[i - 1]),
          $"Within {window}, YearsAgo should be ascending");
      }
    }
  }

  [Test]
  public void PhotoCanAppearInMultipleWindows() {
    // A photo from May 27, 2024 should appear in Day, Week, AND Month
    // groups for a May 27, 2026 reference date.
    var reference = new DateTime(2026, 5, 27);
    var photos = new[] {
      Photo("multi.jpg", new DateTime(2024, 5, 27))
    };

    var groups = MemoriesFinder.Find(photos, reference);

    var windowsContaining = groups
      .Where(g => g.Photos.Any(f => f.Name == "multi.jpg"))
      .Select(g => g.TimeWindow)
      .ToHashSet();

    Assert.That(windowsContaining, Does.Contain(TimeWindow.Day));
    Assert.That(windowsContaining, Does.Contain(TimeWindow.Month));
    // Week depends on whether the ISO week lines up, which it may or may not
  }
}
