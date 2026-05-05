using NUnit.Framework;
using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
[Category("Unit")]
public sealed class SaltAndPepperFilterTests {
  [Test]
  public void Filter_RemovesPureSaltAndPepperFromUniformBackground() {
    using var src = new Image<Rgba32>(64, 64);
    var rng = new Random(42);
    src.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          byte v = 128;
          var r = rng.NextDouble();
          if (r < 0.05) v = 0;
          else if (r > 0.95) v = 255;
          row[x] = new Rgba32(v, v, v, (byte)255);
        }
      }
    });

    using var clean = SaltAndPepperFilter.Filter(src);

    long extreme = 0;
    clean.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          if (row[x].R == 0 || row[x].R == 255) extreme++;
      }
    });
    Assert.That(extreme, Is.LessThanOrEqualTo(2), "should clear all 0/255 outliers from a uniform background");
  }

  [Test]
  public void Filter_PreservesGenuineDetailBelowThreshold() {
    using var src = new Image<Rgba32>(64, 64);
    src.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          // Smooth gradient — every neighbour-delta is ≤ 4, well under
          // the 35-luma threshold. No pixel should change.
          var v = (byte)Math.Min(255, x * 4 + y * 4);
          row[x] = new Rgba32(v, v, v, (byte)255);
        }
      }
    });

    using var clean = SaltAndPepperFilter.Filter(src);
    long deltaSum = 0;
    src.ProcessPixelRows(clean, (sa, ca) => {
      for (var y = 0; y < sa.Height; y++) {
        var s = sa.GetRowSpan(y);
        var c = ca.GetRowSpan(y);
        for (var x = 0; x < s.Length; x++)
          deltaSum += Math.Abs(s[x].R - c[x].R);
      }
    });
    Assert.That(deltaSum, Is.EqualTo(0), "smooth gradient should pass through unchanged");
  }

  [Test]
  public void Filter_OutputDimensionsMatchInput() {
    using var src = new Image<Rgba32>(73, 41);
    using var clean = SaltAndPepperFilter.Filter(src);
    Assert.That(clean.Width, Is.EqualTo(73));
    Assert.That(clean.Height, Is.EqualTo(41));
  }
}
