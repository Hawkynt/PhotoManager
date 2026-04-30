using PhotoManager.Core.Hdr;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Hdr;

[TestFixture]
public class ToneMapperTests {
  private static HdrRadianceMap UniformRadiance(int w, int h, float value) {
    var n = w * h;
    var r = new float[n];
    var g = new float[n];
    var b = new float[n];
    for (var i = 0; i < n; i++) {
      r[i] = value;
      g[i] = value;
      b[i] = value;
    }
    return new HdrRadianceMap(w, h, r, g, b, new double[256], new double[256], new double[256]);
  }

  private static HdrRadianceMap Gradient(int w, int h, float min, float max) {
    var n = w * h;
    var r = new float[n];
    var g = new float[n];
    var b = new float[n];
    for (var i = 0; i < n; i++) {
      var t = (float)i / Math.Max(1, n - 1);
      var v = min + (max - min) * t;
      r[i] = v;
      g[i] = v;
      b[i] = v;
    }
    return new HdrRadianceMap(w, h, r, g, b, new double[256], new double[256], new double[256]);
  }

  [Test]
  public void Reinhard_NoWhitePoint_PreservesMonotonicity() {
    var prev = -1.0;
    foreach (var v in new[] { 0.0, 0.1, 0.5, 1.0, 5.0, 10.0 }) {
      var ld = ToneMapper.ReinhardGlobal(v, 0);
      Assert.That(ld, Is.GreaterThan(prev));
      Assert.That(ld, Is.LessThan(1.0));
      prev = ld;
    }
  }

  [Test]
  public void Reinhard_WithWhitePoint_ClampsAtWhite() {
    var ld = ToneMapper.ReinhardGlobal(5.0, 5.0);
    Assert.That(ld, Is.EqualTo(1.0).Within(1e-6));
  }

  [Test]
  public void Drago_BiasMonotonicity_LowerBiasBrightensMidtones() {
    // In Drago's adaptive log mapping the bias parameter b inversely
    // controls midtone brightness — smaller b ⇒ more shadow contrast.
    var lowBias = ToneMapper.DragoLogarithmic(0.5, 1.0, 0.7);
    var midBias = ToneMapper.DragoLogarithmic(0.5, 1.0, 0.85);
    var highBias = ToneMapper.DragoLogarithmic(0.5, 1.0, 0.95);
    Assert.That(lowBias, Is.GreaterThan(midBias));
    Assert.That(midBias, Is.GreaterThan(highBias));
  }

  [Test]
  public void Map_Reinhard_ZeroRadiance_ProducesBlack() {
    var rad = UniformRadiance(8, 8, 0);
    using var img = ToneMapper.Map(rad, ToneMapOperator.Reinhard);
    img.ProcessPixelRows(a => {
      var p = a.GetRowSpan(4)[4];
      Assert.That((int)p.R, Is.EqualTo(0));
    });
  }

  [Test]
  public void Map_Reinhard_Gradient_ProducesIncreasingPixels() {
    var rad = Gradient(32, 1, 0.001f, 100f);
    using var img = ToneMapper.Map(rad, ToneMapOperator.Reinhard);
    int prev = -1;
    img.ProcessPixelRows(a => {
      var row = a.GetRowSpan(0);
      foreach (var p in row) {
        Assert.That((int)p.R, Is.GreaterThanOrEqualTo(prev));
        prev = p.R;
      }
    });
    Assert.That(prev, Is.GreaterThan(50));
  }

  [Test]
  public void Map_Drago_HandlesHighDynamicRange() {
    var rad = Gradient(32, 1, 0.001f, 1000f);
    using var img = ToneMapper.Map(rad, ToneMapOperator.Drago, dragoBias: 0.85);
    int dark = -1, bright = -1;
    img.ProcessPixelRows(a => {
      var row = a.GetRowSpan(0);
      dark = row[0].R;
      bright = row[31].R;
    });
    Assert.Multiple(() => {
      Assert.That(dark, Is.LessThan(40), "Near-black input should map near zero LDR.");
      Assert.That(bright, Is.GreaterThan(120), "Near-saturation input should map well into upper LDR range.");
      Assert.That(bright, Is.GreaterThan(dark + 100));
    });
  }
}
