using Hawkynt.PhotoManager.Core.Hdr;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Hdr;

[TestFixture]
public class HdrMergerTests {
  private static byte EncodePowerLaw(double irradiance, double exposureSeconds) {
    var product = irradiance * exposureSeconds;
    if (product <= 0) return 0;
    if (product >= 1) return 255;
    var encoded = Math.Pow(product, 1.0 / 2.2);
    return (byte)Math.Clamp((int)Math.Round(encoded * 255.0), 0, 255);
  }

  /// <summary>
  /// Build a high-DR scene with a smooth gradient from very dim to very bright;
  /// no exposure can capture both extremes well.
  /// </summary>
  private static (double[,] Scene, int W, int H) HighDrScene() {
    const int w = 32;
    const int h = 32;
    var scene = new double[h, w];
    for (var y = 0; y < h; y++) {
      for (var x = 0; x < w; x++) {
        var t = (double)x / (w - 1);
        scene[y, x] = 0.005 * Math.Pow(2000.0, t);
      }
    }
    return (scene, w, h);
  }

  private static Image<Rgba32> Render(double[,] scene, int w, int h, double exposureSeconds) {
    var img = new Image<Rgba32>(w, h);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < w; x++) {
          var b = EncodePowerLaw(scene[y, x], exposureSeconds);
          row[x] = new Rgba32(b, b, b, 255);
        }
      }
    });
    return img;
  }

  [Test]
  public async Task MergeAsync_3Frame_OutputBrightInShadows_NotBlownInHighlights() {
    var (scene, w, h) = HighDrScene();
    var times = new[] { 1.0 / 1000, 1.0 / 30, 1.0 };
    var imgs = times.Select(t => Render(scene, w, h, t)).ToList();
    try {
      var darkest = imgs[0];
      var brightest = imgs[2];

      var optionsResult = await HdrMerger.MergeAsync(
        BuildEntries(imgs, times),
        new HdrOptions(AlignBeforeMerge: false));

      using var ldr = optionsResult.Image;

      // Sample the shadow column (left half) and highlight column (right half).
      int ldrShadow = 0, ldrHighlight = 0, darkShadow = 0, brightHighlight = 0;
      ldr.ProcessPixelRows(a => {
        ldrShadow = a.GetRowSpan(h / 2)[w / 4].R;
        ldrHighlight = a.GetRowSpan(h / 2)[3 * w / 4].R;
      });
      darkest.ProcessPixelRows(a => {
        darkShadow = a.GetRowSpan(h / 2)[w / 4].R;
      });
      brightest.ProcessPixelRows(a => {
        brightHighlight = a.GetRowSpan(h / 2)[3 * w / 4].R;
      });

      Assert.Multiple(() => {
        Assert.That(ldrShadow, Is.GreaterThan(darkShadow), "HDR shadows should lift the darkest frame's near-black region.");
        Assert.That(brightHighlight, Is.EqualTo(255), "Sanity: brightest frame is saturated where we expect.");
        Assert.That(ldrHighlight, Is.LessThan(255), "HDR highlights should not be 100% blown out.");
        Assert.That(ldrShadow, Is.LessThan(ldrHighlight), "LDR shadow should still be darker than the highlight after tone mapping.");
      });
    } finally {
      foreach (var i in imgs) i.Dispose();
    }
  }

  [Test]
  public async Task MergeAsync_FewerThanTwoFrames_Throws() {
    Assert.ThrowsAsync<ArgumentException>(() => HdrMerger.MergeAsync(
      new List<HdrBracketEntry>(),
      new HdrOptions()));
    await Task.CompletedTask;
  }

  [Test]
  public void Options_DefaultsAreSensible() {
    var o = new HdrOptions();
    Assert.Multiple(() => {
      Assert.That(o.AlignBeforeMerge, Is.True);
      Assert.That(o.Operator, Is.EqualTo(ToneMapOperator.Reinhard));
      Assert.That(o.DragoBias, Is.EqualTo(0.85));
      Assert.That(o.Saturation, Is.EqualTo(1.0));
    });
  }

  private static List<HdrBracketEntry> BuildEntries(List<Image<Rgba32>> imgs, double[] times) {
    var dir = Path.Combine(Path.GetTempPath(), "phm-hdr-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var entries = new List<HdrBracketEntry>();
    for (var i = 0; i < imgs.Count; i++) {
      var path = Path.Combine(dir, $"f{i}.png");
      imgs[i].SaveAsPng(path);
      entries.Add(new HdrBracketEntry(new FileInfo(path), times[i]));
    }
    return entries;
  }
}
