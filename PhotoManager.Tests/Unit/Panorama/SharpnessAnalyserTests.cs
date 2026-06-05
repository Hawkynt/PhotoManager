using Hawkynt.PhotoManager.Core.Panorama;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Panorama;

[TestFixture]
public class SharpnessAnalyserTests {
  private static Image<Rgba32> Solid(int width, int height, byte r, byte g, byte b) {
    var img = new Image<Rgba32>(width, height);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new Rgba32(r, g, b, 255);
      }
    });
    return img;
  }

  private static Image<Rgba32> Checkerboard(int width, int height, int cellSize) {
    var img = new Image<Rgba32>(width, height);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var black = ((x / cellSize) + (y / cellSize)) % 2 == 0;
          var c = (byte)(black ? 0 : 255);
          row[x] = new Rgba32(c, c, c, 255);
        }
      }
    });
    return img;
  }

  private static double SharpnessFraction(Image<L8> mask) {
    long sharp = 0;
    long total = 0;
    mask.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          if (row[x].PackedValue >= 128)
            sharp++;
          total++;
        }
      }
    });
    return (double)sharp / total;
  }

  [Test]
  public void BuildPatchMask_AllZeroImage_ProducesAllZeroMask() {
    using var img = Solid(128, 128, 0, 0, 0);
    using var mask = SharpnessAnalyser.BuildPatchMask(img, patchSize: 32);
    Assert.That(SharpnessFraction(mask), Is.EqualTo(0.0));
  }

  [Test]
  public void BuildPatchMask_UniformMidGreyImage_ProducesAllZeroMask() {
    using var img = Solid(128, 128, 128, 128, 128);
    using var mask = SharpnessAnalyser.BuildPatchMask(img, patchSize: 32);
    Assert.That(SharpnessFraction(mask), Is.EqualTo(0.0));
  }

  [Test]
  public void BuildPatchMask_SharpCheckerboard_ProducesAllSharpMask() {
    // Every patch hits the same high Laplacian variance, so the median ==
    // each variance, and threshold = median * fraction is below all
    // variances → every patch is sharp.
    using var img = Checkerboard(128, 128, 4);
    using var mask = SharpnessAnalyser.BuildPatchMask(img, patchSize: 32);
    Assert.That(SharpnessFraction(mask), Is.EqualTo(1.0));
  }

  [Test]
  public void BuildPatchMask_HalfSharpHalfBlurred_RoughlyHalves() {
    // Sharp checkerboard on the left half, flat grey on the right half.
    using var sharp = Checkerboard(128, 128, 4);
    using var img = new Image<Rgba32>(128, 128);
    sharp.ProcessPixelRows(img, (src, dst) => {
      for (var y = 0; y < src.Height; y++) {
        var srcRow = src.GetRowSpan(y);
        var dstRow = dst.GetRowSpan(y);
        for (var x = 0; x < dstRow.Length; x++)
          dstRow[x] = x < 64 ? srcRow[x] : new Rgba32(128, 128, 128, 255);
      }
    });

    using var mask = SharpnessAnalyser.BuildPatchMask(img, patchSize: 32);
    var fraction = SharpnessFraction(mask);
    // 50% sharp before dilation; the 3x3 max-filter then spills sharpness
    // one patch into the grey side, raising the seam from 50% to ~75%.
    // Anything in [0.35, 0.85] is the "roughly halves at the boundary"
    // contract from the spec.
    Assert.That(fraction, Is.GreaterThan(0.35).And.LessThanOrEqualTo(0.85),
      $"Expected ~50% sharp (allowing dilation halo), got {fraction:P0}");
  }

  [Test]
  public void BuildPatchMask_SharpRectangleOnBlurredBackground_CoversTheRectangle() {
    // 256x256 mostly-uniform background with a sharp 64x64 rectangle in
    // the centre. The mask should be sharp inside the rectangle and zero
    // outside (modulo a one-patch dilation halo).
    using var img = new Image<Rgba32>(256, 256);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var insideRect = x >= 96 && x < 160 && y >= 96 && y < 160;
          if (insideRect) {
            // checkerboard inside
            var c = (byte)((((x / 4) + (y / 4)) % 2 == 0) ? 0 : 255);
            row[x] = new Rgba32(c, c, c, 255);
          } else {
            row[x] = new Rgba32(120, 120, 120, 255);
          }
        }
      }
    });

    using var mask = SharpnessAnalyser.BuildPatchMask(img, patchSize: 32);

    // Centre pixel must be sharp.
    var centreSharp = false;
    var cornerSharp = false;
    mask.ProcessPixelRows(accessor => {
      centreSharp = accessor.GetRowSpan(128)[128].PackedValue >= 128;
      cornerSharp = accessor.GetRowSpan(8)[8].PackedValue >= 128;
    });
    Assert.That(centreSharp, Is.True, "Centre of sharp rectangle should be marked sharp.");
    Assert.That(cornerSharp, Is.False, "Top-left corner (uniform background) should be marked blurry.");
  }

  [Test]
  public void BuildPatchMask_LowContrastImage_StillMarksSomePatchesSharp() {
    // Low-contrast variation: every pixel has a small noise-like variation
    // but the overall variance is low. The adaptive threshold (median *
    // fraction) means at least the median patch is at threshold-equality,
    // so we should see ~half the patches marked sharp (not the whole mask
    // dropped to zero).
    var img = new Image<Rgba32>(128, 128);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          // Sub-pattern with small fluctuation: vertical stripes of 2 px,
          // luminance step of 4 (very low contrast but non-zero variance).
          var c = (byte)(120 + (x % 2) * 4);
          row[x] = new Rgba32(c, c, c, 255);
        }
      }
    });

    using (img) {
      using var mask = SharpnessAnalyser.BuildPatchMask(img, patchSize: 32);
      var fraction = SharpnessFraction(mask);
      Assert.That(fraction, Is.GreaterThan(0.0),
        "Adaptive threshold should keep some patches sharp on low-contrast input.");
    }
  }

  [Test]
  public void BuildPatchMask_OutputDimensionsMatchInput() {
    using var img = Checkerboard(123, 91, 7);
    using var mask = SharpnessAnalyser.BuildPatchMask(img, patchSize: 32);
    Assert.That(mask.Width, Is.EqualTo(123));
    Assert.That(mask.Height, Is.EqualTo(91));
  }

  [Test]
  public void BuildPatchMask_RejectsTinyPatchSize() {
    using var img = Solid(32, 32, 0, 0, 0);
    Assert.Throws<ArgumentOutOfRangeException>(() => SharpnessAnalyser.BuildPatchMask(img, patchSize: 1));
  }

  [Test]
  public void BuildPatchMask_RejectsNegativeFraction() {
    using var img = Solid(32, 32, 0, 0, 0);
    Assert.Throws<ArgumentOutOfRangeException>(() => SharpnessAnalyser.BuildPatchMask(img, patchSize: 16, minVarianceFraction: -0.1));
  }

  [Test]
  public void OpenCvStitchOptions_MasksFlowsThrough() {
    using var img1 = Solid(50, 50, 200, 100, 50);
    using var img2 = Solid(50, 50, 200, 100, 50);
    using var mask1 = new Image<L8>(50, 50);
    using var mask2 = new Image<L8>(50, 50);
    var options = new OpenCvStitchOptions {
      Masks = [mask1, mask2]
    };
    Assert.That(options.Masks, Is.Not.Null);
    Assert.That(options.Masks!.Count, Is.EqualTo(2));
    Assert.That(options.Masks[0], Is.SameAs(mask1));
    Assert.That(options.Masks[1], Is.SameAs(mask2));
  }

  [Test]
  public void OpenCvStitcher_RejectsMaskCountMismatch() {
    using var img1 = Solid(50, 50, 200, 100, 50);
    using var img2 = Solid(50, 50, 200, 100, 50);
    using var mask1 = new Image<L8>(50, 50);
    var options = new OpenCvStitchOptions { Masks = [mask1] };
    Assert.Throws<ArgumentException>(() => OpenCvPanoramaStitcher.Stitch([img1, img2], options));
  }

  [Test]
  public void OpenCvStitcher_RejectsMaskDimensionMismatch() {
    using var img1 = Solid(50, 50, 200, 100, 50);
    using var img2 = Solid(50, 50, 200, 100, 50);
    using var mask1 = new Image<L8>(50, 50);
    using var mask2 = new Image<L8>(40, 40);
    var options = new OpenCvStitchOptions { Masks = [mask1, mask2] };
    Assert.Throws<ArgumentException>(() => OpenCvPanoramaStitcher.Stitch([img1, img2], options));
  }

  [Test]
  public void TripodStitcher_RejectsMaskCountMismatch() {
    using var f1 = Solid(50, 50, 100, 100, 100);
    using var f2 = Solid(50, 50, 100, 100, 100);
    using var mask1 = new Image<L8>(50, 50);
    Assert.Throws<ArgumentException>(() => TripodPanoramaStitcher.Stitch([f1, f2], 0.3, [mask1]));
  }

  [Test]
  public void TripodStitcher_NullMasksBehavesAsBefore() {
    // Two identical frames stitched without masks — sanity check that the
    // mask-aware overload still produces output for the no-mask path.
    using var f1 = Checkerboard(80, 60, 4);
    using var f2 = Checkerboard(80, 60, 4);
    using var stitched = TripodPanoramaStitcher.Stitch([f1, f2], 0.3, masks: null);
    Assert.That(stitched.Width, Is.GreaterThanOrEqualTo(80));
    Assert.That(stitched.Height, Is.GreaterThanOrEqualTo(60));
  }

  [Test]
  public void TripodStitcher_AllZeroSecondMaskKeepsCanvasInOverlap() {
    // Frame 1 is solid red, frame 2 is solid blue with an all-zero mask.
    // In the overlap zone, the linear-feather weight on frame 2 should be
    // zero (mask kills it) → the overlap stays red, not feathered to blue.
    using var f1 = Solid(64, 32, 255, 0, 0);
    using var f2 = Solid(64, 32, 0, 0, 255);
    using var m1 = new Image<L8>(64, 32);
    using var m2 = new Image<L8>(64, 32);
    m1.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new L8(255);
      }
    });
    // m2 stays all zero → frame 2 contributes nothing per the mask weight.
    using var stitched = TripodPanoramaStitcher.Stitch([f1, f2], 0.5, [m1, m2]);

    // Sample a pixel firmly inside the overlap zone (right edge of canvas
    // ≈ stitched.Width - 32). Because the SSD search may shift placement
    // slightly we just sample dead-centre vertically and at a position
    // a few pixels left of the right edge of frame 1's footprint.
    var samples = new List<Rgba32>();
    stitched.ProcessPixelRows(a => {
      var midRow = a.GetRowSpan(a.Height / 2);
      for (var x = a.Width - 24; x < a.Width - 16; x++)
        samples.Add(midRow[x]);
    });
    foreach (var p in samples) {
      // The pixel should still be predominantly red (canvas wins because
      // frame 2's per-pixel weight got multiplied by zero).
      Assert.That(p.R, Is.GreaterThan(p.B),
        $"Overlap pixel should retain canvas red, got R={p.R} B={p.B}.");
    }
  }
}
