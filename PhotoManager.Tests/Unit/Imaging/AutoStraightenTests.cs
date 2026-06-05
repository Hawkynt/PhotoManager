using NUnit.Framework;
using Hawkynt.PhotoManager.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.Tests.Unit.Imaging;

[TestFixture]
[Category("Unit")]
public sealed class AutoStraightenTests {
  [Test]
  public void EstimateRotation_returns_zero_for_a_uniform_image() {
    using var src = new Image<Rgba32>(256, 256, new Rgba32((byte)128, (byte)128, (byte)128, (byte)255));
    Assert.That(AutoStraighten.EstimateRotation(src), Is.EqualTo(0));
  }

  [Test]
  public void EstimateRotation_returns_within_search_range() {
    // Multi-edge synthetic image — make sure we always return a result
    // inside the [-MaxAngle, +MaxAngle] window, even if the detector
    // can't pin a single dominant orientation.
    using var src = new Image<Rgba32>(256, 256);
    src.ProcessPixelRows(accessor => {
      for (var y = 0; y < 256; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < 256; x++)
          row[x] = ((x + y) & 0x10) == 0
            ? new Rgba32((byte)0, (byte)0, (byte)0, (byte)255)
            : new Rgba32((byte)255, (byte)255, (byte)255, (byte)255);
      }
    });

    var estimate = AutoStraighten.EstimateRotation(src);
    Assert.That(Math.Abs(estimate), Is.LessThanOrEqualTo(AutoStraighten.MaxAngleDegrees + 0.001),
      $"Estimate {estimate:F2}° escaped the search range.");
  }
}
