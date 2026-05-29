using PhotoManager.Core.Segmentation;

namespace PhotoManager.Tests.Unit.Segmentation;

[TestFixture]
public class InpainterFeatherTests {
  /// <summary>
  /// Verify that the new two-pass distance transform produces the same
  /// Chebyshev distance values as the original per-pixel brute-force
  /// <see cref="OnnxInpainter.ComputeFeatherWeight"/> for a variety of
  /// synthetic masks.
  /// </summary>
  [Test]
  public void DistanceMap_MatchesBruteForce_SmallSquareMask() {
    // 20x20 mask with a solid 10x10 masked block in the centre.
    const int maskW = 20, maskH = 20, featherPx = 6;
    var mask = new float[maskW * maskH];
    for (var y = 5; y < 15; y++)
      for (var x = 5; x < 15; x++)
        mask[y * maskW + x] = 1f;

    AssertDistanceMapMatchesBruteForce(mask, maskW, maskH, featherPx);
  }

  [Test]
  public void DistanceMap_MatchesBruteForce_FullMask() {
    // Entire mask is active — every pixel should hit maxDist.
    const int maskW = 16, maskH = 16, featherPx = 6;
    var mask = new float[maskW * maskH];
    Array.Fill(mask, 1f);

    AssertDistanceMapMatchesBruteForce(mask, maskW, maskH, featherPx);
  }

  [Test]
  public void DistanceMap_MatchesBruteForce_EmptyMask() {
    // No masked pixels — distance map should be all zeros.
    const int maskW = 16, maskH = 16, featherPx = 6;
    var mask = new float[maskW * maskH];

    var distMap = OnnxInpainter.BuildChebyshevDistanceMap(mask, maskW, maskH, 0, 0, maskW, maskH, featherPx);

    Assert.That(distMap, Is.All.EqualTo(0));
  }

  [Test]
  public void DistanceMap_MatchesBruteForce_SinglePixelMask() {
    // One masked pixel at centre of a 13x13 grid.
    const int maskW = 13, maskH = 13, featherPx = 6;
    var mask = new float[maskW * maskH];
    mask[6 * maskW + 6] = 1f;

    AssertDistanceMapMatchesBruteForce(mask, maskW, maskH, featherPx);
  }

  [Test]
  public void DistanceMap_MatchesBruteForce_LShapedMask() {
    // L-shaped mask to exercise asymmetric distance propagation.
    const int maskW = 20, maskH = 20, featherPx = 6;
    var mask = new float[maskW * maskH];
    // Vertical bar from (3,3) to (3,16)
    for (var y = 3; y <= 16; y++) mask[y * maskW + 3] = 1f;
    // Horizontal bar from (3,16) to (16,16)
    for (var x = 3; x <= 16; x++) mask[16 * maskW + x] = 1f;

    AssertDistanceMapMatchesBruteForce(mask, maskW, maskH, featherPx);
  }

  [Test]
  public void DistanceMap_MatchesBruteForce_ScatteredPixels() {
    // Random-ish scattered mask pixels.
    const int maskW = 24, maskH = 24, featherPx = 6;
    var mask = new float[maskW * maskH];
    var rng = new Random(42); // deterministic seed
    for (var i = 0; i < mask.Length; i++)
      mask[i] = rng.NextDouble() > 0.6 ? 1f : 0f;

    AssertDistanceMapMatchesBruteForce(mask, maskW, maskH, featherPx);
  }

  [Test]
  public void DistanceMap_MatchesBruteForce_TileSubregion() {
    // Test with a sub-region offset (simulates a tile that doesn't
    // start at mask origin).
    const int maskW = 30, maskH = 30, featherPx = 6;
    var mask = new float[maskW * maskH];
    for (var y = 8; y < 25; y++)
      for (var x = 10; x < 28; x++)
        mask[y * maskW + x] = 1f;

    const int x0 = 5, y0 = 5, w = 20, h = 20;
    AssertDistanceMapMatchesBruteForce(mask, maskW, maskH, featherPx, x0, y0, w, h);
  }

  [Test]
  public void DistanceMap_MatchesBruteForce_EdgeMask() {
    // Mask touches all four edges of the grid.
    const int maskW = 12, maskH = 12, featherPx = 4;
    var mask = new float[maskW * maskH];
    // Top row
    for (var x = 0; x < maskW; x++) mask[x] = 1f;
    // Bottom row
    for (var x = 0; x < maskW; x++) mask[(maskH - 1) * maskW + x] = 1f;
    // Left column
    for (var y = 0; y < maskH; y++) mask[y * maskW] = 1f;
    // Right column
    for (var y = 0; y < maskH; y++) mask[y * maskW + maskW - 1] = 1f;

    AssertDistanceMapMatchesBruteForce(mask, maskW, maskH, featherPx);
  }

  /// <summary>
  /// Helper: builds the distance map via the new two-pass transform,
  /// then compares every masked pixel against the brute-force method.
  /// </summary>
  private static void AssertDistanceMapMatchesBruteForce(
    float[] mask, int maskW, int maskH, int featherPx,
    int x0 = 0, int y0 = 0, int w = -1, int h = -1) {
    if (w < 0) w = maskW;
    if (h < 0) h = maskH;

#pragma warning disable CS0618 // ComputeFeatherWeight is Obsolete — intentional use for verification
    var distMap = OnnxInpainter.BuildChebyshevDistanceMap(mask, maskW, maskH, x0, y0, w, h, featherPx);

    for (var y = 0; y < h; y++) {
      for (var x = 0; x < w; x++) {
        var mx = x0 + x;
        var my = y0 + y;

        // Skip pixels outside the mask array bounds or unmasked.
        if (mx < 0 || mx >= maskW || my < 0 || my >= maskH)
          continue;
        if (mask[my * maskW + mx] <= 0)
          continue;

        var expected = OnnxInpainter.ComputeFeatherWeight(mask, maskW, mx, my, featherPx);
        var actual = Math.Min(1f, (float)distMap[y * w + x] / featherPx);
        Assert.That(actual, Is.EqualTo(expected),
          $"Mismatch at tile ({x},{y}) / mask ({mx},{my}): " +
          $"distMap={distMap[y * w + x]}, expected weight={expected}, actual weight={actual}");
      }
    }
#pragma warning restore CS0618
  }
}
