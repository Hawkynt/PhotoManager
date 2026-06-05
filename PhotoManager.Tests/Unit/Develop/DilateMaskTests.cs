using Hawkynt.PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Develop;

[TestFixture]
public class DilateMaskTests {
  /// <summary>
  /// Naive 8-neighbour dilation used as the reference implementation.
  /// One iteration expands every set pixel to its 3x3 neighbourhood;
  /// repeated <paramref name="radius"/> times to match the old behaviour.
  /// </summary>
  private static Image<Rgba32> NaiveDilate(Image<Rgba32> mask, int radius) {
    if (radius < 1)
      return mask.Clone();
    var w = mask.Width;
    var h = mask.Height;
    var result = mask.Clone();
    var snap = new byte[w * h];
    for (var iter = 0; iter < radius; iter++) {
      result.ProcessPixelRows(a => {
        for (var y = 0; y < h; y++) {
          var row = a.GetRowSpan(y);
          for (var x = 0; x < w; x++)
            snap[y * w + x] = (byte)(row[x].R >= 128 ? 1 : 0);
        }
      });
      result.ProcessPixelRows(a => {
        for (var y = 0; y < h; y++) {
          var row = a.GetRowSpan(y);
          for (var x = 0; x < w; x++) {
            if (snap[y * w + x] == 1)
              continue;
            var hit = false;
            for (var dy = -1; dy <= 1 && !hit; dy++) {
              var ny = y + dy;
              if (ny < 0 || ny >= h) continue;
              for (var dx = -1; dx <= 1; dx++) {
                if (dx == 0 && dy == 0) continue;
                var nx = x + dx;
                if (nx < 0 || nx >= w) continue;
                if (snap[ny * w + nx] == 1) { hit = true; break; }
              }
            }
            if (hit)
              row[x] = new Rgba32((byte)255, (byte)0, (byte)0, (byte)200);
          }
        }
      });
    }
    return result;
  }

  private static Image<Rgba32> CreateMask(int w, int h, params (int X, int Y)[] setPixels) {
    var img = new Image<Rgba32>(w, h, new Rgba32(0, 0, 0, 0));
    img.ProcessPixelRows(a => {
      foreach (var (px, py) in setPixels) {
        if (px >= 0 && px < w && py >= 0 && py < h)
          a.GetRowSpan(py)[px] = new Rgba32(255, 0, 0, 200);
      }
    });
    return img;
  }

  private static bool IsSet(Image<Rgba32> img, int x, int y) {
    var set = false;
    img.ProcessPixelRows(a => {
      set = a.GetRowSpan(y)[x].R >= 128;
    });
    return set;
  }

  [Test]
  public void DilateMask_Radius0_ReturnsCopy() {
    using var mask = CreateMask(10, 10, (5, 5));
    using var result = AutoScratchPipeline.DilateMask(mask, 0);
    Assert.That(IsSet(result, 5, 5), Is.True);
    Assert.That(IsSet(result, 4, 5), Is.False);
  }

  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(5)]
  public void DilateMask_SinglePixel_MatchesNaive(int radius) {
    using var mask = CreateMask(30, 30, (15, 15));
    using var separable = AutoScratchPipeline.DilateMask(mask, radius);
    using var naive = NaiveDilate(mask, radius);
    AssertMasksEqual(separable, naive, $"radius={radius}, single pixel at (15,15)");
  }

  [TestCase(1)]
  [TestCase(2)]
  [TestCase(4)]
  public void DilateMask_MultiplePixels_MatchesNaive(int radius) {
    using var mask = CreateMask(40, 40, (5, 5), (20, 10), (30, 30), (0, 0), (39, 39));
    using var separable = AutoScratchPipeline.DilateMask(mask, radius);
    using var naive = NaiveDilate(mask, radius);
    AssertMasksEqual(separable, naive, $"radius={radius}, scattered pixels");
  }

  [TestCase(1)]
  [TestCase(3)]
  public void DilateMask_HorizontalLine_MatchesNaive(int radius) {
    var pixels = Enumerable.Range(5, 20).Select(x => (x, 15)).ToArray();
    using var mask = CreateMask(40, 30, pixels);
    using var separable = AutoScratchPipeline.DilateMask(mask, radius);
    using var naive = NaiveDilate(mask, radius);
    AssertMasksEqual(separable, naive, $"radius={radius}, horizontal line");
  }

  [TestCase(1)]
  [TestCase(3)]
  public void DilateMask_VerticalLine_MatchesNaive(int radius) {
    var pixels = Enumerable.Range(5, 20).Select(y => (15, y)).ToArray();
    using var mask = CreateMask(30, 40, pixels);
    using var separable = AutoScratchPipeline.DilateMask(mask, radius);
    using var naive = NaiveDilate(mask, radius);
    AssertMasksEqual(separable, naive, $"radius={radius}, vertical line");
  }

  [Test]
  public void DilateMask_CornerPixel_MatchesNaive() {
    using var mask = CreateMask(20, 20, (0, 0));
    using var separable = AutoScratchPipeline.DilateMask(mask, 3);
    using var naive = NaiveDilate(mask, 3);
    AssertMasksEqual(separable, naive, "radius=3, corner pixel at (0,0)");
  }

  [Test]
  public void DilateMask_EmptyMask_StaysEmpty() {
    using var mask = new Image<Rgba32>(20, 20, new Rgba32(0, 0, 0, 0));
    using var result = AutoScratchPipeline.DilateMask(mask, 5);
    var anySet = false;
    result.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height && !anySet; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          if (row[x].R >= 128) { anySet = true; break; }
      }
    });
    Assert.That(anySet, Is.False, "Dilating an empty mask should produce an empty mask");
  }

  [Test]
  public void DilateMask_LargeRadius_MatchesNaive() {
    using var mask = CreateMask(50, 50, (25, 25));
    using var separable = AutoScratchPipeline.DilateMask(mask, 8);
    using var naive = NaiveDilate(mask, 8);
    AssertMasksEqual(separable, naive, "radius=8, single pixel at (25,25)");
  }

  private static void AssertMasksEqual(Image<Rgba32> actual, Image<Rgba32> expected, string context) {
    Assert.That(actual.Width, Is.EqualTo(expected.Width), $"Width mismatch: {context}");
    Assert.That(actual.Height, Is.EqualTo(expected.Height), $"Height mismatch: {context}");
    var mismatches = new List<string>();
    actual.ProcessPixelRows(expected, (aAcc, eAcc) => {
      for (var y = 0; y < aAcc.Height; y++) {
        var aRow = aAcc.GetRowSpan(y);
        var eRow = eAcc.GetRowSpan(y);
        for (var x = 0; x < aRow.Length; x++) {
          var aSet = aRow[x].R >= 128;
          var eSet = eRow[x].R >= 128;
          if (aSet != eSet)
            mismatches.Add($"({x},{y}): separable={aSet}, naive={eSet}");
        }
      }
    });
    Assert.That(mismatches, Is.Empty,
      $"Pixel-for-pixel mismatch ({context}): {string.Join("; ", mismatches.Take(20))}");
  }
}
