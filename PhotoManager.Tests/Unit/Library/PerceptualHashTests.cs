using PhotoManager.Core.Library;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Library;

[TestFixture]
public class PerceptualHashTests {
  private DirectoryInfo _workingDir = null!;

  [SetUp]
  public void Setup() {
    this._workingDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-phash-" + Guid.NewGuid().ToString("N")));
    this._workingDir.Create();
  }

  [TearDown]
  public void Teardown() {
    if (this._workingDir.Exists)
      this._workingDir.Delete(recursive: true);
  }

  // 256x256 smooth two-blob gradient — compresses well so different JPEG
  // qualities preserve the DCT signature. Seed picks the blob centers so
  // different seeds yield discriminably different images.
  private FileInfo WritePatternedJpeg(string name, int seed, int quality = 90) {
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
  public async Task Compute_SameFile_ReturnsIdenticalHash() {
    var file = this.WritePatternedJpeg("a.jpg", seed: 7);

    var first = await PerceptualHash.ComputeAsync(file);
    var second = await PerceptualHash.ComputeAsync(file);

    Assert.That(first, Is.EqualTo(second));
  }

  [Test]
  public async Task Compute_SameImageReencoded_HashIsCloseInHamming() {
    var hi = this.WritePatternedJpeg("hi.jpg", seed: 11, quality: 95);
    var lo = this.WritePatternedJpeg("lo.jpg", seed: 11, quality: 60);

    var hHi = await PerceptualHash.ComputeAsync(hi);
    var hLo = await PerceptualHash.ComputeAsync(lo);

    var distance = PerceptualHash.HammingDistance(hHi, hLo);
    Assert.That(distance, Is.LessThanOrEqualTo(4),
      $"Re-encoded copies should hash within 4 bits; got {distance}.");
  }

  [Test]
  public async Task Compute_UnrelatedImages_HaveLargeHammingDistance() {
    var a = this.WritePatternedJpeg("a.jpg", seed: 1);
    var b = this.WritePatternedJpeg("b.jpg", seed: 999);

    var hA = await PerceptualHash.ComputeAsync(a);
    var hB = await PerceptualHash.ComputeAsync(b);

    var distance = PerceptualHash.HammingDistance(hA, hB);
    Assert.That(distance, Is.GreaterThanOrEqualTo(20),
      $"Unrelated images should differ by at least 20 bits; got {distance}.");
  }

  [Test]
  public void HammingDistance_PopcountOfXor() {
    Assert.Multiple(() => {
      Assert.That(PerceptualHash.HammingDistance(0UL, 0UL), Is.EqualTo(0));
      Assert.That(PerceptualHash.HammingDistance(0UL, ulong.MaxValue), Is.EqualTo(64));
      Assert.That(PerceptualHash.HammingDistance(0b1010UL, 0b0101UL), Is.EqualTo(4));
    });
  }
}
