using NUnit.Framework;
using PhotoManager.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Imaging;

[TestFixture]
[Category("Unit")]
public sealed class ClaheTests {
  [Test]
  public void Apply_preserves_dimensions() {
    using var src = new Image<Rgba32>(128, 96);
    using var dst = Clahe.Apply(src);
    Assert.That(dst.Width, Is.EqualTo(128));
    Assert.That(dst.Height, Is.EqualTo(96));
  }

  [Test]
  public void Apply_throws_on_invalid_args() {
    using var src = new Image<Rgba32>(64, 64);
    Assert.Throws<ArgumentOutOfRangeException>(() => Clahe.Apply(src, tileCount: 0));
    Assert.Throws<ArgumentOutOfRangeException>(() => Clahe.Apply(src, clipLimit: 0));
  }

  [Test]
  public void Apply_expands_dynamic_range_on_a_low_contrast_image() {
    // Image of grey values in [110, 145] — a narrow band. CLAHE should
    // stretch this toward the full [0, 255] range.
    using var src = new Image<Rgba32>(64, 64);
    src.ProcessPixelRows(accessor => {
      for (var y = 0; y < 64; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < 64; x++) {
          var v = (byte)(110 + ((x + y) & 0x1F));  // 110..141
          row[x] = new Rgba32(v, v, v, (byte)255);
        }
      }
    });

    using var dst = Clahe.Apply(src);
    var min = (byte)255;
    var max = (byte)0;
    dst.ProcessPixelRows(accessor => {
      for (var y = 0; y < 64; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < 64; x++) {
          if (row[x].R < min) min = row[x].R;
          if (row[x].R > max) max = row[x].R;
        }
      }
    });

    // Source dynamic range is 110..141 (span 31). CLAHE with default
    // parameters should at minimum widen that meaningfully; the exact
    // amount depends on tile / clip-limit defaults so we assert just
    // that the new span is materially larger.
    var srcSpan = 141 - 110;
    var dstSpan = max - min;
    Assert.That(dstSpan, Is.GreaterThan(srcSpan + 10),
      $"CLAHE should widen the value range; got src span {srcSpan} → dst span {dstSpan}.");
  }
}
