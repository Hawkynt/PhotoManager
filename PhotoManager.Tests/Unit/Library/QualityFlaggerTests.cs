using PhotoManager.Core.Library;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Tests.Unit.Library;

[TestFixture]
public class QualityFlaggerTests {
  private DirectoryInfo _tempDir = null!;

  [SetUp]
  public void SetUp() {
    this._tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-quality-" + Guid.NewGuid().ToString("N")));
    this._tempDir.Create();
  }

  [TearDown]
  public void TearDown() {
    if (this._tempDir.Exists)
      this._tempDir.Delete(recursive: true);
  }

  [Test]
  public void AnalyseImage_HighFrequencyCheckerboard_NotBlurry() {
    using var image = new Image<L8>(64, 64);
    image.ProcessPixelRows(rows => {
      for (var y = 0; y < rows.Height; y++) {
        var row = rows.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new L8((((x + y) & 1) == 0) ? (byte)20 : (byte)200);
      }
    });

    var result = QualityFlagger.AnalyseImage(image);

    Assert.Multiple(() => {
      Assert.That(result.IsBlurry, Is.False, "checkerboard has huge laplacian variance");
      Assert.That(result.SharpnessScore, Is.GreaterThan(QualityFlagger.BlurVarianceThreshold * 10));
    });
  }

  [Test]
  public void AnalyseImage_FlatGray_FlaggedBlurry() {
    using var image = new Image<L8>(64, 64);
    image.ProcessPixelRows(rows => {
      for (var y = 0; y < rows.Height; y++) {
        var row = rows.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new L8(128);
      }
    });

    var result = QualityFlagger.AnalyseImage(image);

    Assert.Multiple(() => {
      Assert.That(result.IsBlurry, Is.True, "perfectly flat image has zero high-frequency content");
      Assert.That(result.SharpnessScore, Is.LessThan(QualityFlagger.BlurVarianceThreshold));
    });
  }

  [Test]
  public void AnalyseImage_BlurredCheckerboard_FlaggedBlurry() {
    using var image = new Image<L8>(64, 64);
    image.ProcessPixelRows(rows => {
      for (var y = 0; y < rows.Height; y++) {
        var row = rows.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new L8((((x + y) & 1) == 0) ? (byte)20 : (byte)200);
      }
    });
    // Heavy gaussian blur drops the high-frequency edges far below threshold,
    // mimicking an out-of-focus capture of the same scene.
    image.Mutate(c => c.GaussianBlur(8.0f));

    var result = QualityFlagger.AnalyseImage(image);

    Assert.That(result.IsBlurry, Is.True);
  }

  [Test]
  public void AnalyseImage_AllWhite_FlaggedOverexposed() {
    using var image = new Image<L8>(64, 64);
    image.ProcessPixelRows(rows => {
      for (var y = 0; y < rows.Height; y++) {
        var row = rows.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new L8(255);
      }
    });

    var result = QualityFlagger.AnalyseImage(image);

    Assert.Multiple(() => {
      Assert.That(result.IsOverexposed, Is.True);
      Assert.That(result.IsUnderexposed, Is.False);
      Assert.That(result.ClippedHighlightFraction, Is.EqualTo(1.0).Within(1e-9));
    });
  }

  [Test]
  public void AnalyseImage_AllBlack_FlaggedUnderexposed() {
    using var image = new Image<L8>(64, 64);
    // Default L8 byte is zero, so an empty buffer = full black.

    var result = QualityFlagger.AnalyseImage(image);

    Assert.Multiple(() => {
      Assert.That(result.IsUnderexposed, Is.True);
      Assert.That(result.IsOverexposed, Is.False);
      Assert.That(result.ClippedShadowFraction, Is.EqualTo(1.0).Within(1e-9));
    });
  }

  [Test]
  public void AnalyseImage_MidGray_NoExposureFlags() {
    using var image = new Image<L8>(64, 64);
    image.ProcessPixelRows(rows => {
      for (var y = 0; y < rows.Height; y++) {
        var row = rows.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new L8(128);
      }
    });

    var result = QualityFlagger.AnalyseImage(image);

    Assert.Multiple(() => {
      Assert.That(result.IsOverexposed, Is.False);
      Assert.That(result.IsUnderexposed, Is.False);
    });
  }

  [Test]
  public async Task AnalyseAsync_MissingFile_ReturnsDefault() {
    var ghost = new FileInfo(Path.Combine(this._tempDir.FullName, "ghost.jpg"));
    var result = await new QualityFlagger().AnalyseAsync(ghost);
    Assert.That(result, Is.EqualTo(default(QualityResult)));
  }

  [Test]
  public async Task AnalyseAsync_RealJpegFlatImage_FlaggedBlurry() {
    // Round-trip via JPEG to cover the file-load path. A 1x1 JPEG flattens
    // any structure, so the result is unambiguously soft + (likely) overexposed
    // since white default fill is very common — keep the assertion tight on
    // the blur flag which is the critical one for this path.
    var path = Path.Combine(this._tempDir.FullName, "flat.jpg");
    using (var img = new Image<Rgb24>(64, 64)) {
      img.ProcessPixelRows(rows => {
        for (var y = 0; y < rows.Height; y++) {
          var row = rows.GetRowSpan(y);
          for (var x = 0; x < row.Length; x++)
            row[x] = new Rgb24(120, 120, 120);
        }
      });
      img.SaveAsJpeg(path);
    }

    var result = await new QualityFlagger().AnalyseAsync(new FileInfo(path));
    Assert.That(result.IsBlurry, Is.True);
  }
}
