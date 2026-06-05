using Hawkynt.PhotoManager.Core.Geocoding;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Tests.Unit.Geocoding;

[TestFixture]
public class GeofenceBatchServiceTests {
  private DirectoryInfo _root = null!;

  [SetUp]
  public void SetUp() {
    this._root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-geofence-" + Guid.NewGuid().ToString("N")));
    this._root.Create();
  }

  [TearDown]
  public void TearDown() {
    try { this._root.Delete(recursive: true); } catch { /* best-effort */ }
  }

  private FileInfo Touch(string name) {
    var f = new FileInfo(Path.Combine(this._root.FullName, name));
    File.WriteAllBytes(f.FullName, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
    f.Refresh();
    return f;
  }

  private static MapBookmark BerlinHQ => new() {
    Name = "Berlin HQ",
    Latitude = 52.5200,
    Longitude = 13.4050,
    RadiusMeters = 5000,
    City = "Berlin",
    Country = "Germany",
    CountryCode = "DE"
  };

  [Test]
  public async Task DryRun_ReportsHitWithoutWriting() {
    var file = this.Touch("a.jpg");
    var reader = new StubReader();
    reader.Register(file, new FullMetadata { Gps = new GpsCoordinate(52.5163, 13.3777) });
    var writer = new RecordingWriter();
    var svc = new GeofenceBatchService(reader, writer);

    var outcomes = await svc.RunAsync(
      new[] { file },
      new[] { BerlinHQ },
      new GeofenceBatchService.Options(DryRun: true)
    );

    Assert.Multiple(() => {
      Assert.That(outcomes, Has.Count.EqualTo(1));
      Assert.That(outcomes[0].Matches, Has.Count.EqualTo(1));
      Assert.That(outcomes[0].WouldWrite, Is.True);
      Assert.That(writer.Applied, Is.Empty, "dry-run must not write");
    });
  }

  [Test]
  public async Task NotDryRun_WritesEdit() {
    var file = this.Touch("a.jpg");
    var reader = new StubReader();
    reader.Register(file, new FullMetadata { Gps = new GpsCoordinate(52.5163, 13.3777) });
    var writer = new RecordingWriter();
    var svc = new GeofenceBatchService(reader, writer);

    var outcomes = await svc.RunAsync(
      new[] { file },
      new[] { BerlinHQ },
      new GeofenceBatchService.Options(DryRun: false)
    );

    Assert.Multiple(() => {
      Assert.That(outcomes[0].WouldWrite, Is.True);
      Assert.That(writer.Applied, Has.Count.EqualTo(1));
      Assert.That(writer.Applied[0].edit.City.Value, Is.EqualTo("Berlin"));
    });
  }

  [Test]
  public async Task OnlyFillEmpty_PreservesExistingFields() {
    var file = this.Touch("a.jpg");
    var reader = new StubReader();
    reader.Register(file, new FullMetadata {
      Gps = new GpsCoordinate(52.5163, 13.3777),
      City = "Already-Set"
    });
    var writer = new RecordingWriter();
    var svc = new GeofenceBatchService(reader, writer);

    await svc.RunAsync(
      new[] { file },
      new[] { BerlinHQ },
      new GeofenceBatchService.Options(DryRun: false, OnlyFillEmpty: true)
    );

    Assert.That(writer.Applied, Has.Count.EqualTo(1));
    var edit = writer.Applied[0].edit;
    Assert.Multiple(() => {
      Assert.That(edit.City.HasValue, Is.False, "existing City must be preserved");
      Assert.That(edit.Country.HasValue, Is.True, "empty Country still gets filled");
      Assert.That(edit.Country.Value, Is.EqualTo("Germany"));
    });
  }

  [Test]
  public async Task GeofenceMiss_LeavesFileUntouched() {
    var file = this.Touch("a.jpg");
    var reader = new StubReader();
    // Coordinate ~1500 km from Berlin → no match.
    reader.Register(file, new FullMetadata { Gps = new GpsCoordinate(40.7128, -74.0060) });
    var writer = new RecordingWriter();
    var svc = new GeofenceBatchService(reader, writer);

    var outcomes = await svc.RunAsync(
      new[] { file },
      new[] { BerlinHQ },
      new GeofenceBatchService.Options(DryRun: false)
    );

    Assert.Multiple(() => {
      Assert.That(outcomes[0].Matches, Is.Empty);
      Assert.That(outcomes[0].WouldWrite, Is.False);
      Assert.That(writer.Applied, Is.Empty);
    });
  }

  [Test]
  public async Task NoGps_LeavesFileUntouched() {
    var file = this.Touch("a.jpg");
    var reader = new StubReader();
    reader.Register(file, new FullMetadata());
    var writer = new RecordingWriter();
    var svc = new GeofenceBatchService(reader, writer);

    var outcomes = await svc.RunAsync(
      new[] { file },
      new[] { BerlinHQ },
      new GeofenceBatchService.Options(DryRun: false)
    );

    Assert.That(outcomes[0].Matches, Is.Empty);
    Assert.That(writer.Applied, Is.Empty);
  }

  [Test]
  public void Cancellation_StopsTheBatch() {
    var files = Enumerable.Range(0, 50).Select(i => this.Touch($"f{i}.jpg")).ToList();
    var reader = new StubReader();
    foreach (var f in files)
      reader.Register(f, new FullMetadata { Gps = new GpsCoordinate(52.5163, 13.3777) });
    var writer = new RecordingWriter();
    var svc = new GeofenceBatchService(reader, writer);
    using var cts = new CancellationTokenSource();

    var progress = new SyncProgress<GeofenceBatchService.Progress>(p => {
      if (p.Processed >= 3)
        cts.Cancel();
    });

    Assert.ThrowsAsync<OperationCanceledException>(async () => await svc.RunAsync(
      files,
      new[] { BerlinHQ },
      new GeofenceBatchService.Options(DryRun: true),
      progress,
      cts.Token
    ));
  }

  // Progress<T> queues callbacks via SynchronizationContext, which doesn't
  // fire reliably inside NUnit's test executor. This synchronous variant
  // calls back inline so the cancellation trigger lands before the next
  // file runs.
  private sealed class SyncProgress<T>(Action<T> on) : IProgress<T> {
    public void Report(T value) => on(value);
  }

  [Test]
  public async Task DryRunMatchCount_ReportsCorrectly() {
    var f1 = this.Touch("a.jpg");
    var f2 = this.Touch("b.jpg");
    var f3 = this.Touch("c.jpg");
    var reader = new StubReader();
    reader.Register(f1, new FullMetadata { Gps = new GpsCoordinate(52.5163, 13.3777) }); // hit
    reader.Register(f2, new FullMetadata { Gps = new GpsCoordinate(40.7128, -74.0060) }); // miss
    reader.Register(f3, new FullMetadata()); // no GPS
    var writer = new RecordingWriter();
    var svc = new GeofenceBatchService(reader, writer);

    var outcomes = await svc.RunAsync(
      new[] { f1, f2, f3 },
      new[] { BerlinHQ },
      new GeofenceBatchService.Options(DryRun: true)
    );

    Assert.Multiple(() => {
      Assert.That(outcomes.Count(o => o.Matches.Count > 0), Is.EqualTo(1));
      Assert.That(outcomes.Count(o => o.WouldWrite), Is.EqualTo(1));
    });
  }

  private sealed class StubReader : IMetadataReader {
    private readonly Dictionary<string, FullMetadata> _map = new(StringComparer.OrdinalIgnoreCase);
    public void Register(FileInfo file, FullMetadata md) => this._map[file.FullName] = md;
    public Task<FullMetadata> ReadAsync(FileInfo file, CancellationToken ct = default)
      => Task.FromResult(this._map.GetValueOrDefault(file.FullName, new FullMetadata()));
  }

  private sealed class RecordingWriter : IMetadataWriter {
    public List<(FileInfo file, MetadataEdit edit)> Applied { get; } = new();
    public Task<FileInfo> ApplyAsync(FileInfo imageFile, MetadataEdit edit, CancellationToken ct = default) {
      this.Applied.Add((imageFile, edit));
      return Task.FromResult(imageFile);
    }
  }
}
