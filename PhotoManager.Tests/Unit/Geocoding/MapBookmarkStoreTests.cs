using PhotoManager.Core.Geocoding;

namespace PhotoManager.Tests.Unit.Geocoding;

[TestFixture]
public sealed class MapBookmarkStoreTests {
  private DirectoryInfo? _tempDir;

  [SetUp]
  public void SetUp() {
    this._tempDir = new DirectoryInfo(
      Path.Combine(Path.GetTempPath(), "pm-tests-bookmarks-" + Guid.NewGuid().ToString("N")));
    this._tempDir.Create();
  }

  [TearDown]
  public void TearDown() {
    if (this._tempDir is { Exists: true } dir)
      dir.Delete(recursive: true);
  }

  private FileInfo NewFile(string name = "bookmarks.json")
    => new(Path.Combine(this._tempDir!.FullName, name));

  [Test]
  public void Load_MissingFile_ReturnsEmpty() {
    var store = new MapBookmarkStore(this.NewFile());
    var result = store.Load();
    Assert.That(result, Is.Empty);
  }

  [Test]
  public void Save_ThenLoad_RoundTripsAllFields() {
    var file = this.NewFile();
    var store = new MapBookmarkStore(file);

    var original = new MapBookmark {
      Name = "Pizzeria Roma",
      Latitude = 41.9028,
      Longitude = 12.4964,
      RadiusMeters = 50,
      Location = "Via dei Fori Imperiali",
      City = "Rome",
      State = "Lazio",
      Country = "Italy",
      CountryCode = "IT"
    };
    store.Save(new[] { original });

    var fresh = new MapBookmarkStore(file).Load();
    Assert.That(fresh, Has.Count.EqualTo(1));
    var loaded = fresh[0];

    Assert.Multiple(() => {
      Assert.That(loaded.Id, Is.EqualTo(original.Id));
      Assert.That(loaded.Name, Is.EqualTo("Pizzeria Roma"));
      Assert.That(loaded.Latitude, Is.EqualTo(41.9028).Within(1e-9));
      Assert.That(loaded.Longitude, Is.EqualTo(12.4964).Within(1e-9));
      Assert.That(loaded.RadiusMeters, Is.EqualTo(50));
      Assert.That(loaded.Location, Is.EqualTo("Via dei Fori Imperiali"));
      Assert.That(loaded.City, Is.EqualTo("Rome"));
      Assert.That(loaded.State, Is.EqualTo("Lazio"));
      Assert.That(loaded.Country, Is.EqualTo("Italy"));
      Assert.That(loaded.CountryCode, Is.EqualTo("IT"));
    });
  }

  [Test]
  public void Save_OverwritesExistingContent() {
    var file = this.NewFile();
    var store = new MapBookmarkStore(file);

    store.Save(new[] {
      new MapBookmark { Name = "First", Latitude = 1, Longitude = 2 },
      new MapBookmark { Name = "Second", Latitude = 3, Longitude = 4 }
    });
    store.Save(new[] {
      new MapBookmark { Name = "Only", Latitude = 5, Longitude = 6 }
    });

    var loaded = store.Load();
    Assert.That(loaded, Has.Count.EqualTo(1));
    Assert.That(loaded[0].Name, Is.EqualTo("Only"));
  }

  [Test]
  public void Load_MalformedJson_ReturnsEmpty() {
    var file = this.NewFile();
    File.WriteAllText(file.FullName, "{ this is not valid json ][");

    var loaded = new MapBookmarkStore(file).Load();
    Assert.That(loaded, Is.Empty);
  }

  [Test]
  public void Load_EmptyFile_ReturnsEmpty() {
    var file = this.NewFile();
    File.WriteAllText(file.FullName, string.Empty);

    var loaded = new MapBookmarkStore(file).Load();
    Assert.That(loaded, Is.Empty);
  }

  [Test]
  public void Load_FiltersOutBookmarksWithoutNames() {
    var file = this.NewFile();
    File.WriteAllText(file.FullName,
      """[{"name":"Good","latitude":50,"longitude":10},{"name":"","latitude":51,"longitude":11},{"name":"   ","latitude":52,"longitude":12}]""");

    var loaded = new MapBookmarkStore(file).Load();
    Assert.That(loaded, Has.Count.EqualTo(1));
    Assert.That(loaded[0].Name, Is.EqualTo("Good"));
  }

  [Test]
  public void Load_FiltersOutImplausibleCoordinates() {
    var file = this.NewFile();
    File.WriteAllText(file.FullName,
      """[{"name":"OK","latitude":50,"longitude":10},{"name":"BadLat","latitude":99,"longitude":10},{"name":"BadLon","latitude":50,"longitude":-200}]""");

    var loaded = new MapBookmarkStore(file).Load();
    Assert.That(loaded.Select(b => b.Name), Is.EquivalentTo(new[] { "OK" }));
  }

  [Test]
  public void DefaultFile_LivesUnderAppDataPaths() {
    var defaultFile = MapBookmarkStore.DefaultFile();
    Assert.That(defaultFile.Name, Is.EqualTo("map-bookmarks.json"));
    Assert.That(defaultFile.Directory!.FullName, Is.EqualTo(PhotoManager.Core.AppDataPaths.Root().FullName));
  }
}
