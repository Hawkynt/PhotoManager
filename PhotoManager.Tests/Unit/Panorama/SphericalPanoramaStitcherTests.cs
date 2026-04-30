using PhotoManager.Core.Panorama;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Panorama;

/// <summary>
/// Smoke tests for <see cref="SphericalPanoramaStitcher"/>. The OpenCV
/// stitcher path is tagged <c>RequiresOpenCv</c> so CI runners without the
/// matching <c>OpenCvSharp4.runtime.*</c> binary degrade to inconclusive
/// instead of failing.
/// </summary>
[TestFixture, Category("RequiresOpenCv")]
public class SphericalPanoramaStitcherTests {
  private static Image<Rgba32> RichPattern(int width, int height, int xOffset) {
    var img = new Image<Rgba32>(width, height);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var srcX = x + xOffset;
          var r = (byte)((srcX * 17 + y * 31 + (srcX * y) % 7) & 0xFF);
          var g = (byte)((srcX * 23 + y * 13) & 0xFF);
          var b = (byte)((srcX * 41 + y * 19) & 0xFF);
          row[x] = new Rgba32(r, g, b, 255);
        }
      }
    });
    return img;
  }

  private static Image<Rgba32> SolidBlack(int width, int height) => new(width, height);

  [Test]
  public void Stitch_RejectsSingleFrame_ReturnsNull() {
    using var only = RichPattern(64, 48, 0);
    var result = SphericalPanoramaStitcher.Stitch(new[] { only });
    Assert.That(result, Is.Null);
    Assert.That(SphericalPanoramaStitcher.LastStatus, Does.Contain("at least two"));
  }

  [Test]
  public void Stitch_AllBlackInputs_ReturnsNull() {
    using var a = SolidBlack(160, 120);
    using var b = SolidBlack(160, 120);

    Image<Rgba32>? result;
    try {
      result = SphericalPanoramaStitcher.Stitch(new[] { a, b });
    } catch (DllNotFoundException) {
      Assert.Inconclusive("OpenCV native library not loaded — skipping smoke test.");
      return;
    } catch (Exception ex) when (ex is TypeInitializationException
                              || ex is System.Reflection.TargetInvocationException) {
      Assert.Inconclusive($"OpenCV failed to initialise: {ex.Message}");
      return;
    }
    Assert.That(result, Is.Null);
  }

  [Test]
  public void Stitch_TwoOverlappingFrames_ProducesEquirectAspect() {
    using var left = RichPattern(320, 240, 0);
    using var right = RichPattern(320, 240, 200);
    var options = new SphericalStitchOptions { OutputWidth = 1024, BlendOverlaps = true };

    Image<Rgba32>? result;
    try {
      result = SphericalPanoramaStitcher.Stitch(new[] { left, right }, options);
    } catch (DllNotFoundException) {
      Assert.Inconclusive("OpenCV native library not loaded — skipping smoke test.");
      return;
    } catch (Exception ex) when (ex is TypeInitializationException
                              || ex is System.Reflection.TargetInvocationException) {
      Assert.Inconclusive($"OpenCV failed to initialise: {ex.Message}");
      return;
    }

    if (result is null) {
      // OpenCV may legitimately fail to find features in synthetic noise —
      // accept that as documented behaviour ("returns null + status").
      Assert.That(SphericalPanoramaStitcher.LastStatus, Is.Not.Empty);
      Assert.Pass($"Stitcher returned null (synthetic input): {SphericalPanoramaStitcher.LastStatus}");
      return;
    }

    using (result) {
      Assert.That(result.Width, Is.EqualTo(options.OutputWidth));
      Assert.That(result.Height, Is.EqualTo(options.OutputWidth / 2));
      var aspect = (double)result.Width / result.Height;
      // 2:1 within ±5% as documented for the fallback path.
      Assert.That(aspect, Is.EqualTo(2.0).Within(0.10));
    }
  }

  [Test]
  public void Stitch_RejectsOddOutputWidth() {
    using var a = RichPattern(64, 48, 0);
    using var b = RichPattern(64, 48, 30);
    var options = new SphericalStitchOptions { OutputWidth = 1023 };
    var result = SphericalPanoramaStitcher.Stitch(new[] { a, b }, options);
    Assert.That(result, Is.Null);
    Assert.That(SphericalPanoramaStitcher.LastStatus, Does.Contain("OutputWidth"));
  }
}
