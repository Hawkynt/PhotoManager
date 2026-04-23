using PhotoManager.Core.Detection;
using PhotoManager.Core.Faces;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Regions;

namespace PhotoManager.Tests.Unit.Faces;

[TestFixture]
public class FaceRecognitionServiceTests {
  private DirectoryInfo _workingDir = null!;
  private FileInfo _registryFile = null!;

  [SetUp]
  public void Setup() {
    this._workingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-faces-" + Guid.NewGuid().ToString("N")));
    this._workingDir.Create();
    this._registryFile = new FileInfo(Path.Combine(this._workingDir.FullName, "people.json"));
  }

  [TearDown]
  public void Teardown() {
    if (this._workingDir.Exists)
      this._workingDir.Delete(recursive: true);
  }

  private FileInfo CreateFakeImage(string name = "photo.jpg") {
    var file = new FileInfo(Path.Combine(this._workingDir.FullName, name));
    File.WriteAllBytes(file.FullName, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
    file.Refresh();
    return file;
  }

  [Test]
  public void MergeRegions_NoOverlap_AppendsCandidate() {
    var existing = new[] {
      new FaceRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), "Alice")
    };
    var detected = new[] {
      new FaceRegion(new NormalizedBoundingBox(0.5f, 0.5f, 0.1f, 0.1f))
    };

    var merged = FaceRecognitionService.MergeRegions(existing, detected, iouThreshold: 0.3f);

