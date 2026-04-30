using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
public class Lut3DApplyTests {
  [Test]
  public void Apply_IdentityLut_LeavesPixelsWithinOneLsb() {
    var lut = BuildIdentity(size: 8);
    using var img = MakeGradient(8, 8);
    var beforeBytes = SnapshotBytes(img);

    Lut3D.Apply(img, lut, opacity: 1.0);

    var afterBytes = SnapshotBytes(img);
    for (var i = 0; i < beforeBytes.Length; i++)
      Assert.That(Math.Abs(beforeBytes[i] - afterBytes[i]), Is.LessThanOrEqualTo(1),
        $"channel {i} drifted by more than 1 LSB on identity LUT.");
  }

  [Test]
  public void Apply_OpacityZero_LeavesInputUnchanged() {
    // A non-identity LUT (invert) — opacity 0 must keep the input.
    var lut = BuildInvert(size: 4);
    using var img = MakeGradient(4, 4);
    var before = SnapshotBytes(img);

    Lut3D.Apply(img, lut, opacity: 0.0);

    Assert.That(SnapshotBytes(img), Is.EqualTo(before));
  }

  [Test]
  public void Apply_NonIdentity_TransformsKnownSamplePoint() {
    // 2-cube LUT that swaps R and B at every vertex. Mid-grey input
    // should produce mid-grey output (R==B==G is the symmetric point);
    // a pure-red input should land on pure-blue.
    var lut = BuildRBSwap();
    using var img = new Image<Rgba32>(2, 1);
    img[0, 0] = new Rgba32(255, 0, 0, 255);
    img[1, 0] = new Rgba32(128, 128, 128, 255);

    Lut3D.Apply(img, lut, opacity: 1.0);

    var pure = img[0, 0];
    var grey = img[1, 0];
    Assert.Multiple(() => {
      Assert.That(pure.R, Is.EqualTo(0));
      Assert.That(pure.G, Is.EqualTo(0));
      Assert.That(pure.B, Is.EqualTo(255));
      // Mid-grey: R==G==B → swap is a no-op for the symmetric diagonal.
      Assert.That(Math.Abs(grey.R - 128), Is.LessThanOrEqualTo(1));
      Assert.That(Math.Abs(grey.G - 128), Is.LessThanOrEqualTo(1));
      Assert.That(Math.Abs(grey.B - 128), Is.LessThanOrEqualTo(1));
    });
  }

  private static Lut3D BuildIdentity(int size) {
    var table = new float[size * size * size * 3];
    var nMinus1 = size - 1f;
    for (var b = 0; b < size; b++)
      for (var g = 0; g < size; g++)
        for (var r = 0; r < size; r++) {
          var idx = ((b * size + g) * size + r) * 3;
          table[idx]     = r / nMinus1;
          table[idx + 1] = g / nMinus1;
          table[idx + 2] = b / nMinus1;
        }
    return new(size, table);
  }

  private static Lut3D BuildInvert(int size) {
    var table = new float[size * size * size * 3];
    var nMinus1 = size - 1f;
    for (var b = 0; b < size; b++)
      for (var g = 0; g < size; g++)
        for (var r = 0; r < size; r++) {
          var idx = ((b * size + g) * size + r) * 3;
          table[idx]     = 1f - r / nMinus1;
          table[idx + 1] = 1f - g / nMinus1;
          table[idx + 2] = 1f - b / nMinus1;
        }
    return new(size, table);
  }

  private static Lut3D BuildRBSwap() {
    const int size = 2;
    var table = new float[size * size * size * 3];
    var nMinus1 = size - 1f;
    for (var b = 0; b < size; b++)
      for (var g = 0; g < size; g++)
        for (var r = 0; r < size; r++) {
          var idx = ((b * size + g) * size + r) * 3;
          table[idx]     = b / nMinus1;
          table[idx + 1] = g / nMinus1;
          table[idx + 2] = r / nMinus1;
        }
    return new(size, table);
  }

  private static Image<Rgba32> MakeGradient(int w, int h) {
    var img = new Image<Rgba32>(w, h);
    img.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var r = (byte)(x * 255 / Math.Max(1, w - 1));
          var g = (byte)(y * 255 / Math.Max(1, h - 1));
          var b = (byte)((x + y) * 255 / Math.Max(1, w + h - 2));
          row[x] = new Rgba32(r, g, b, 255);
        }
      }
    });
    return img;
  }

  private static byte[] SnapshotBytes(Image<Rgba32> img) {
    var bytes = new byte[img.Width * img.Height * 4];
    var i = 0;
    img.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          bytes[i++] = row[x].R;
          bytes[i++] = row[x].G;
          bytes[i++] = row[x].B;
          bytes[i++] = row[x].A;
        }
      }
    });
    return bytes;
  }
}
