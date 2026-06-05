using Hawkynt.PhotoManager.Core.Detection;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Tests.Unit.Detection;

[TestFixture]
public class DetectionServiceTests {
  [Test]
  public void MergeKeywords_NoOverlap_ConcatenatesInOrder() {
    var merged = DetectionService.MergeKeywords(
      existing: new[] { "user-tag1", "user-tag2" },
      detected: new[] { "cat", "dog" }
    );

    Assert.That(merged, Is.EqualTo(new[] { "user-tag1", "user-tag2", "cat", "dog" }).AsCollection);
  }

  [Test]
  public void MergeKeywords_CaseInsensitiveDedupe_PrefersExistingCasing() {
    var merged = DetectionService.MergeKeywords(
      existing: new[] { "Beach", "Vacation" },
      detected: new[] { "beach", "rome" }
    );

    Assert.Multiple(() => {
      Assert.That(merged.Count, Is.EqualTo(3));
      Assert.That(merged, Does.Contain("Beach"),    "existing casing should be preserved");
      Assert.That(merged, Does.Contain("Vacation"));
      Assert.That(merged, Does.Contain("rome"));
      Assert.That(merged, Does.Not.Contain("beach"));
    });
  }

  [Test]
  public void MergeKeywords_DedupesWithinDetected() {
    var merged = DetectionService.MergeKeywords(
      existing: Array.Empty<string>(),
      detected: new[] { "dog", "dog", "DOG", "cat" }
    );

    Assert.That(merged.Count, Is.EqualTo(2));
  }

  [Test]
  public void MergeKeywords_SkipsBlanks() {
    var merged = DetectionService.MergeKeywords(
      existing: new[] { "", "  ", "real" },
      detected: new[] { "", "auto" }
    );

    Assert.That(merged, Is.EqualTo(new[] { "real", "auto" }).AsCollection);
  }

  [Test]
  public async Task DetectAndWriteKeywordsAsync_EmptyDetector_LeavesSidecarAlone() {
    var workingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-det-" + Guid.NewGuid().ToString("N")));
    workingDir.Create();
    try {
      var file = new FileInfo(Path.Combine(workingDir.FullName, "photo.jpg"));
      await File.WriteAllBytesAsync(file.FullName, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

      var detector = new EmptyDetector();
      var service = new DetectionService(detector, new MetadataReader(), new XmpSidecarWriter());

      await service.DetectAndWriteKeywordsAsync(file);

      var sidecar = SidecarPath.For(file);
      Assert.That(sidecar.Exists, Is.False,
        "no detections → no sidecar should be created from scratch");
    } finally {
      workingDir.Delete(recursive: true);
    }
  }

  [Test]
  public async Task DetectAndWriteKeywordsAsync_MergesDetectionsWithExistingKeywords() {
    var workingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-det2-" + Guid.NewGuid().ToString("N")));
    workingDir.Create();
    try {
      var file = new FileInfo(Path.Combine(workingDir.FullName, "holiday.jpg"));
      await File.WriteAllBytesAsync(file.FullName, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

      var writer = new XmpSidecarWriter();
      var reader = new MetadataReader();

      // Seed an existing user-supplied keyword
      await writer.ApplyAsync(file, new MetadataEdit {
        Keywords = Optional<IReadOnlyList<string>>.Set(new[] { "user-tag" })
      });

      var detector = new StaticDetector("beach", "sunset");
      var service = new DetectionService(detector, reader, writer);

      var result = await service.DetectAndWriteKeywordsAsync(file);

      var final = await reader.ReadAsync(file);

      Assert.Multiple(() => {
        Assert.That(result.Labels.Count, Is.EqualTo(2));
        Assert.That(final.Keywords, Is.EqualTo(new[] { "user-tag", "beach", "sunset" }).AsCollection);
      });
    } finally {
      workingDir.Delete(recursive: true);
    }
  }

  private sealed class EmptyDetector : IDetector {
    public Task<DetectionResult> DetectAsync(FileInfo imageFile, CancellationToken cancellationToken = default)
      => Task.FromResult(DetectionResult.Empty);
  }

  private sealed class StaticDetector : IDetector {
    private readonly string[] _labels;
    public StaticDetector(params string[] labels) => this._labels = labels;

    public Task<DetectionResult> DetectAsync(FileInfo imageFile, CancellationToken cancellationToken = default) {
      var labels = this._labels
        .Select(l => new DetectionLabel(l, 1.0f, DetectionKind.Object))
        .ToArray();
      return Task.FromResult(new DetectionResult(labels));
    }
  }
}
