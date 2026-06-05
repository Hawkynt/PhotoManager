using Hawkynt.PhotoManager.Core.Develop;
using Hawkynt.PhotoManager.Tests.Helpers;

namespace Hawkynt.PhotoManager.Tests.Unit.Develop;

[TestFixture]
public class DevelopHistoryTests {
  [Test]
  public void Push_OnEmpty_ProducesSingleEntry() {
    var settings = new DevelopSettings(ExposureStops: 0.5);
    var pushed = DevelopHistory.Push(Array.Empty<DevelopSnapshot>(), settings, label: "first");

    Assert.That(pushed, Has.Count.EqualTo(1));
    Assert.That(pushed[0].Label, Is.EqualTo("first"));
    Assert.That(pushed[0].Settings.ExposureStops, Is.EqualTo(0.5).Within(1e-9));
    Assert.That(pushed[0].TimestampUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
  }

  [Test]
  public void Push_PutsNewEntryAtTheFront() {
    var older = new DevelopSnapshot(DateTime.UtcNow.AddMinutes(-10), "older", new DevelopSettings());
    var pushed = DevelopHistory.Push(new[] { older }, new DevelopSettings(ContrastPercent: 25), "newer");

    Assert.That(pushed, Has.Count.EqualTo(2));
    Assert.That(pushed[0].Label, Is.EqualTo("newer"));
    Assert.That(pushed[1].Label, Is.EqualTo("older"));
  }

  [Test]
  public void Push_RespectsMaxDepth_DroppingOldestEntries() {
    var stack = (IReadOnlyList<DevelopSnapshot>)Array.Empty<DevelopSnapshot>();
    for (var i = 0; i < 55; i++)
      stack = DevelopHistory.Push(stack, new DevelopSettings(ExposureStops: i * 0.1), $"step {i}", maxDepth: 50);

    Assert.That(stack, Has.Count.EqualTo(50));
    Assert.That(stack[0].Label, Is.EqualTo("step 54"));
    Assert.That(stack[^1].Label, Is.EqualTo("step 5"));
  }

  [Test]
  public void Push_IdenticalSettingsTwice_StillCreatesTwoEntries() {
    var settings = new DevelopSettings(ExposureStops: 0.5);
    var first = DevelopHistory.Push(Array.Empty<DevelopSnapshot>(), settings, "a");
    Thread.Sleep(2);  // make sure the timestamps differ
    var second = DevelopHistory.Push(first, settings, "b");

    Assert.That(second, Has.Count.EqualTo(2));
    Assert.That(second[0].TimestampUtc, Is.GreaterThan(second[1].TimestampUtc));
  }

  [Test]
  public void Push_NullExisting_Throws() {
    Assert.Throws<ArgumentNullException>(() => DevelopHistory.Push(null!, new DevelopSettings()));
  }

  [Test]
  public void Push_NullSettings_Throws() {
    Assert.Throws<ArgumentNullException>(
      () => DevelopHistory.Push(Array.Empty<DevelopSnapshot>(), null!));
  }

  [Test]
  public void Push_InvalidMaxDepth_ClampsToOne() {
    var pushed = DevelopHistory.Push(Array.Empty<DevelopSnapshot>(), new DevelopSettings(ExposureStops: 1), "a", maxDepth: 0);
    Assert.That(pushed, Has.Count.EqualTo(1));
  }

  [Test]
  public void DefaultMaxDepth_Is50() {
    Assert.That(DevelopHistory.DefaultMaxDepth, Is.EqualTo(50));
  }

  [Test]
  public void Push_Max50Cap_OldestTrimmed() {
    var stack = (IReadOnlyList<DevelopSnapshot>)Array.Empty<DevelopSnapshot>();
    for (var i = 0; i < 60; i++)
      stack = DevelopHistory.Push(stack, new DevelopSettings(ExposureStops: i * 0.1), $"step {i}");

    Assert.That(stack, Has.Count.EqualTo(50));
    Assert.That(stack[0].Label, Is.EqualTo("step 59"));
    Assert.That(stack[^1].Label, Is.EqualTo("step 10"));
  }

  // ---------- GetAll ----------

  [Test]
  public void GetAll_ReturnsAllEntries_NewestFirst() {
    var s1 = new DevelopSnapshot(DateTime.UtcNow.AddMinutes(-2), "a", new DevelopSettings(ExposureStops: 0.5));
    var s2 = new DevelopSnapshot(DateTime.UtcNow.AddMinutes(-1), "b", new DevelopSettings(ExposureStops: 1.0));
    var stack = new List<DevelopSnapshot> { s2, s1 };

    var all = DevelopHistory.GetAll(stack);
    Assert.That(all, Has.Count.EqualTo(2));
    Assert.That(all[0].Label, Is.EqualTo("b"));
    Assert.That(all[1].Label, Is.EqualTo("a"));
  }

  [Test]
  public void GetAll_EmptyHistory_ReturnsEmptyList() {
    var all = DevelopHistory.GetAll(Array.Empty<DevelopSnapshot>());
    Assert.That(all, Is.Empty);
  }

  [Test]
  public void GetAll_NullThrows() {
    Assert.Throws<ArgumentNullException>(() => DevelopHistory.GetAll(null!));
  }

  // ---------- RollbackTo ----------

  [Test]
  public void RollbackTo_ReturnsCorrectSettings() {
    var settings0 = new DevelopSettings(ExposureStops: 0.5);
    var settings1 = new DevelopSettings(ContrastPercent: 25);
    var settings2 = new DevelopSettings(SaturationPercent: -30);
    var stack = new List<DevelopSnapshot> {
      new(DateTime.UtcNow, "latest", settings0),
      new(DateTime.UtcNow.AddMinutes(-1), "mid", settings1),
      new(DateTime.UtcNow.AddMinutes(-2), "oldest", settings2)
    };

    Assert.That(DevelopHistory.RollbackTo(stack, 0).ExposureStops, Is.EqualTo(0.5).Within(1e-9));
    Assert.That(DevelopHistory.RollbackTo(stack, 1).ContrastPercent, Is.EqualTo(25).Within(1e-9));
    Assert.That(DevelopHistory.RollbackTo(stack, 2).SaturationPercent, Is.EqualTo(-30).Within(1e-9));
  }

  [Test]
  public void RollbackTo_NegativeIndex_Throws() {
    var stack = DevelopHistory.Push(Array.Empty<DevelopSnapshot>(), new DevelopSettings(), "a");
    Assert.Throws<ArgumentOutOfRangeException>(() => DevelopHistory.RollbackTo(stack, -1));
  }

  [Test]
  public void RollbackTo_IndexOutOfRange_Throws() {
    var stack = DevelopHistory.Push(Array.Empty<DevelopSnapshot>(), new DevelopSettings(), "a");
    Assert.Throws<ArgumentOutOfRangeException>(() => DevelopHistory.RollbackTo(stack, 1));
  }

  [Test]
  public void RollbackTo_EmptyHistory_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(
      () => DevelopHistory.RollbackTo(Array.Empty<DevelopSnapshot>(), 0));
  }

