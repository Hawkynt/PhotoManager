using Hawkynt.PhotoManager.Core.Panorama;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Panorama;

[TestFixture]
public class TripodPanoramaStitcherTests {
  /// <summary>
  /// Build a 400x200 image with a horizontal gradient varied in both axes
  /// (so SSD search has to find the right shift, not flat overlap).
  /// </summary>
  private static Image<Rgba32> Pattern(int width, int height) {
    var img = new Image<Rgba32>(width, height);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var r = (byte)((x * 13 + y * 5) % 256);
          var g = (byte)((x * 7 + y * 11) % 256);
          var b = (byte)((x * 3 + y * 17) % 256);
          row[x] = new Rgba32(r, g, b, 255);
        }
      }
    });
    return img;
  }

  private static Image<Rgba32> Crop(Image<Rgba32> source, int x, int y, int width, int height) {
    var output = new Image<Rgba32>(width, height);
    source.ProcessPixelRows(output, (src, dst) => {
      for (var j = 0; j < height; j++) {
        var srcRow = src.GetRowSpan(y + j);
        var dstRow = dst.GetRowSpan(j);
        for (var i = 0; i < width; i++)
          dstRow[i] = srcRow[x + i];
      }
    });
    return output;
  }

  [Test]
  public void Stitch_SingleFrame_ReturnsClone() {
    using var src = Pattern(50, 30);
    using var stitched = TripodPanoramaStitcher.Stitch(new[] { src });
    Assert.That(stitched.Width, Is.EqualTo(50));
    Assert.That(stitched.Height, Is.EqualTo(30));
  }

  [Test]
  public void Stitch_TwoOverlappingFrames_ProducesFullWidthMosaic() {
    // 400×200 source, split at x=200 with 30% (= 60px) overlap. The right
    // crop starts 60 pixels earlier, so frames share x∈[140,200] of the
    // original.
    using var full = Pattern(400, 200);
    using var left = Crop(full, 0, 0, 200, 200);
    using var right = Crop(full, 140, 0, 260, 200);

    using var stitched = TripodPanoramaStitcher.Stitch(new[] { left, right }, overlapHint: 0.23);

    // The combined width should be ≈400 (200 + 260 - 60). Allow ±20 px
    // for search-window slack on the SSD-minimisation alignment.
    Assert.That(stitched.Width, Is.InRange(380, 420));
    Assert.That(stitched.Height, Is.InRange(195, 205));

    // The central column of the original full image should survive in the
    // stitched output: read its colour and look for it nearby in the result.
    var fullCenter = ReadPixel(full, 200, 100);
    var found = ScanForPixelInRow(stitched, 100, fullCenter, tolerance: 4);
    Assert.That(found, Is.True, "Expected to find the original central pixel in the stitched output.");
  }

  [Test]
  public void Stitch_RejectsEmptyList() {
    Assert.Throws<ArgumentException>(() => TripodPanoramaStitcher.Stitch(Array.Empty<Image<Rgba32>>()));
  }

  private static Rgba32 ReadPixel(Image<Rgba32> img, int x, int y) {
    Rgba32 picked = default;
    img.ProcessPixelRows(accessor => { picked = accessor.GetRowSpan(y)[x]; });
    return picked;
  }

  private static bool ScanForPixelInRow(Image<Rgba32> img, int y, Rgba32 needle, int tolerance) {
    var found = false;
    img.ProcessPixelRows(accessor => {
      var row = accessor.GetRowSpan(Math.Min(y, accessor.Height - 1));
      for (var x = 0; x < row.Length; x++) {
        var px = row[x];
        if (Math.Abs(px.R - needle.R) <= tolerance
            && Math.Abs(px.G - needle.G) <= tolerance
            && Math.Abs(px.B - needle.B) <= tolerance) {
          found = true;
          return;
        }
      }
    });
    return found;
  }
}
