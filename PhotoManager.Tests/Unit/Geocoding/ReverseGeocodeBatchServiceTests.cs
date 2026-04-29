using PhotoManager.Core.Geocoding;
using PhotoManager.Core.Metadata;

namespace PhotoManager.Tests.Unit.Geocoding;

[TestFixture]
public sealed class ReverseGeocodeBatchServiceTests {
  // The batch service only ever calls FileInfo.* through the reader/writer
  // doubles, so we don't need real files on disk — same fake path is reused.
  private static FileInfo NewFakeFile(string name) =>
    new(Path.Combine(Path.GetTempPath(), name));

  [Test]
  public async Task RunAsync_DryRun_DoesNotInvokeWriter() {
    var file = NewFakeFile("dry.jpg");
    var reader = new FakeMetadataReader {
      [file.FullName] = new FullMetadata { Gps = new GpsCoordinate(48, 2) }
    };
    var geocoder = new FakeGeocoder {
      Result = new GeocodingResult("Avenue", "Paris", "Île-de-France", "France", "FR")
    };
    var writer = new FakeMetadataWriter();

    var service = new ReverseGeocodeBatchService(geocoder, reader, writer);
    var results = await service.RunAsync(
      new[] { file },
      new ReverseGeocodeBatchOptions { DryRun = true });

    Assert.That(results, Has.Count.EqualTo(1));
    Assert.That(results[0].Outcome, Is.EqualTo(ReverseGeocodeBatchOutcome.Resolved));
    Assert.That(writer.WriteCount, Is.Zero, "dry run must not write");
  }

  [Test]
  public async Task RunAsync_NoGps_SkipsWithoutCallingGeocoder() {
    var file = NewFakeFile("nogps.jpg");
    var reader = new FakeMetadataReader { [file.FullName] = new FullMetadata { Gps = null } };
    var geocoder = new FakeGeocoder { Result = null };
    var writer = new FakeMetadataWriter();

    var service = new ReverseGeocodeBatchService(geocoder, reader, writer);
    var results = await service.RunAsync(new[] { file });

    Assert.That(results[0].Outcome, Is.EqualTo(ReverseGeocodeBatchOutcome.NoGps));
    Assert.That(geocoder.CallCount, Is.Zero);
    Assert.That(writer.WriteCount, Is.Zero);
  }

  [Test]
  public async Task RunAsync_NoMatchFromGeocoder_RecordsSkip() {
    var file = NewFakeFile("nomatch.jpg");
    var reader = new FakeMetadataReader {
      [file.FullName] = new FullMetadata { Gps = new GpsCoordinate(0, 0) }
    };
    var geocoder = new FakeGeocoder { Result = null };
    var writer = new FakeMetadataWriter();

    var service = new ReverseGeocodeBatchService(geocoder, reader, writer);
    var results = await service.RunAsync(new[] { file });

    Assert.That(results[0].Outcome, Is.EqualTo(ReverseGeocodeBatchOutcome.NoMatch));
    Assert.That(writer.WriteCount, Is.Zero);
  }

  [Test]
  public async Task RunAsync_OnlyFillEmptyFields_SkipsWhenAllFieldsAlreadySet() {
    var file = NewFakeFile("alreadytagged.jpg");
    var reader = new FakeMetadataReader {
      [file.FullName] = new FullMetadata {
        Gps = new GpsCoordinate(48, 2),
        Location = "Existing", City = "Paris", State = "X", Country = "France", CountryCode = "FR"
      }
    };
    var geocoder = new FakeGeocoder {
      Result = new GeocodingResult("Other", "Paris", "X", "France", "FR")
    };
    var writer = new FakeMetadataWriter();

    var service = new ReverseGeocodeBatchService(geocoder, reader, writer);
    var results = await service.RunAsync(
      new[] { file },
      new ReverseGeocodeBatchOptions { OnlyFillEmptyFields = true });

    Assert.That(results[0].Outcome, Is.EqualTo(ReverseGeocodeBatchOutcome.AlreadyTagged));
    Assert.That(writer.WriteCount, Is.Zero);
  }

  [Test]
  public async Task RunAsync_OnlyFillEmptyFields_FillsHolesAndKeepsExisting() {
    var file = NewFakeFile("holes.jpg");
    var reader = new FakeMetadataReader {
      [file.FullName] = new FullMetadata {
        Gps = new GpsCoordinate(48, 2),
        City = "Paris"   // already set; should NOT be overwritten
      }
    };
    var geocoder = new FakeGeocoder {
      Result = new GeocodingResult("New Location", "OtherCity", "Île-de-France", "France", "FR")
    };
    var writer = new FakeMetadataWriter();

    var service = new ReverseGeocodeBatchService(geocoder, reader, writer);
    var results = await service.RunAsync(
      new[] { file },
      new ReverseGeocodeBatchOptions { OnlyFillEmptyFields = true });

    Assert.That(results[0].Outcome, Is.EqualTo(ReverseGeocodeBatchOutcome.Resolved));
    Assert.That(writer.WriteCount, Is.EqualTo(1));

    var edit = writer.LastEdit!;
    Assert.Multiple(() => {
      Assert.That(edit.Location.HasValue, Is.True, "Location was empty so should be patched");
      Assert.That(edit.Location.Value, Is.EqualTo("New Location"));
      Assert.That(edit.City.HasValue, Is.False, "existing City must not be stomped");
      Assert.That(edit.State.HasValue, Is.True);
      Assert.That(edit.Country.HasValue, Is.True);
      Assert.That(edit.CountryCode.HasValue, Is.True);
    });
  }

