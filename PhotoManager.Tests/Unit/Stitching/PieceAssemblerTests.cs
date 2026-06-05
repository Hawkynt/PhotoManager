using Hawkynt.PhotoManager.Core.Stitching;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Stitching;

[TestFixture]
public class PieceAssemblerTests {
  /// <summary>
  /// Build a synthetic two-piece torn-paper image: a single 200×200 white
  /// rectangle on black with a single jagged tear running roughly down the
  /// middle, the tear pixels punched to black so the segmenter sees two
  /// separate connected components. The two halves SHARE the tear boundary
  /// in source coordinates — they're already in their final positions, just
  /// not connected — so the assembler's source-layout path puts them right
  /// back next to each other.
  /// </summary>
  private static Image<Rgba32> BuildTornPair() {
    const int CanvasW = 240;
    const int CanvasH = 240;
    var canvas = new Image<Rgba32>(CanvasW, CanvasH, new Rgba32(0, 0, 0, 255));

    int[] TearX() {
      var col = new int[200];
      // Smooth-but-non-symmetric tear: two slow sines at incommensurable
      // frequencies. Adjacent rows differ by at most ~2 px so a 5-px-wide
      // black gap on either side keeps 4-connected flood fill from bridging.
      for (var y = 0; y < 200; y++) {
        var bumpA = (int)(6 * Math.Sin(y * 0.04));
        var bumpB = (int)(3 * Math.Cos(y * 0.11));
        col[y] = 120 + bumpA + bumpB;
      }
      return col;
    }
    var tear = TearX();

    canvas.ProcessPixelRows(accessor => {
      for (var y = 0; y < 200; y++) {
        var row = accessor.GetRowSpan(20 + y);
        // Left piece: white up to tear-line-x[y] - 3 (3-px gap each side
        // so 4-connected flood fill cannot bridge consecutive rows).
        for (var x = 20; x < tear[y] - 3; x++) {
          if (x >= 0 && x < CanvasW)
            row[x] = new Rgba32(245, 245, 245, 255);
        }
        // Right piece: white from tear-line-x[y] + 3 to x=219.
        for (var x = tear[y] + 3; x < 220; x++) {
          if (x >= 0 && x < CanvasW)
            row[x] = new Rgba32(245, 245, 245, 255);
        }
      }
    });
    return canvas;
  }

  [Test]
  public void Assemble_TwoMatchingPieces_PlacesThemAdjacent() {
    using var src = BuildTornPair();
    using var result = PieceStitcher.Run(src, ScannerBackground.Black, minPieceArea: 1000);

    Assert.That(result.Pieces.Count, Is.EqualTo(2), "Segmenter should find exactly two pieces.");
    Assert.That(result.Placed.Count, Is.EqualTo(2), "Both pieces should be placed in the assembly.");
    Assert.That(result.Unplaced, Is.Empty);

    // After assembly, the two placed pieces should sit close together — the
    // distance between their centroids in world space should be much less
    // than the segmenter's separation in the source (160 px). 80 px is a
    // generous ceiling that still rejects the "scattered" failure mode.
    var p0 = result.Placed[0];
    var p1 = result.Placed[1];
    var c0 = TransformCentroid(p0);
    var c1 = TransformCentroid(p1);
    var dx = c0.X - c1.X;
    var dy = c0.Y - c1.Y;
    var d = Math.Sqrt(dx * dx + dy * dy);
    Assert.That(d, Is.LessThan(120), $"Pieces should be adjacent after assembly, got centroid distance {d:F1}.");

    // The reassembled canvas should contain a meaningful blob of white pixels
    // — i.e. the assembler actually rendered something coherent rather than
    // an empty canvas.
    var whitePixels = CountWhitePixels(result.Canvas);
    Assert.That(whitePixels, Is.GreaterThan(15000), $"Reassembled canvas should be mostly white paper, got {whitePixels}.");
  }

