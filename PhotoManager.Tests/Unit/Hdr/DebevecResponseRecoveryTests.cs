using PhotoManager.Core.Hdr;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Hdr;

[TestFixture]
public class DebevecResponseRecoveryTests {
  /// <summary>Encode linear-light irradiance to a byte through a power-law (γ ≈ 2.2) "camera" with exposure time multiplier.</summary>
  private static byte EncodePowerLaw(double irradiance, double exposureSeconds) {
    var product = irradiance * exposureSeconds;
    if (product <= 0) return 0;
    if (product >= 1) return 255;
    var encoded = Math.Pow(product, 1.0 / 2.2);
    var b = (int)Math.Round(encoded * 255.0);
    if (b < 0) return 0;
    if (b > 255) return 255;
    return (byte)b;
  }

  private static Image<Rgba32> SyntheticBracket(int w, int h, double[,] irradiance, double exposureSeconds) {
    var img = new Image<Rgba32>(w, h);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < w; x++) {
          var b = EncodePowerLaw(irradiance[y, x], exposureSeconds);
          row[x] = new Rgba32(b, b, b, 255);
        }
      }
    });
    return img;
  }

  private static double[,] BuildScene(int w, int h) {
    var scene = new double[h, w];
    for (var y = 0; y < h; y++) {
      for (var x = 0; x < w; x++) {
        var t = (double)(y * w + x) / Math.Max(1, w * h - 1);
        scene[y, x] = 0.005 + Math.Pow(t, 2.0) * 50.0;
      }
    }
    return scene;
  }

  [Test]
  public void Recover_SyntheticPowerLawCamera_ResponseIsMonotonic() {
    var scene = BuildScene(16, 16);
    var times = new[] { 1.0 / 60, 1.0 / 8, 1.0 };
    var imgs = times.Select(t => SyntheticBracket(16, 16, scene, t)).ToList();
    try {
      var map = DebevecResponseRecovery.Recover(imgs, times);
      var prev = double.NegativeInfinity;
      var monotonicCount = 0;
      for (var z = 1; z < 255; z++) {
        if (map.ResponseRed[z] > prev) {
          prev = map.ResponseRed[z];
          monotonicCount++;
        }
      }
      Assert.That(monotonicCount, Is.GreaterThan(200), "Response curve should be monotonically increasing across most levels.");
      Assert.That(map.ResponseRed[127], Is.EqualTo(0).Within(1e-6), "g(127) should equal 0 by constraint.");
    } finally {
      foreach (var i in imgs) i.Dispose();
    }
  }

  [Test]
  public void Recover_RadianceMap_BrighterPixelsHaveHigherRadiance() {
    var scene = BuildScene(8, 8);
    var times = new[] { 1.0 / 30, 1.0 / 4, 1.0 };
    var imgs = times.Select(t => SyntheticBracket(8, 8, scene, t)).ToList();
    try {
      var map = DebevecResponseRecovery.Recover(imgs, times);
      Assert.That(map.Red[0], Is.LessThan(map.Red[63]));
      Assert.That(map.Red[0], Is.GreaterThan(0));
    } finally {
      foreach (var i in imgs) i.Dispose();
    }
  }

  [Test]
  public void Recover_TooFewExposures_Throws() {
    using var single = new Image<Rgba32>(4, 4);
    Assert.Throws<ArgumentException>(() => DebevecResponseRecovery.Recover(new[] { single }, new[] { 1.0 }));
  }

  [Test]
  public void Recover_MismatchedDimensions_Throws() {
    using var a = new Image<Rgba32>(4, 4);
    using var b = new Image<Rgba32>(4, 5);
    Assert.Throws<ArgumentException>(() => DebevecResponseRecovery.Recover(new[] { a, b }, new[] { 1.0, 0.5 }));
  }
}
