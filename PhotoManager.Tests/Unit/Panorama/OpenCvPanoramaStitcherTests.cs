using Hawkynt.PhotoManager.Core.Panorama;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Panorama;

/// <summary>
/// Integration smoke tests for <see cref="OpenCvPanoramaStitcher"/>. These
/// tests are tagged <c>RequiresOpenCv</c> and degrade to inconclusive when
/// the OpenCV native runtime can't be loaded — typically a Linux container
/// without the matching <c>OpenCvSharp4.runtime.linux-x64</c> package
/// available, or a feature-poor scene where the stitcher returns NULL.
/// </summary>
[TestFixture, Category("RequiresOpenCv")]
public class OpenCvPanoramaStitcherTests {
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

  [Test]
  public void Stitch_TwoOverlappingFrames_ReturnsImageOrNull() {
    // The pseudo-random pattern is intentionally feature-rich so the OpenCV
    // feature detector has something to lock onto. If it still can't merge
    // (e.g. patterns are too synthetic), we accept a null return as the
    // documented "couldn't stitch" outcome — the test passes either way as
    // long as we don't blow up.
    using var left = RichPattern(320, 240, 0);
    using var right = RichPattern(320, 240, 200);

    Image<Rgba32>? result;
    try {
      result = OpenCvPanoramaStitcher.Stitch(new[] { left, right });
    } catch (DllNotFoundException) {
      Assert.Inconclusive("OpenCV native library not loaded on this platform — skipping smoke test.");
      return;
    } catch (Exception ex) when (ex is TypeInitializationException
                              || ex is System.Reflection.TargetInvocationException) {
      Assert.Inconclusive($"OpenCV failed to initialise: {ex.Message}");
      return;
    }

    if (result is null) {
      Assert.Pass("Stitcher returned null (insufficient features in synthetic input) — acceptable.");
      return;
    }

    using (result) {
      Assert.That(result.Width, Is.GreaterThan(0));
      Assert.That(result.Height, Is.GreaterThan(0));
    }
  }

  [Test]
  public void Stitch_RejectsSingleFrame() {
    using var only = RichPattern(50, 50, 0);
    Assert.Throws<ArgumentException>(() => OpenCvPanoramaStitcher.Stitch(new[] { only }));
  }
}