    Assert.That(merged, Has.Count.EqualTo(2));
  }

  [Test]
  public void MergeRegions_HeavyOverlap_ExistingNameWins() {
    var existing = new[] {
      new FaceRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), "Alice")
    };
    var detected = new[] {
      new FaceRegion(new NormalizedBoundingBox(0.005f, 0.005f, 0.1f, 0.1f), "Bob")
    };

    var merged = FaceRecognitionService.MergeRegions(existing, detected, iouThreshold: 0.3f);

    Assert.Multiple(() => {
      Assert.That(merged, Has.Count.EqualTo(1));
      Assert.That(merged[0].PersonName, Is.EqualTo("Alice"), "user-tagged name must not be overwritten");
    });
  }

  [Test]
  public void MergeRegions_OverlapWithUnnamedExisting_AcceptsDetectedName() {
    var existing = new[] {
      new FaceRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f))
    };
    var detected = new[] {
      new FaceRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), "Carol")
    };

    var merged = FaceRecognitionService.MergeRegions(existing, detected, iouThreshold: 0.5f);

    Assert.Multiple(() => {
      Assert.That(merged, Has.Count.EqualTo(1));
      Assert.That(merged[0].PersonName, Is.EqualTo("Carol"));
    });
  }

  [Test]
  public async Task TagFaceAsync_UpdatesRegionAndKeywords() {
    var file = this.CreateFakeImage();
    var writer = new XmpSidecarWriter();
    var reader = new MetadataReader();

    // Seed a face region without a name
    await writer.ApplyAsync(file, new MetadataEdit {
      Regions = Optional<IReadOnlyList<TaggedRegion>>.Set(new[] {
        new TaggedRegion(new NormalizedBoundingBox(0.3f, 0.3f, 0.1f, 0.15f), RegionCategory.Person)
      })
    });

    var service = new FaceRecognitionService(
      new NullFaceDetector(), reader, writer,
      new PeopleRegistry(this._registryFile)
    );

    await service.TagFaceAsync(file, faceIndex: 0, name: "Alice");

    var final = await reader.ReadAsync(file);

    Assert.Multiple(() => {
      Assert.That(final.Faces, Has.Count.EqualTo(1));
      Assert.That(final.Faces[0].PersonName, Is.EqualTo("Alice"));
      Assert.That(final.Keywords, Does.Contain("Alice"));
    });
  }

  [Test]
  public async Task TagFaceAsync_WithEmbedding_RegistersIt() {
    var file = this.CreateFakeImage();
    var writer = new XmpSidecarWriter();
    var reader = new MetadataReader();

    await writer.ApplyAsync(file, new MetadataEdit {
      Regions = Optional<IReadOnlyList<TaggedRegion>>.Set(new[] {
        new TaggedRegion(new NormalizedBoundingBox(0.4f, 0.4f, 0.1f, 0.1f), RegionCategory.Person)
      })
    });

    var registry = new PeopleRegistry(this._registryFile);
    var service = new FaceRecognitionService(new NullFaceDetector(), reader, writer, registry);

    var embedding = new[] { 0.2f, 0.8f, 0.1f };
    await service.TagFaceAsync(file, 0, "Alice", embedding);

    Assert.That(registry.FindMatch(embedding), Is.EqualTo("Alice"));
  }

  [Test]
  public void ContainsSmaller_BodyAroundFace_True() {
    var body = new NormalizedBoundingBox(0.1f, 0.1f, 0.6f, 0.8f);
    var face = new NormalizedBoundingBox(0.3f, 0.15f, 0.15f, 0.2f);  // ~6% of body area
    Assert.That(FaceRecognitionService.ContainsSmaller(body, face, areaRatio: 0.4f), Is.True);
  }

  [Test]
  public void ContainsSmaller_SideBySideRegions_False() {
    var a = new NormalizedBoundingBox(0.1f, 0.1f, 0.3f, 0.3f);
    var b = new NormalizedBoundingBox(0.5f, 0.1f, 0.3f, 0.3f);
    Assert.That(FaceRecognitionService.ContainsSmaller(a, b, areaRatio: 0.4f), Is.False);
  }

  [Test]
  public void ContainsSmaller_SameSizeOverlap_RejectsAsNotSmaller() {
    // Same box — neither is "smaller" so neither should be suppressed.
    var r = new NormalizedBoundingBox(0.1f, 0.1f, 0.3f, 0.3f);
    Assert.That(FaceRecognitionService.ContainsSmaller(r, r, areaRatio: 0.4f), Is.False);
  }

  /// <summary>
  /// End-to-end assertion: any face produced by the detector gets checked
  /// against the <see cref="PeopleRegistry"/>, and when the cosine match
  /// clears the threshold the returned region is named accordingly. This
  /// is the "propose the right person when a good match is found" contract.
  /// </summary>
  [Test]
  public async Task DetectAndWrite_WithRegistryMatch_LabelsDetectedFaceWithKnownName() {
    var file = this.CreateFakeImage();
    var writer = new XmpSidecarWriter();
    var reader = new MetadataReader();

    // Seed the registry with a known person.
    var registry = new PeopleRegistry(this._registryFile);
    var aliceReference = new[] { 1.0f, 0.0f, 0.0f };
    registry.AddReference("Alice", aliceReference);

    // Detector returns one face with an embedding that is cosine-close to Alice.
    var detector = new StubDetectorWithEmbedding(
      new NormalizedBoundingBox(0.3f, 0.3f, 0.2f, 0.2f),
      embedding: new[] { 0.99f, 0.1f, 0.0f }
    );

    var service = new FaceRecognitionService(detector, reader, writer, registry);
    var result = await service.DetectAndWriteAsync(file);

    Assert.Multiple(() => {
      Assert.That(result, Has.Count.EqualTo(1));
      Assert.That(result[0].PersonName, Is.EqualTo("Alice"),
        "registry auto-match should assign the known name");
    });

    // Region lands in the sidecar as a PROPOSED match with Alice's name
    // pre-filled — the user still has to click Accept (or Tag) before the
    // name is promoted to keywords. This matches the user workflow:
    // "every face needs to be accepted before stored".
    var final = await reader.ReadAsync(file);
    var personRegion = final.Regions.Single(r => r.Category == RegionCategory.Person);
    Assert.Multiple(() => {
      Assert.That(personRegion.Label, Is.EqualTo("Alice"));
      Assert.That(personRegion.Status, Is.EqualTo(RegionStatus.Proposed),
        "detection produces proposals, not accepted regions");
      Assert.That(final.Keywords, Does.Not.Contain("Alice"),
        "keyword promotion waits for user-Accept");
    });

    // Now user accepts → name becomes a keyword.
    var regionService = new RegionService(reader, writer);
    await regionService.AcceptAsync(file, final.Regions.ToList().IndexOf(personRegion));

    var afterAccept = await reader.ReadAsync(file);
    Assert.Multiple(() => {
      Assert.That(afterAccept.Regions.Single(r => r.Category == RegionCategory.Person).Status,
        Is.EqualTo(RegionStatus.Accepted));
      Assert.That(afterAccept.Keywords, Does.Contain("Alice"));
    });
  }

  [Test]
  public async Task DetectAndWrite_NoRegistryMatch_LeavesFaceUnnamedAndProposed() {
    var file = this.CreateFakeImage();
    var writer = new XmpSidecarWriter();
    var reader = new MetadataReader();

    var registry = new PeopleRegistry(this._registryFile);
    registry.AddReference("Alice", new[] { 1.0f, 0.0f, 0.0f });

    // Embedding points away from Alice — cosine similarity ~0.
    var detector = new StubDetectorWithEmbedding(
      new NormalizedBoundingBox(0.4f, 0.4f, 0.2f, 0.2f),
      embedding: new[] { 0.0f, 1.0f, 0.0f }
    );

    var service = new FaceRecognitionService(detector, reader, writer, registry);
    await service.DetectAndWriteAsync(file);

    var final = await reader.ReadAsync(file);
    // The stored region should be unnamed AND Proposed so the user can
    // review + tag it (auto-match didn't hit the threshold).
    var personRegion = final.Regions.Single(r => r.Category == RegionCategory.Person);
    Assert.Multiple(() => {
      Assert.That(personRegion.Label, Is.Null.Or.Empty);
      Assert.That(personRegion.Status, Is.EqualTo(RegionStatus.Proposed),
        "unnamed face detections should come in as Proposed so the user reviews them");
    });
  }

  private sealed class StubDetectorWithEmbedding : IFaceDetector {
    private readonly NormalizedBoundingBox _box;
    private readonly float[]? _embedding;
    public StubDetectorWithEmbedding(NormalizedBoundingBox box, float[]? embedding = null) {
      this._box = box;
      this._embedding = embedding;
    }
    public Task<IReadOnlyList<DetectedFace>> DetectAsync(FileInfo imageFile, CancellationToken cancellationToken = default) {
      IReadOnlyList<DetectedFace> result = new[] {
        new DetectedFace(new FaceRegion(this._box), Confidence: 0.9f, Embedding: this._embedding)
      };
      return Task.FromResult(result);
    }
  }

  [Test]
  public void TagFaceAsync_IndexOutOfRange_Throws() {
    var file = this.CreateFakeImage();
    var service = new FaceRecognitionService(
      new NullFaceDetector(), new MetadataReader(), new XmpSidecarWriter(),
      new PeopleRegistry(this._registryFile)
    );

    Assert.ThrowsAsync<ArgumentOutOfRangeException>(
      async () => await service.TagFaceAsync(file, faceIndex: 5, name: "Nobody")
    );
  }
}
