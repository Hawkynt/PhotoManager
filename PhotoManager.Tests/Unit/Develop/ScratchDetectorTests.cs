using NUnit.Framework;
using Hawkynt.PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Develop;

[TestFixture]
[Category("Unit")]
public sealed class ScratchDetectorTests {
  [Test]
  public void Detect_BlankImage_ReturnsBlankMask() {
    using var img = new Image<Rgba32>(64, 64);
    // mid-gray fill
    img.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new Rgba32((byte)128, (byte)128, (byte)128, (byte)255);
      }
    });

    using var mask = ScratchDetector.Detect(img);
    long n = 0;
    mask.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          if (row[x].R >= 128) n++;
      }
    });
    Assert.That(n, Is.LessThanOrEqualTo(8), "blank image shouldn't trigger more than a few border-noise pixels");
  }

  [Test]
  public void Detect_BrightLineOnDarkBackground_HitsTheLine() {
    using var img = new Image<Rgba32>(128, 128);
    // dark background (50,50,50), bright horizontal line at y=64
    img.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var luma = (y is 63 or 64 or 65) ? (byte)230 : (byte)50;
          row[x] = new Rgba32(luma, luma, luma, (byte)255);
        }
      }
    });

    using var mask = ScratchDetector.Detect(img);

    // Mask should contain pixels near y=64.
    long onLine = 0; long offLine = 0;
    mask.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          if (row[x].R >= 128) {
            if (Math.Abs(y - 64) <= 6) onLine++;
            else offLine++;
          }
      }
    });
    Assert.That(onLine, Is.GreaterThan(50), "should detect the bright horizontal scratch");
    Assert.That(offLine, Is.LessThan(20), "off-line detections should be minimal");
  }

  [Test]
  public void Detect_MaskHasMatchingDimensions() {
    using var img = new Image<Rgba32>(95, 73);
    using var mask = ScratchDetector.Detect(img);
    Assert.That(mask.Width, Is.EqualTo(95));
    Assert.That(mask.Height, Is.EqualTo(73));
  }
}
