using PhotoManager.Core.Detection;
using PhotoManager.Core.Library;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Regions;
using PhotoManager.Core.Services;

namespace PhotoManager.Tests.Unit.Library;

[TestFixture]
public class LibraryIndexTests {
  private DirectoryInfo _root = null!;

  [SetUp]
  public void Setup() {
    this._root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-libidx-" + Guid.NewGuid().ToString("N")));
    this._root.Create();
  }

  [TearDown]
  public void Teardown() {
    if (this._root.Exists)
      this._root.Delete(recursive: true);
  }

  private FileInfo CreateJpg(string relative) {
    var f = new FileInfo(Path.Combine(this._root.FullName, relative));
    f.Directory!.Create();
    File.WriteAllBytes(f.FullName, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
    f.Refresh();
    return f;
  }

  private sealed class StubReader : IMetadataReader {
    private readonly Dictionary<string, FullMetadata> _map = new(StringComparer.OrdinalIgnoreCase);
    public void Register(FileInfo f, FullMetadata m) => this._map[f.FullName] = m;
    public Task<FullMetadata> ReadAsync(FileInfo file, CancellationToken ct = default)
      => Task.FromResult(this._map.GetValueOrDefault(file.FullName, new FullMetadata()));
  }

  private static TaggedRegion Person(string name) => new(
    new NormalizedBoundingBox(0, 0, 0.1f, 0.1f),
    RegionCategory.Person,
    Label: name
  );

  [Test]
  public async Task ScanAsync_VisitsSupportedImagesOnly() {
    var reader = new StubReader();
    var cache = new MetadataCache(reader);
    var index = new LibraryIndex(cache, new SupportedFormatsService());
    this.CreateJpg("a.jpg");
    this.CreateJpg("b.jpg");
    this.CreateJpg("notes.txt");

    var count = await index.ScanAsync(this._root);

    Assert.That(count, Is.EqualTo(2));
  }

  [Test]
  public async Task Search_ByKeyword_ReturnsMatching() {
    var reader = new StubReader();
    var cache = new MetadataCache(reader);
    var index = new LibraryIndex(cache, new SupportedFormatsService());
    var a = this.CreateJpg("a.jpg");
    var b = this.CreateJpg("b.jpg");

    reader.Register(a, new FullMetadata { Keywords = ["vacation", "beach"] });
    reader.Register(b, new FullMetadata { Keywords = ["work"] });

    await index.ScanAsync(this._root);

    var hits = index.Search(new LibrarySearchQuery(Keyword: "beach"));

    Assert.Multiple(() => {
      Assert.That(hits, Has.Count.EqualTo(1));
      Assert.That(hits[0].File.Name, Is.EqualTo("a.jpg"));
    });
  }

  [Test]
  public async Task Search_ByPerson_LooksInsidePersonRegionsOnly() {
    var reader = new StubReader();
    var cache = new MetadataCache(reader);
    var index = new LibraryIndex(cache, new SupportedFormatsService());
    var a = this.CreateJpg("a.jpg");
    var b = this.CreateJpg("b.jpg");

    reader.Register(a, new FullMetadata { Regions = [Person("Alice")] });
    reader.Register(b, new FullMetadata {
      Regions = [new TaggedRegion(new NormalizedBoundingBox(0,0,0.1f,0.1f), RegionCategory.Animal, Label: "Alice the cat")]
    });

    await index.ScanAsync(this._root);

    var hits = index.Search(new LibrarySearchQuery(Person: "Alice"));

    Assert.Multiple(() => {
      Assert.That(hits, Has.Count.EqualTo(1));
      Assert.That(hits[0].File.Name, Is.EqualTo("a.jpg"));
    });
  }

  [Test]
  public async Task Search_ByLocation_MatchesAnyPlaceField() {
    var reader = new StubReader();
    var cache = new MetadataCache(reader);
    var index = new LibraryIndex(cache, new SupportedFormatsService());
    var a = this.CreateJpg("a.jpg");
    var b = this.CreateJpg("b.jpg");
    var c = this.CreateJpg("c.jpg");

    reader.Register(a, new FullMetadata { City = "Berlin" });
    reader.Register(b, new FullMetadata { Country = "Berlin, Germany" });
    reader.Register(c, new FullMetadata { Keywords = ["Berlin"] });  // keyword, NOT place

    await index.ScanAsync(this._root);

    var hits = index.Search(new LibrarySearchQuery(Location: "Berlin"));

    Assert.That(hits.Select(h => h.File.Name), Is.EquivalentTo(new[] { "a.jpg", "b.jpg" }));
  }

  [Test]
  public async Task Search_AnyText_SpansMultipleFields() {
    var reader = new StubReader();
    var cache = new MetadataCache(reader);
    var index = new LibraryIndex(cache, new SupportedFormatsService());
    var a = this.CreateJpg("a.jpg");
    var b = this.CreateJpg("b.jpg");

    reader.Register(a, new FullMetadata { Title = "Anniversary dinner" });
    reader.Register(b, new FullMetadata { Caption = "anniversary trip" });

    await index.ScanAsync(this._root);

    var hits = index.Search(new LibrarySearchQuery(AnyText: "anniversary"));

    Assert.That(hits, Has.Count.EqualTo(2));
  }

  [Test]
  public async Task Search_MinRating_FiltersInclusive() {
    var reader = new StubReader();
    var cache = new MetadataCache(reader);
    var index = new LibraryIndex(cache, new SupportedFormatsService());
    var a = this.CreateJpg("a.jpg");
    var b = this.CreateJpg("b.jpg");
    var c = this.CreateJpg("c.jpg");

    reader.Register(a, new FullMetadata { Rating = 5 });
    reader.Register(b, new FullMetadata { Rating = 3 });
    reader.Register(c, new FullMetadata { Rating = 1 });

    await index.ScanAsync(this._root);

    var hits = index.Search(new LibrarySearchQuery(MinRating: 3));

    Assert.That(hits.Select(h => h.File.Name), Is.EquivalentTo(new[] { "a.jpg", "b.jpg" }));
  }

  [Test]
  public async Task Distinct_People_Keywords_Locations() {
    var reader = new StubReader();
    var cache = new MetadataCache(reader);
    var index = new LibraryIndex(cache, new SupportedFormatsService());
    var a = this.CreateJpg("a.jpg");
    var b = this.CreateJpg("b.jpg");

    reader.Register(a, new FullMetadata {
      Regions = [Person("Alice"), Person("Bob")],
      Keywords = ["beach", "vacation"],
      City = "Paris", Country = "France"
    });
    reader.Register(b, new FullMetadata {
      Regions = [Person("Alice")],                // dup
      Keywords = ["beach"],                        // dup
      City = "Paris"                               // dup
    });

    await index.ScanAsync(this._root);

    Assert.Multiple(() => {
      Assert.That(index.DistinctPeople(), Is.EquivalentTo(new[] { "Alice", "Bob" }));
      Assert.That(index.DistinctKeywords(), Is.EquivalentTo(new[] { "beach", "vacation" }));
      Assert.That(index.DistinctLocations(), Is.EquivalentTo(new[] { "France", "Paris" }));
    });
  }

  [Test]
  public async Task Search_EmptyQuery_ReturnsEverything() {
    var reader = new StubReader();
    var cache = new MetadataCache(reader);
    var index = new LibraryIndex(cache, new SupportedFormatsService());
    this.CreateJpg("a.jpg");
    this.CreateJpg("b.jpg");

    await index.ScanAsync(this._root);

    var hits = index.Search(new LibrarySearchQuery());

    Assert.That(hits, Has.Count.EqualTo(2));
  }
}
