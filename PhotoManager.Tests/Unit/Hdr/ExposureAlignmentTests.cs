using PhotoManager.Core.Hdr;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Hdr;

[TestFixture]
public class ExposureAlignmentTests {
  private static Image<Rgba32> Checkerboard(int width, int height, int cell) {
    var img = new Image<Rgba32>(width, height);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < width; x++) {
          var on = ((x / cell) + (y / cell)) % 2 == 0;
          var v = (byte)(on ? 220 : 30);
          row[x] = new Rgba32(v, v, v, 255);
        }
      }
    });
    return img;
  }

  /// <summary>
  /// Aperiodic deterministic luminance pattern. MTB alignment needs
  /// unambiguous content; periodic checkerboards alias and let the search
  /// lock onto a different lattice cell. A pseudo-random luma per pixel
  /// gives a histogram broad enough to make the median split meaningful
  /// and avoids any aliasing.
  /// </summary>
  private static Image<Rgba32> Aperiodic(int width, int height) {
    var img = new Image<Rgba32>(width, height);
    var rng = new Random(20260429);
    var values = new byte[width * height];
    for (var i = 0; i < values.Length; i++)
      values[i] = (byte)rng.Next(256);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < width; x++) {
          var bb = values[y * width + x];
          row[x] = new Rgba32(bb, bb, bb, 255);
        }
      }
    });
    return img;
  }

  private static Image<Rgba32> Shift(Image<Rgba32> source, int dx, int dy) {
    var w = source.Width;
    var h = source.Height;
    var output = new Image<Rgba32>(w, h);
    source.ProcessPixelRows(output, (src, dst) => {
      for (var y = 0; y < h; y++) {
        var srcY = Math.Clamp(y - dy, 0, h - 1);
        var srcRow = src.GetRowSpan(srcY);
        var dstRow = dst.GetRowSpan(y);
        for (var x = 0; x < w; x++) {
          var srcX = Math.Clamp(x - dx, 0, w - 1);
          dstRow[x] = srcRow[srcX];
        }
      }
    });
    return output;
  }

  [Test]
  public void Align_NoShift_ReturnsZero() {
    using var a = Checkerboard(64, 64, 8);
    using var b = Checkerboard(64, 64, 8);
    var t = ExposureAlignment.Align(a, b);
    Assert.Multiple(() => {
      Assert.That(t.Dx, Is.EqualTo(0));
      Assert.That(t.Dy, Is.EqualTo(0));
    });
  }

  [Test]
  public void Align_PositiveShift_RecoversTranslation() {
    // Align returns the corrective translation: applying it via Shift to
    // the candidate brings it back onto the reference, so the recovered
    // values are the negatives of the synthetic test shift.
    using var refImg = Aperiodic(128, 128);
    using var shifted = Shift(refImg, 3, -5);
    var t = ExposureAlignment.Align(refImg, shifted);
    Assert.Multiple(() => {
      Assert.That(t.Dx, Is.EqualTo(-3));
      Assert.That(t.Dy, Is.EqualTo(5));
    });
  }

  [Test]
  public void Align_LargerShift_PyramidStillRecovers() {
    using var refImg = Aperiodic(160, 160);
    using var shifted = Shift(refImg, -7, 11);
    var t = ExposureAlignment.Align(refImg, shifted);
    Assert.Multiple(() => {
      Assert.That(t.Dx, Is.EqualTo(7));
      Assert.That(t.Dy, Is.EqualTo(-11));
    });
  }

  [Test]
  public void Shift_AppliesTranslationAndPadsEdges() {
    using var refImg = Checkerboard(40, 40, 5);
    using var shifted = ExposureAlignment.Shift(refImg, new Translation(2, 0));
    refImg.ProcessPixelRows(shifted, (src, dst) => {
      var srcRow = src.GetRowSpan(20);
      var dstRow = dst.GetRowSpan(20);
      for (var x = 2; x < 40; x++) {
        Assert.That(dstRow[x].R, Is.EqualTo(srcRow[x - 2].R));
      }
    });
  }

  [Test]
  public void MismatchedDimensions_Throws() {
    using var a = Checkerboard(40, 40, 5);
    using var b = Checkerboard(40, 41, 5);
    Assert.Throws<ArgumentException>(() => ExposureAlignment.Align(a, b));
  }
}