  [Test]
  public void RollbackTo_NullThrows() {
    Assert.Throws<ArgumentNullException>(() => DevelopHistory.RollbackTo(null!, 0));
  }

  // ---------- Push -> GetAll round-trip ----------

  [Test]
  public void Push_Then_GetAll_RoundTrips() {
    var stack = (IReadOnlyList<DevelopSnapshot>)Array.Empty<DevelopSnapshot>();
    stack = DevelopHistory.Push(stack, new DevelopSettings(ExposureStops: 0.5), "first");
    stack = DevelopHistory.Push(stack, new DevelopSettings(ContrastPercent: 10), "second");

    var all = DevelopHistory.GetAll(stack);
    Assert.That(all, Has.Count.EqualTo(2));
    Assert.That(all[0].Label, Is.EqualTo("second"));
    Assert.That(all[1].Label, Is.EqualTo("first"));
    Assert.That(all[0].Settings.ContrastPercent, Is.EqualTo(10).Within(1e-9));
    Assert.That(all[1].Settings.ExposureStops, Is.EqualTo(0.5).Within(1e-9));
  }

  // ---------- JSON serialization round-trip ----------

  [Test]
  public void SerializationRoundTrip_ViaJsonPreservesData() {
    var settings = new DevelopSettings(
      ExposureStops: 1.5,
      ContrastPercent: -20,
      SaturationPercent: 50,
      TemperatureShift: 15,
      SharpeningAmount: 40);

    var stack = (IReadOnlyList<DevelopSnapshot>)Array.Empty<DevelopSnapshot>();
    stack = DevelopHistory.Push(stack, settings, "test label");

    var json = System.Text.Json.JsonSerializer.Serialize(stack);
    var deserialized = System.Text.Json.JsonSerializer.Deserialize<List<DevelopSnapshot>>(json)!;

    Assert.That(deserialized, Has.Count.EqualTo(1));
    Assert.That(deserialized[0].Label, Is.EqualTo("test label"));
    Assert.That(deserialized[0].Settings.ExposureStops, Is.EqualTo(1.5).Within(1e-9));
    Assert.That(deserialized[0].Settings.ContrastPercent, Is.EqualTo(-20).Within(1e-9));
    Assert.That(deserialized[0].Settings.SaturationPercent, Is.EqualTo(50).Within(1e-9));
    Assert.That(deserialized[0].Settings.TemperatureShift, Is.EqualTo(15).Within(1e-9));
    Assert.That(deserialized[0].Settings.SharpeningAmount, Is.EqualTo(40).Within(1e-9));
  }

