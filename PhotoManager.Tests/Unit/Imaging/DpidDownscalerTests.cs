using NUnit.Framework;
using Hawkynt.PhotoManager.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Imaging;

[TestFixture]
[Category("Unit")]
public sealed class DpidDownscalerTests {
  [Test]
  public void Downscale_produces_target_dimensions() {
    using var src = new Image<Rgba32>(256, 256);
    using var dst = DpidDownscaler.Downscale(src, 64, 64);

    Assert.That(dst.Width, Is.EqualTo(64));
    Assert.That(dst.Height, Is.EqualTo(64));
  }

  [Test]
  public void Downscale_uniform_image_returns_the_uniform_colour() {
    using var src = new Image<Rgba32>(128, 128, new Rgba32((byte)123, (byte)45, (byte)67, (byte)255));
    using var dst = DpidDownscaler.Downscale(src, 32, 32);

    Assert.That(dst[16, 16], Is.EqualTo(new Rgba32((byte)123, (byte)45, (byte)67, (byte)255)));
  }

  [Test]
  public void Downscale_preserves_edge_contrast_at_aligned_boundary() {
    // 256×8 step edge at column 128 (cols 0..127 black, 128..255 white).
    // Downscale to 4×1: boundaries land at 0/64/128/192/256, so left
    // halves are pure black and right halves pure white. DPID must keep
    // them that way (the patch is uniform per output sample).
    using var src = new Image<Rgba32>(256, 8);
    src.ProcessPixelRows(accessor => {
      for (var y = 0; y < 8; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < 256; x++)
          row[x] = x < 128
            ? new Rgba32((byte)0, (byte)0, (byte)0, (byte)255)
            : new Rgba32((byte)255, (byte)255, (byte)255, (byte)255);
      }
    });

    using var dpid = DpidDownscaler.Downscale(src, 4, 1);
    Assert.That(dpid[0, 0].R, Is.EqualTo(0), "First sample should stay pure black.");
    Assert.That(dpid[1, 0].R, Is.EqualTo(0), "Second sample should stay pure black.");
    Assert.That(dpid[2, 0].R, Is.EqualTo(255), "Third sample should stay pure white.");
    Assert.That(dpid[3, 0].R, Is.EqualTo(255), "Fourth sample should stay pure white.");
  }

  [Test]
  public void Downscale_preserves_a_single_distinctive_pixel_in_a_patch() {
    // Patch of 64 grey pixels with one bright-red outlier. Box-mean
    // would dilute it to grey+1; DPID with lambda=1 should weight the
    // outlier substantially because it is far from the patch mean,
    // producing a meaningfully redder output than the grey base.
    using var src = new Image<Rgba32>(8, 8, new Rgba32((byte)128, (byte)128, (byte)128, (byte)255));
    src[3, 3] = new Rgba32((byte)255, (byte)0, (byte)0, (byte)255);

    using var box = ThumbnailManager.Resize(src, 1, 1);
    using var dpid = DpidDownscaler.Downscale(src, 1, 1);

    // DPID's R should exceed the box-mean R because the red outlier carries higher weight.
    Assert.That(dpid[0, 0].R, Is.GreaterThan(box[0, 0].R),
      $"DPID R={dpid[0, 0].R} should exceed box-mean R={box[0, 0].R} thanks to distance weighting.");
  }

  [Test]
  public void Downscale_throws_when_target_larger_than_source() {
    using var src = new Image<Rgba32>(64, 64);
    Assert.Throws<ArgumentException>(() => DpidDownscaler.Downscale(src, 128, 128));
  }

  [Test]
  public void Downscale_throws_for_invalid_target_dimensions() {
    using var src = new Image<Rgba32>(64, 64);
    Assert.Throws<ArgumentOutOfRangeException>(() => DpidDownscaler.Downscale(src, 0, 32));
    Assert.Throws<ArgumentOutOfRangeException>(() => DpidDownscaler.Downscale(src, 32, -1));
  }
}