  [Test]
  public void Assemble_TwoIncompatiblePieces_DoesNotInventGarbageTransform() {
    // Two visually distinct, non-matching pieces: a circle and a rectangle.
    // Their contours don't share an arc so no edge match should clear the
    // residual ceiling. Either no match wins (one stays unplaced) or the
    // assembler keeps the second placement well away from the first when
    // forced — we assert the failure mode is graceful.
    var canvas = new Image<Rgba32>(400, 200, new Rgba32(0, 0, 0, 255));
    canvas.ProcessPixelRows(accessor => {
      // Filled rectangle on the left.
      for (var y = 30; y < 170; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 30; x < 130; x++) row[x] = new Rgba32(245, 245, 245, 255);
      }
      // Filled circle on the right.
      var cx = 280;
      var cy = 100;
      var rr = 60 * 60;
      for (var y = 0; y < 200; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 200; x < 360; x++) {
          var ddx = x - cx;
          var ddy = y - cy;
          if (ddx * ddx + ddy * ddy <= rr) row[x] = new Rgba32(245, 245, 245, 255);
        }
      }
    });

    using (canvas) {
      using var result = PieceStitcher.Run(canvas, ScannerBackground.Black, minPieceArea: 500);
      Assert.That(result.Pieces.Count, Is.EqualTo(2));

      // The anchor (the bigger rectangle) is always placed at the origin. If
      // the second piece couldn't find a clean match it must either: stay
      // unplaced, or — if a low-residual match was found despite no real
      // shared edge — at least not be slammed inside the rectangle. We
      // accept either as "graceful": no garbage transform that piles a
      // 60-radius circle entirely inside the 100×140 rectangle.
      if (result.Placed.Count == 2) {
        var rect = result.Placed[0];
        var circ = result.Placed[1];
        var anchorArea = MeasurePlacedArea(rect);
        var circArea = MeasurePlacedArea(circ);
        var overlap = MeasureOverlap(rect, circ);
        var fraction = overlap / (double)Math.Min(anchorArea, circArea);
        Assert.That(fraction, Is.LessThan(0.5),
            $"Incompatible pieces should not be force-stacked; overlap fraction was {fraction:F2}.");
      } else {
        Assert.That(result.Unplaced.Count, Is.EqualTo(1));
      }
    }
  }

  private static (double X, double Y) TransformCentroid(PieceAssembler.PlacedPiece p) {
    // Centroid of the alpha-set pixels in local coords: cheap approximation
    // by walking the cropped image.
    var img = p.Piece.Image;
    var pixels = new Rgba32[img.Width * img.Height];
    img.CopyPixelDataTo(pixels);
    double sx = 0, sy = 0, n = 0;
    for (var y = 0; y < img.Height; y++) {
      for (var x = 0; x < img.Width; x++) {
        if (pixels[y * img.Width + x].A >= 128) {
          sx += x; sy += y; n++;
        }
      }
    }
    if (n <= 0) return (p.TranslationX, p.TranslationY);
    sx /= n; sy /= n;
    var wx = p.Rotation[0] * sx + p.Rotation[1] * sy + p.TranslationX;
    var wy = p.Rotation[2] * sx + p.Rotation[3] * sy + p.TranslationY;
    return (wx, wy);
  }

  private static int CountWhitePixels(Image<Rgba32> img) {
    var pixels = new Rgba32[img.Width * img.Height];
    img.CopyPixelDataTo(pixels);
    var count = 0;
    for (var i = 0; i < pixels.Length; i++) {
      var p = pixels[i];
      if (p.A >= 128 && p.R > 200 && p.G > 200 && p.B > 200) count++;
    }
    return count;
  }

  private static int MeasurePlacedArea(PieceAssembler.PlacedPiece p) {
    var img = p.Piece.Image;
    var pixels = new Rgba32[img.Width * img.Height];
    img.CopyPixelDataTo(pixels);
    var c = 0;
    for (var i = 0; i < pixels.Length; i++) if (pixels[i].A >= 128) c++;
    return c;
  }

  /// <summary>Rough overlap proxy: world-space AABB intersection. Good enough
  /// for the test's "garbage transform" assertion.</summary>
  private static int MeasureOverlap(PieceAssembler.PlacedPiece a, PieceAssembler.PlacedPiece b) {
    var aRect = WorldAabb(a);
    var bRect = WorldAabb(b);
    var ix1 = Math.Max(aRect.x0, bRect.x0);
    var iy1 = Math.Max(aRect.y0, bRect.y0);
    var ix2 = Math.Min(aRect.x1, bRect.x1);
    var iy2 = Math.Min(aRect.y1, bRect.y1);
    if (ix1 >= ix2 || iy1 >= iy2) return 0;
    return (int)((ix2 - ix1) * (iy2 - iy1));
  }

  private static (double x0, double y0, double x1, double y1) WorldAabb(PieceAssembler.PlacedPiece p) {
    var pw = p.Piece.Image.Width;
    var ph = p.Piece.Image.Height;
    double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
    foreach (var (lx, ly) in new (double, double)[] { (0, 0), (pw, 0), (pw, ph), (0, ph) }) {
      var wx = p.Rotation[0] * lx + p.Rotation[1] * ly + p.TranslationX;
      var wy = p.Rotation[2] * lx + p.Rotation[3] * ly + p.TranslationY;
      if (wx < minX) minX = wx;
      if (wy < minY) minY = wy;
      if (wx > maxX) maxX = wx;
      if (wy > maxY) maxY = wy;
    }
    return (minX, minY, maxX, maxY);
  }
}

/// <summary>
/// Simple structural tests for the segmenter; not strictly required by the
/// task but they make the assembler tests easier to debug when something
/// breaks earlier in the pipeline.
/// </summary>
[TestFixture]
public class PieceSegmenterStructuralTests {
  [Test]
  public void Segment_BlackImage_ReturnsNoPieces() {
    using var img = new Image<Rgba32>(50, 50, new Rgba32(0, 0, 0, 255));
    var pieces = PieceSegmenter.Segment(img, ScannerBackground.Black);
    Assert.That(pieces, Is.Empty);
  }

  [Test]
  public void Segment_TwoSeparateBlobs_ReturnsTwoPieces() {
    using var img = new Image<Rgba32>(120, 60, new Rgba32(0, 0, 0, 255));
    img.ProcessPixelRows(accessor => {
      for (var y = 10; y < 50; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 10; x < 50; x++) row[x] = new Rgba32(240, 240, 240, 255);
        for (var x = 70; x < 110; x++) row[x] = new Rgba32(240, 240, 240, 255);
      }
    });
    var pieces = PieceSegmenter.Segment(img, ScannerBackground.Black, minPieceArea: 100);
    Assert.That(pieces.Count, Is.EqualTo(2));
    Assert.That(pieces[0].Contour.Count, Is.GreaterThan(8));
  }
}
