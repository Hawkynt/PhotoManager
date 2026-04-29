using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Library;

/// <summary>
/// Classic 64-bit pHash. The pipeline is:
/// resize to 32×32 grayscale, run a 2D DCT-II on the luminance matrix,
/// take the top-left 8×8 low-frequency block (skipping the DC term when
/// computing the median so a constant offset doesn't bias the output),
/// then emit one bit per coefficient — 1 if the coefficient is at least
/// the median, else 0. Two hashes are similar iff their Hamming distance
/// (popcount of XOR) is small.
///
/// Robust to JPEG re-encoding, mild scaling, and small color/exposure
/// shifts; insensitive to rotation by 90°+ (which is by design — that's
/// a different transform). 64 bits → up to ~6-bit Hamming distance is
/// "the same photo" in practice.
/// </summary>
public static class PerceptualHash {
  private const int PreSize = 32;
  private const int LowFreqSize = 8;

  // Pre-computed cosine table for the 32-point DCT-II. Sized PreSize × PreSize
  // so we can index by [k, n]; we only ever use the first LowFreqSize rows
  // when transforming, so half of the storage is wasted in exchange for
  // straight-line code.
  private static readonly float[,] CosineTable = BuildCosineTable();

  private static float[,] BuildCosineTable() {
    var t = new float[PreSize, PreSize];
    for (var k = 0; k < PreSize; k++) {
      for (var n = 0; n < PreSize; n++)
        t[k, n] = (float)Math.Cos(Math.PI * (2 * n + 1) * k / (2.0 * PreSize));
    }
    return t;
  }

  public static async Task<ulong> ComputeAsync(FileInfo imageFile, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(imageFile);
    if (!imageFile.Exists)
      throw new FileNotFoundException(imageFile.FullName);

    using var image = await Image.LoadAsync<Rgba32>(imageFile.FullName, cancellationToken);
    return Compute(image);
  }

  internal static ulong Compute(Image<Rgba32> image) {
    using var working = image.Clone(c => c.Resize(PreSize, PreSize).Grayscale());

    var pixels = new float[PreSize * PreSize];
    working.ProcessPixelRows(accessor => {
      for (var y = 0; y < PreSize; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < PreSize; x++)
          pixels[y * PreSize + x] = row[x].R;
      }
    });

    var dct = ComputeDct2D(pixels);

    // Median across the 8×8 low-frequency block, excluding the DC coefficient
    // [0,0] which encodes the average brightness — keeping it would let two
    // unrelated images that happen to share a brightness fail to discriminate.
    var lowFreq = new float[LowFreqSize * LowFreqSize - 1];
    var idx = 0;
    for (var y = 0; y < LowFreqSize; y++) {
      for (var x = 0; x < LowFreqSize; x++) {
        if (x == 0 && y == 0)
          continue;
        lowFreq[idx++] = dct[y, x];
      }
    }
    Array.Sort(lowFreq);
    var median = (lowFreq[lowFreq.Length / 2 - 1] + lowFreq[lowFreq.Length / 2]) / 2f;

    ulong hash = 0;
    var bit = 0;
    for (var y = 0; y < LowFreqSize; y++) {
      for (var x = 0; x < LowFreqSize; x++) {
        if (dct[y, x] >= median)
          hash |= 1UL << bit;
        bit++;
      }
    }
    return hash;
  }

  /// <summary>Hamming distance between two 64-bit pHashes — number of bits that differ.</summary>
  public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

  // 2D DCT-II via two 1D passes. Only the first LowFreqSize rows/cols of
  // output are needed for the hash so we cap k at LowFreqSize on both passes.
  private static float[,] ComputeDct2D(float[] pixels) {
    var rowDct = new float[PreSize, LowFreqSize];
    for (var y = 0; y < PreSize; y++) {
      for (var k = 0; k < LowFreqSize; k++) {
        float sum = 0;
        for (var n = 0; n < PreSize; n++)
          sum += pixels[y * PreSize + n] * CosineTable[k, n];
        rowDct[y, k] = sum;
      }
    }

    var result = new float[LowFreqSize, LowFreqSize];
    for (var k = 0; k < LowFreqSize; k++) {
      for (var x = 0; x < LowFreqSize; x++) {
        float sum = 0;
        for (var n = 0; n < PreSize; n++)
          sum += rowDct[n, x] * CosineTable[k, n];
        result[k, x] = sum;
      }
    }
    return result;
  }
}
