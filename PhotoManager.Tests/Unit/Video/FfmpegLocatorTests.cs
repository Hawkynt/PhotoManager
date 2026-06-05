using System.Globalization;
using System.Runtime.InteropServices;
using Hawkynt.PhotoManager.Core.Video;

namespace Hawkynt.PhotoManager.Tests.Unit.Video;

[TestFixture]
public class FfmpegLocatorTests {
  private string? _originalPath;

  [SetUp]
  public void SetUp() {
    this._originalPath = Environment.GetEnvironmentVariable("PATH");
  }

  [TearDown]
  public void TearDown() {
    Environment.SetEnvironmentVariable("PATH", this._originalPath);
  }

  [Test]
  public void FindOnPath_ReturnsNull_WhenPathIsEmpty() {
    Environment.SetEnvironmentVariable("PATH", string.Empty);
    Assert.That(FfmpegLocator.FindOnPath(), Is.Null);
  }

  [Test]
  public void FindOnPath_ReturnsBinary_WhenItLivesInPathDir() {
    using var sandbox = new TempDir();
    var fakeFfmpeg = CreateFakeBinary(sandbox.Dir);
    Environment.SetEnvironmentVariable("PATH", sandbox.Dir.FullName);

    var found = FfmpegLocator.FindOnPath();

    Assert.That(found, Is.Not.Null);
    Assert.That(found!.FullName, Is.EqualTo(fakeFfmpeg.FullName)
      .Using<string>((a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? 0 : 1));
  }

  [Test]
  public void FindOnPath_StopsAtFirstHit_WhenMultipleDirsHaveBinary() {
    using var first = new TempDir();
    using var second = new TempDir();
    var firstHit = CreateFakeBinary(first.Dir);
    CreateFakeBinary(second.Dir);
    var combined = first.Dir.FullName + Path.PathSeparator + second.Dir.FullName;
    Environment.SetEnvironmentVariable("PATH", combined);

    var found = FfmpegLocator.FindOnPath();

    Assert.That(found, Is.Not.Null);
    Assert.That(found!.FullName, Is.EqualTo(firstHit.FullName)
      .Using<string>((a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? 0 : 1));
  }

  [Test]
  public void FindOnPath_SkipsMissingEntriesAndQuotedDirs() {
    using var sandbox = new TempDir();
    var fakeFfmpeg = CreateFakeBinary(sandbox.Dir);
    var bogus = Path.Combine(Path.GetTempPath(), $"definitely-missing-{Guid.NewGuid():N}");
    var quoted = "\"" + sandbox.Dir.FullName + "\"";
    var combined = string.Join(Path.PathSeparator.ToString(), bogus, quoted);
    Environment.SetEnvironmentVariable("PATH", combined);

    var found = FfmpegLocator.FindOnPath();

    Assert.That(found, Is.Not.Null);
    Assert.That(found!.FullName, Is.EqualTo(fakeFfmpeg.FullName)
      .Using<string>((a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? 0 : 1));
  }

  [Test]
  public void Find_FallsBackToCommonInstallPaths_WhenNotOnPath() {
    Environment.SetEnvironmentVariable("PATH", string.Empty);
    var anyExpected = FfmpegLocator.CommonInstallPaths().ToList();
    Assert.That(anyExpected, Is.Not.Empty, "There should always be at least one common install path per OS.");

    var found = FfmpegLocator.Find();

    if (found is null) {
      // Acceptable — none of the common locations exist on this box.
      Assert.Pass("No ffmpeg in any common install path; null is the expected result.");
      return;
    }

    var match = anyExpected.Any(c =>
      string.Equals(Path.GetFullPath(c), found.FullName, StringComparison.OrdinalIgnoreCase));
    Assert.That(match, Is.True, $"Found path {found.FullName} should be one of the common install paths.");
  }

  [Test]
  public void CommonInstallPaths_PerOs_ReturnsExpectedShape() {
    var paths = FfmpegLocator.CommonInstallPaths().ToList();
    Assert.That(paths, Is.Not.Empty);
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
      Assert.That(paths, Has.Some.Contains("ffmpeg.exe"));
      Assert.That(paths, Has.Some.Contains(@"C:\ffmpeg\bin\ffmpeg.exe"));
      return;
    }
    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
      Assert.That(paths, Has.Some.Contains("/opt/homebrew/bin/ffmpeg"));
      return;
    }
    Assert.That(paths, Has.Some.Contains("/usr/bin/ffmpeg"));
  }

  [Test]
  public void IsAvailable_AgreesWithFind() {
    Assert.That(FfmpegLocator.IsAvailable(), Is.EqualTo(FfmpegLocator.Find() is { Exists: true }));
  }

  [Test]
  public void BuildCommandLine_IncludesFpsAndOutputPattern() {
    using var sandbox = new TempDir();
    var video = new FileInfo(Path.Combine(sandbox.Dir.FullName, "clip.mp4"));
    File.WriteAllBytes(video.FullName, [0]);
    var outDir = new DirectoryInfo(Path.Combine(sandbox.Dir.FullName, "out"));
    var args = VideoFrameExtractor.BuildCommandLine(video, outDir, new VideoExtractOptions {
      Fps = 1.5,
      JpegQuality = 4,
    });
    Assert.Multiple(() => {
      Assert.That(args, Has.Some.EqualTo("-i"));
      Assert.That(args, Has.Some.EqualTo(video.FullName));
      Assert.That(args, Has.Some.EqualTo("-vf"));
      Assert.That(args, Has.Some.EqualTo("fps=1.5"));
      Assert.That(args, Has.Some.EqualTo("-q:v"));
      Assert.That(args, Has.Some.EqualTo("4"));
      Assert.That(args[^1], Does.Contain("frame_%05d.jpg"));
      Assert.That(args, Has.None.EqualTo("-ss"));
      Assert.That(args, Has.None.EqualTo("-to"));
    });
  }

  [Test]
  public void BuildCommandLine_AddsScaleFilter_WhenMaxLongEdgeSet() {
    using var sandbox = new TempDir();
    var video = new FileInfo(Path.Combine(sandbox.Dir.FullName, "clip.mp4"));
    File.WriteAllBytes(video.FullName, [0]);
    var args = VideoFrameExtractor.BuildCommandLine(video, sandbox.Dir, new VideoExtractOptions {
      Fps = 2,
      MaxLongEdge = 2048,
    });

    var vfIndex = args.ToList().IndexOf("-vf");
    Assert.That(vfIndex, Is.GreaterThanOrEqualTo(0));
    Assert.That(args[vfIndex + 1], Does.Contain("fps=2"));
    Assert.That(args[vfIndex + 1], Does.Contain("scale="));
    Assert.That(args[vfIndex + 1], Does.Contain("2048"));
  }

  [Test]
  public void BuildCommandLine_EmitsClipFlags_WhenStartOrEndProvided() {
    using var sandbox = new TempDir();
    var video = new FileInfo(Path.Combine(sandbox.Dir.FullName, "clip.mp4"));
    File.WriteAllBytes(video.FullName, [0]);
    var args = VideoFrameExtractor.BuildCommandLine(video, sandbox.Dir, new VideoExtractOptions {
      StartTime = TimeSpan.FromSeconds(5),
      EndTime = TimeSpan.FromSeconds(12.5),
    }).ToList();

    var ssIndex = args.IndexOf("-ss");
    var toIndex = args.IndexOf("-to");
    Assert.That(ssIndex, Is.GreaterThanOrEqualTo(0));
    Assert.That(toIndex, Is.GreaterThan(ssIndex));
    Assert.That(args[ssIndex + 1], Is.EqualTo("00:00:05.000"));
    Assert.That(args[toIndex + 1], Is.EqualTo("00:00:12.500"));
  }

  [Test]
  public void BuildCommandLine_UsesInvariantDecimalSeparator_ForFps() {
    using var sandbox = new TempDir();
    var video = new FileInfo(Path.Combine(sandbox.Dir.FullName, "clip.mp4"));
    File.WriteAllBytes(video.FullName, [0]);
    var prevCulture = CultureInfo.CurrentCulture;
    try {
      // German uses ',' as the decimal separator — without
      // InvariantCulture this would emit "fps=2,5" and ffmpeg would
      // refuse it.
      CultureInfo.CurrentCulture = new CultureInfo("de-DE");
      var args = VideoFrameExtractor.BuildCommandLine(video, sandbox.Dir, new VideoExtractOptions { Fps = 2.5 });
      var vfIndex = args.ToList().IndexOf("-vf");
      Assert.That(args[vfIndex + 1], Does.Contain("fps=2.5"));
    } finally {
      CultureInfo.CurrentCulture = prevCulture;
    }
  }

  private static FileInfo CreateFakeBinary(DirectoryInfo dir) {
    var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
    var path = Path.Combine(dir.FullName, name);
    File.WriteAllBytes(path, [0]);
    return new FileInfo(path);
  }

  private sealed class TempDir : IDisposable {
    public DirectoryInfo Dir { get; }

    public TempDir() {
      var path = Path.Combine(Path.GetTempPath(), ".pm-test", Guid.NewGuid().ToString("N"));
      this.Dir = System.IO.Directory.CreateDirectory(path);
    }

    public void Dispose() {
      try {
        if (this.Dir.Exists)
          this.Dir.Delete(recursive: true);
      } catch {
        // best-effort
      }
    }
  }
}
