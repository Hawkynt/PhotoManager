using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
public class WatermarkRenderTests {
  private static Image<Rgba32> SolidGrey(int w, int h, byte v = 128) {
    var img = new Image<Rgba32>(w, h);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new Rgba32(v, v, v, 255);
      }
    });
    return img;
  }

  private static int CountChangedPixels(Image<Rgba32> a, Image<Rgba32> b, int x0, int y0, int x1, int y1) {
    var aBuf = new Rgba32[a.Width * a.Height];
    var bBuf = new Rgba32[b.Width * b.Height];
    a.CopyPixelDataTo(aBuf);
    b.CopyPixelDataTo(bBuf);
    var changed = 0;
    for (var y = y0; y < y1; y++) {
      for (var x = x0; x < x1; x++) {
        if (aBuf[y * a.Width + x] != bBuf[y * b.Width + x]) changed++;
      }
    }
    return changed;
  }

  [Test]
  public void Apply_NoWatermark_IsIdentity() {
    using var src = SolidGrey(80, 60);
    using var output = ImageDeveloper.Apply(src, new DevelopSettings());
    Assert.That(CountChangedPixels(src, output, 0, 0, 80, 60), Is.Zero);
  }

  [Test]
  public void Apply_OpacityZero_IsIdentity() {
    using var src = SolidGrey(80, 60);
    using var output = ImageDeveloper.Apply(src,
      new DevelopSettings(WatermarkText: "Test", WatermarkOpacity: 0, WatermarkFontSize: 16));
    Assert.That(CountChangedPixels(src, output, 0, 0, 80, 60), Is.Zero);
  }

  [Test]
  public void Apply_WithWatermarkText_DiffersFromBaselineInWatermarkRegion() {
    using var src = SolidGrey(200, 80);
    using var baseline = ImageDeveloper.Apply(src, new DevelopSettings());
    using var watermarked = ImageDeveloper.Apply(src,
      new DevelopSettings(WatermarkText: "© Photo", WatermarkOpacity: 1.0, WatermarkPosition: "BottomRight", WatermarkFontSize: 24));

    // The bottom-right corner should have at least some changed pixels
    // (font may be missing on the host — accept zero only when no system
    // font collection is available).
    var changed = CountChangedPixels(baseline, watermarked, 0, 0, 200, 80);
    if (SixLabors.Fonts.SystemFonts.Families.Any())
      Assert.That(changed, Is.GreaterThan(0));

    // Top-left region should be unchanged regardless.
    var topLeftChanged = CountChangedPixels(baseline, watermarked, 0, 0, 60, 30);
    Assert.That(topLeftChanged, Is.Zero);
  }

  [Test]
  public void Apply_EmptyText_IsIdentity() {
    using var src = SolidGrey(80, 60);
    using var output = ImageDeveloper.Apply(src,
      new DevelopSettings(WatermarkText: "", WatermarkOpacity: 1.0));
    Assert.That(CountChangedPixels(src, output, 0, 0, 80, 60), Is.Zero);
  }
}
