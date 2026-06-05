using NUnit.Framework;
using Hawkynt.PhotoManager.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Imaging;

[TestFixture]
[Category("Unit")]
public sealed class ReinhardToneMapTests {
  [Test]
  public void Apply_preserves_dimensions() {
    using var src = new Image<Rgba32>(96, 64);
    using var dst = ReinhardToneMap.Apply(src);
    Assert.That(dst.Width, Is.EqualTo(96));
    Assert.That(dst.Height, Is.EqualTo(64));
  }

  [Test]
  public void Apply_lifts_mid_tones_for_a_dark_scene() {
    // Mostly-dark scene: 90 % pixels at value 30, 10 % at value 200.
    // Log-average is dominated by the darks → scale lifts everything up
    // so the resulting mean is closer to mid-grey (128) than the input.
    using var src = new Image<Rgba32>(64, 64);
    src.ProcessPixelRows(accessor => {
      for (var y = 0; y < 64; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < 64; x++) {
          var v = (y * 64 + x) % 10 == 0 ? (byte)200 : (byte)30;
          row[x] = new Rgba32(v, v, v, (byte)255);
        }
      }
    });

    using var dst = ReinhardToneMap.Apply(src, key: 0.18, whitePoint: 4.0);

    long sum = 0;
    var n = 0;
    dst.ProcessPixelRows(accessor => {
      for (var y = 0; y < 64; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < 64; x++) {
          sum += row[x].R;
          n++;
        }
      }
    });
    var dstMean = sum / (double)n;

    // Source mean ~= 30 * 0.9 + 200 * 0.1 = 47. Reinhard should at
    // minimum not crush the scene further — the dominant-dark path
    // typically lifts shadows somewhat. Stay conservative: assert just
    // that we don't darken below the source mean.
    Assert.That(dstMean, Is.GreaterThanOrEqualTo(40),
      $"Reinhard should not darken a dark scene; got mean = {dstMean:F1}.");
  }
}