  [Test]
  public async Task RunAsync_OverwriteMode_PatchesAllFields() {
    var file = NewFakeFile("overwrite.jpg");
    var reader = new FakeMetadataReader {
      [file.FullName] = new FullMetadata {
        Gps = new GpsCoordinate(48, 2),
        City = "OldCity"
      }
    };
    var geocoder = new FakeGeocoder {
      Result = new GeocodingResult("NewLoc", "NewCity", "NewState", "NewCountry", "NC")
    };
    var writer = new FakeMetadataWriter();

    var service = new ReverseGeocodeBatchService(geocoder, reader, writer);
    var results = await service.RunAsync(
      new[] { file },
      new ReverseGeocodeBatchOptions { OnlyFillEmptyFields = false });

    Assert.That(results[0].Outcome, Is.EqualTo(ReverseGeocodeBatchOutcome.Resolved));
    var edit = writer.LastEdit!;
    Assert.Multiple(() => {
      Assert.That(edit.City.HasValue, Is.True);
      Assert.That(edit.City.Value, Is.EqualTo("NewCity"));
      Assert.That(edit.Country.Value, Is.EqualTo("NewCountry"));
    });
  }

  [Test]
  public async Task RunAsync_GeocoderThrows_RecordsErrorAndContinues() {
    var ok = NewFakeFile("ok.jpg");
    var bad = NewFakeFile("bad.jpg");
    var reader = new FakeMetadataReader {
      [ok.FullName] = new FullMetadata { Gps = new GpsCoordinate(48, 2) },
      [bad.FullName] = new FullMetadata { Gps = new GpsCoordinate(50, 10) }
    };
    var geocoder = new ThrowingGeocoder(throwOnFullName: bad.FullName);
    var writer = new FakeMetadataWriter();

    // Order: ok first then bad; the second should fail without affecting the first.
    var service = new ReverseGeocodeBatchService(geocoder, reader, writer);
    var results = await service.RunAsync(new[] { ok, bad });

    Assert.That(results, Has.Count.EqualTo(2));
    Assert.That(results[0].Outcome, Is.EqualTo(ReverseGeocodeBatchOutcome.Resolved));
    Assert.That(results[1].Outcome, Is.EqualTo(ReverseGeocodeBatchOutcome.Error));
    Assert.That(results[1].ErrorMessage, Is.Not.Null.And.Contains("kaboom"));
  }

  [Test]
  public void RunAsync_Cancelled_ThrowsOperationCanceled() {
    var file = NewFakeFile("anything.jpg");
    var reader = new FakeMetadataReader {
      [file.FullName] = new FullMetadata { Gps = new GpsCoordinate(48, 2) }
    };
    var geocoder = new FakeGeocoder { Result = new GeocodingResult(null, "X", null, null, null) };
    var writer = new FakeMetadataWriter();

    var service = new ReverseGeocodeBatchService(geocoder, reader, writer);
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    Assert.ThrowsAsync<OperationCanceledException>(
      () => service.RunAsync(new[] { file }, cancellationToken: cts.Token));
  }

  // ---- Test doubles ----

  private sealed class FakeGeocoder : IReverseGeocoder {
    public GeocodingResult? Result { get; set; }
    public int CallCount { get; private set; }
    public Task<GeocodingResult?> ResolveAsync(GpsCoordinate coordinate, CancellationToken cancellationToken = default) {
      this.CallCount++;
      return Task.FromResult(this.Result);
    }
  }

  private sealed class ThrowingGeocoder(string throwOnFullName) : IReverseGeocoder {
    private readonly string _throwOnFullName = throwOnFullName;
    public Task<GeocodingResult?> ResolveAsync(GpsCoordinate coordinate, CancellationToken cancellationToken = default) {
      if (coordinate.Latitude == 50)
        throw new InvalidOperationException("kaboom");
      return Task.FromResult<GeocodingResult?>(new GeocodingResult("Loc", "City", "State", "Country", "CC"));
    }
  }

  private sealed class FakeMetadataReader : IMetadataReader {
    private readonly Dictionary<string, FullMetadata> _byPath = new(StringComparer.OrdinalIgnoreCase);
    public FullMetadata this[string path] {
      set => this._byPath[path] = value;
    }
    public Task<FullMetadata> ReadAsync(FileInfo imageFile, CancellationToken cancellationToken = default) =>
      Task.FromResult(this._byPath.TryGetValue(imageFile.FullName, out var md) ? md : new FullMetadata());
  }

  private sealed class FakeMetadataWriter : IMetadataWriter {
    public int WriteCount { get; private set; }
    public MetadataEdit? LastEdit { get; private set; }
    public FileInfo? LastFile { get; private set; }
    public Task<FileInfo> ApplyAsync(FileInfo imageFile, MetadataEdit edit, CancellationToken cancellationToken = default) {
      this.WriteCount++;
      this.LastFile = imageFile;
      this.LastEdit = edit;
      return Task.FromResult(imageFile);
    }
  }
}
