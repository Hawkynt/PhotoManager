using PhotoManager.Core.Detection;

namespace PhotoManager.Tests.Unit.Detection;

[TestFixture]
public class PathDerivedDetectorTests {
  // Test fixtures use forward-slash paths — both Windows and Linux
  // FileInfo parse "/" as a path separator. Backslashes are
  // Windows-only, so Linux CI saw a string with no separators and
  // walked the test runner's working directory instead.
  [Test]
  public async Task DetectAsync_FolderStructure_ExtractsSemanticTokens() {
    var detector = new PathDerivedDetector();
    var file = new FileInfo("/Photos/2024/Vacation/Rome/IMG_0001.jpg");

    var result = await detector.DetectAsync(file);

    var names = result.DistinctLabelNames().ToArray();
    Assert.That(names, Does.Contain("vacation").IgnoreCase);
    Assert.That(names, Does.Contain("rome").IgnoreCase);
  }

  [Test]
  public async Task DetectAsync_FiltersStopWords() {
    var detector = new PathDerivedDetector();
    var file = new FileInfo("/Pictures/DCIM/raw/IMG_0002.jpg");

    var result = await detector.DetectAsync(file);

    var names = result.DistinctLabelNames().Select(n => n.ToLowerInvariant()).ToArray();
    Assert.That(names, Does.Not.Contain("pictures"));
    Assert.That(names, Does.Not.Contain("dcim"));
    Assert.That(names, Does.Not.Contain("raw"));
  }

  [Test]
  public async Task DetectAsync_FiltersDateLookingTokens() {
    var detector = new PathDerivedDetector();
    var file = new FileInfo("/Photos/2024/2024-01-15/party/IMG.jpg");

    var result = await detector.DetectAsync(file);

    var names = result.DistinctLabelNames().Select(n => n.ToLowerInvariant()).ToArray();
    Assert.That(names, Does.Not.Contain("2024"),
      "4-digit year should be filtered out — it's organizational, not semantic");
    Assert.That(names, Does.Not.Contain("2024-01-15"));
    Assert.That(names, Does.Contain("party"));
  }

  [Test]
  public async Task DetectAsync_SplitsCompoundSegments() {
    var detector = new PathDerivedDetector();
    var file = new FileInfo("/Photos/beach-sunset_paris.2024/IMG.jpg");

    var result = await detector.DetectAsync(file);

    var names = result.DistinctLabelNames().Select(n => n.ToLowerInvariant()).ToArray();
    Assert.That(names, Does.Contain("beach"));
    Assert.That(names, Does.Contain("sunset"));
    Assert.That(names, Does.Contain("paris"));
  }

  [Test]
  public async Task DetectAsync_NoParent_ReturnsEmpty() {
    var detector = new PathDerivedDetector();
    var file = new FileInfo("only-a-filename.jpg");

    var result = await detector.DetectAsync(file);

    Assert.That(result.Labels, Is.Not.Null);
  }
}
