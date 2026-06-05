using Hawkynt.PhotoManager.Core.Faces;

namespace Hawkynt.PhotoManager.Tests.Unit.Faces;

[TestFixture]
public class PersonTimelineTests {
  private static (DateTime date, FileInfo file) Photo(int year, int month, int day, string name = "photo.jpg")
    => (new DateTime(year, month, day), new FileInfo(name));

  [Test]
  public void Build_SinglePhoto_OneYearBucket() {
    var photos = new[] { Photo(2020, 6, 15) };

    var result = PersonTimeline.Build("Alice", photos);

    Assert.That(result, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(result!.PersonName, Is.EqualTo("Alice"));
      Assert.That(result.YearBuckets, Has.Count.EqualTo(1));
      Assert.That(result.YearBuckets[0].Year, Is.EqualTo(2020));
      Assert.That(result.YearBuckets[0].PhotoCount, Is.EqualTo(1));
    });
  }

  [Test]
  public void Build_MultipleYears_SortedAscending() {
    var photos = new[] {
      Photo(2022, 1, 1),
      Photo(2019, 5, 10),
      Photo(2022, 7, 20),
      Photo(2021, 3, 3)
    };

    var result = PersonTimeline.Build("Bob", photos);

    Assert.That(result, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(result!.YearBuckets, Has.Count.EqualTo(3));
      Assert.That(result.YearBuckets[0].Year, Is.EqualTo(2019));
      Assert.That(result.YearBuckets[0].PhotoCount, Is.EqualTo(1));
      Assert.That(result.YearBuckets[1].Year, Is.EqualTo(2021));
      Assert.That(result.YearBuckets[1].PhotoCount, Is.EqualTo(1));
      Assert.That(result.YearBuckets[2].Year, Is.EqualTo(2022));
      Assert.That(result.YearBuckets[2].PhotoCount, Is.EqualTo(2));
    });
  }

  [Test]
  public void Build_FirstSeen_LastSeen_Correct() {
    var photos = new[] {
      Photo(2015, 12, 25),
      Photo(2023, 1, 1),
      Photo(2019, 6, 15)
    };

    var result = PersonTimeline.Build("Carol", photos);

    Assert.That(result, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(result!.FirstSeen, Is.EqualTo(new DateOnly(2015, 12, 25)));
      Assert.That(result.LastSeen, Is.EqualTo(new DateOnly(2023, 1, 1)));
    });
  }

  [Test]
  public void Build_EmptyInput_ReturnsNull() {
    var photos = Array.Empty<(DateTime, FileInfo)>();

    var result = PersonTimeline.Build("Nobody", photos);

    Assert.That(result, Is.Null);
  }
}