  // ---------- XMP persistence ----------

  [Test]
  public async Task SaveAsync_WithSnapshotLabel_PushesPriorSettingsOntoXmpHistory() {
    using var temp = new TempDirectory();
    var file = temp.NewJpeg("photo.jpg");

    // Round 1 -- silent autosave (no label) writes the first settings without history.
    var first = new DevelopSettings(ExposureStops: 0.5, ContrastPercent: 10);
    Assert.That(await DevelopMetadataStore.SaveAsync(file, first, copyIndex: 0, snapshotLabel: null), Is.True);
    Assert.That(await DevelopMetadataStore.LoadHistoryAsync(file), Is.Empty);

    // Round 2 -- labelled save snapshots the prior settings before overwriting.
    var second = new DevelopSettings(ExposureStops: 1.0, ContrastPercent: 25);
    Assert.That(await DevelopMetadataStore.SaveAsync(file, second, copyIndex: 0, snapshotLabel: "before bump"), Is.True);

    var history = await DevelopMetadataStore.LoadHistoryAsync(file);
    Assert.That(history, Has.Count.EqualTo(1));
    Assert.That(history[0].Label, Is.EqualTo("before bump"));
    Assert.That(history[0].Settings.ExposureStops, Is.EqualTo(0.5).Within(0.01));
    Assert.That(history[0].Settings.ContrastPercent, Is.EqualTo(10));

    var loaded = await DevelopMetadataStore.LoadAsync(file);
    Assert.That(loaded, Is.Not.Null);
    Assert.That(loaded!.ExposureStops, Is.EqualTo(1.0).Within(0.01));
  }

  [Test]
  public async Task LoadHistory_RoundTripsTimestampsLabelsAndSettings() {
    using var temp = new TempDirectory();
    var file = temp.NewJpeg("photo.jpg");
    Assert.That(await DevelopMetadataStore.SaveAsync(file, new DevelopSettings(ExposureStops: 0.25),
      copyIndex: 0, snapshotLabel: null), Is.True);
    Assert.That(await DevelopMetadataStore.SaveAsync(file, new DevelopSettings(ExposureStops: 1.5, ContrastPercent: 20),
      copyIndex: 0, snapshotLabel: "step A"), Is.True);

    var history = await DevelopMetadataStore.LoadHistoryAsync(file);
    Assert.That(history, Has.Count.EqualTo(1));
    var entry = history[0];
    Assert.That(entry.Label, Is.EqualTo("step A"));
    Assert.That(entry.TimestampUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
    Assert.That(entry.TimestampUtc, Is.LessThanOrEqualTo(DateTime.UtcNow.AddSeconds(2)));
    Assert.That(entry.Settings.ExposureStops, Is.EqualTo(0.25).Within(0.01));
  }

  [Test]
  public async Task SaveAsync_SnapshotStackIsCappedAcrossManySaves() {
    using var temp = new TempDirectory();
    var file = temp.NewJpeg("photo.jpg");
    // The very first save has no prior settings to snapshot, so produce 22
    // labelled saves expecting the XMP cap (20) to evict the first two pushed.
    // The XMP persistence layer uses maxDepth: 20 to stay within the ~64 KB
    // APP1 segment budget, even though DevelopHistory.DefaultMaxDepth is 50.
    for (var i = 0; i < 22; i++)
      await DevelopMetadataStore.SaveAsync(file,
        new DevelopSettings(ExposureStops: i * 0.1),
        copyIndex: 0, snapshotLabel: $"step {i}");

    var history = await DevelopMetadataStore.LoadHistoryAsync(file);
    Assert.That(history.Count, Is.LessThanOrEqualTo(20));
    Assert.That(history[0].Label, Is.EqualTo("step 21"));
  }

  /// <summary>Helper for a self-cleaning temp dir.</summary>
  private sealed class TempDirectory : IDisposable {
    public DirectoryInfo Dir { get; }
    public TempDirectory() {
      this.Dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(),
        "PhotoManagerHistoryTests_" + Guid.NewGuid().ToString("N")));
      this.Dir.Create();
    }
    public FileInfo NewJpeg(string name) {
      var path = Path.Combine(this.Dir.FullName, name);
      TestJpegFactory.Write(path, exifSubIfdDateTimeOriginal: new DateTime(2024, 1, 1, 12, 0, 0));
      return new FileInfo(path);
    }
    public void Dispose() {
      try { this.Dir.Delete(recursive: true); } catch { /* best effort */ }
    }
  }
}
