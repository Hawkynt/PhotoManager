using NUnit.Framework;
using Hawkynt.PhotoManager.Core.Enhance;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Enhance;

[TestFixture]
[Category("Unit")]
public sealed class PhotoIssueDetectorTests {
  [Test]
  public void LowLightScore_returns_1_for_a_pitch_dark_image() {
    using var src = new Image<Rgba32>(128, 96, new Rgba32((byte)10, (byte)10, (byte)10, (byte)255));
    Assert.That(PhotoIssueDetector.LowLightScore(src), Is.EqualTo(1).Within(1e-6));
  }

  [Test]
  public void LowLightScore_returns_0_for_a_well_exposed_image() {
    using var src = new Image<Rgba32>(128, 96, new Rgba32((byte)128, (byte)128, (byte)128, (byte)255));
    Assert.That(PhotoIssueDetector.LowLightScore(src), Is.EqualTo(0).Within(1e-6));
  }

  [Test]
  public void HazeScore_returns_high_for_globally_grey_image() {
    // Globally elevated dark channel — every pixel has min channel ≥ 150.
    using var src = new Image<Rgba32>(128, 96, new Rgba32((byte)180, (byte)180, (byte)180, (byte)255));
    Assert.That(PhotoIssueDetector.HazeScore(src), Is.GreaterThan(0.5));
  }

  [Test]
  public void HazeScore_returns_low_for_clean_image_with_dark_pixels() {
    // Half pure black, half mid-tone. After downscale, half the pixels
    // still have dark channel ≈ 0, so the mean dark channel stays well
    // below the haze threshold (50).
    using var src = new Image<Rgba32>(256, 192);
    src.ProcessPixelRows(accessor => {
      for (var y = 0; y < 192; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < 256; x++)
          row[x] = y < 96
            ? new Rgba32((byte)0, (byte)0, (byte)0, (byte)255)
            : new Rgba32((byte)80, (byte)80, (byte)80, (byte)255);
      }
    });
    Assert.That(PhotoIssueDetector.HazeScore(src), Is.LessThan(0.2));
  }

  [Test]
  public void LowResolutionScore_returns_1_for_tiny_thumbnail() {
    using var src = new Image<Rgba32>(320, 240);
    Assert.That(PhotoIssueDetector.LowResolutionScore(src), Is.EqualTo(1));
  }

  [Test]
  public void LowResolutionScore_returns_0_for_high_res_image() {
    using var src = new Image<Rgba32>(4000, 3000);
    Assert.That(PhotoIssueDetector.LowResolutionScore(src), Is.EqualTo(0));
  }
}
