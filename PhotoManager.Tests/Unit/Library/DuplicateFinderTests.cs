using Hawkynt.PhotoManager.Core.Library;
using Hawkynt.PhotoManager.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Library;

[TestFixture]
public class DuplicateFinderTests {
  private DirectoryInfo _workingDir = null!;

  [SetUp]
  public void Setup() {
    this._workingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-dups-" + Guid.NewGuid().ToString("N")));
    this._workingDir.Create();
  }

  [TearDown]
  public void Teardown() {
    if (this._workingDir.Exists)
      this._workingDir.Delete(recursive: true);
  }

  private static FileInfo Stub(string path) => new(path);

  [Test]
  public void FindGroups_GroupsCloseHashesAndIsolatesFarOnes() {
    // a, b within 2 bits → same group; c flips 20 bits relative to a → out.
    const ulong a = 0b1010_1010_1010_1010_1010_1010_1010_1010_1010_1010_1010_1010_1010_1010_1010_1010UL;
    var b = a ^ 0b11UL;                              // 2 bit difference
    var cMask = (1UL << 20) - 1;                     // flip the lowest 20 bits
    var c = a ^ cMask;

    var hashes = new List<HashedFile> {
      new(Stub("a.jpg"), a),
      new(Stub("b.jpg"), b),
      new(Stub("c.jpg"), c)
    };

    var groups = DuplicateFinder.FindGroups(hashes, threshold: 6);

    Assert.That(groups, Has.Count.EqualTo(1));
    var only = groups[0];
    Assert.Multiple(() => {
      Assert.That(only.Files.Select(f => f.File.Name), Is.EquivalentTo(new[] { "a.jpg", "b.jpg" }));
      Assert.That(only.Distances, Has.Count.EqualTo(2));
      Assert.That(only.Distances[0], Is.EqualTo(0));        // anchor vs itself
      Assert.That(only.Distances[1], Is.EqualTo(2));
    });
  }

  [Test]
  public void FindGroups_EmptyInputYieldsNoGroups() {
    var groups = DuplicateFinder.FindGroups(Array.Empty<HashedFile>(), threshold: 6);
    Assert.That(groups, Is.Empty);
  }

  [Test]
  public void FindGroups_AllUnique_NoGroups() {
    var hashes = new List<HashedFile> {
      new(Stub("a.jpg"), 0x1111111111111111UL),
      new(Stub("b.jpg"), 0x2222222222222222UL),
      new(Stub("c.jpg"), 0x4444444444444444UL)
    };

    var groups = DuplicateFinder.FindGroups(hashes, threshold: 6);
    Assert.That(groups, Is.Empty);
  }

  [Test]
  public void FindGroups_NegativeThreshold_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      DuplicateFinder.FindGroups(Array.Empty<HashedFile>(), threshold: -1));
  }

  private FileInfo WriteJpeg(string name, int seed, int quality = 90) {
    var path = Path.Combine(this._workingDir.FullName, name);
    using var image = new Image<Rgba32>(256, 256);
    var rng = new Random(seed);
    var cx1 = rng.Next(40, 216);
    var cy1 = rng.Next(40, 216);
    var cx2 = rng.Next(40, 216);
    var cy2 = rng.Next(40, 216);
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < accessor.Width; x++) {
          var d1 = Math.Sqrt((x - cx1) * (x - cx1) + (y - cy1) * (y - cy1));
          var d2 = Math.Sqrt((x - cx2) * (x - cx2) + (y - cy2) * (y - cy2));
          var v1 = (byte)Math.Clamp(255 - d1 * 1.5, 0, 255);
          var v2 = (byte)Math.Clamp(255 - d2 * 1.5, 0, 255);
          row[x] = new Rgba32(v1, v2, (byte)((v1 + v2) / 2));
        }
      }
    });
    image.SaveAsJpeg(path, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality });
    return new FileInfo(path);
  }

  [Test]
  public async Task ScanAsync_HashesSupportedImages_AndFindsReencodedPair() {
    this.WriteJpeg("a.jpg", seed: 5, quality: 95);
    this.WriteJpeg("a-copy.jpg", seed: 5, quality: 60);
    this.WriteJpeg("c.jpg", seed: 999);
    File.WriteAllText(Path.Combine(this._workingDir.FullName, "notes.txt"), "ignore me");

    var finder = new DuplicateFinder(formats: new SupportedFormatsService());
    var hashed = await finder.ScanAsync(this._workingDir);

    Assert.That(hashed, Is.EqualTo(3), "txt file is skipped");

    var groups = finder.FindGroups(threshold: 6);
    Assert.That(groups, Has.Count.EqualTo(1));
    Assert.That(groups[0].Files.Select(f => f.File.Name),
      Is.EquivalentTo(new[] { "a.jpg", "a-copy.jpg" }));
  }
}
