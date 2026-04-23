using PhotoManager.Core.Previews;

namespace PhotoManager.Tests.Unit.Previews;

[TestFixture]
public class RawPreviewExtractorTests {
  [Test]
  public void FindLargestJpeg_NoJpegPresent_ReturnsNull() {
    var data = new byte[] { 0x00, 0x11, 0x22, 0xFF, 0xD8, 0x00 }; // partial SOI, not a real JPEG
    Assert.That(RawPreviewExtractor.FindLargestJpeg(data), Is.Null);
  }

  [Test]
  public void FindLargestJpeg_SingleJpeg_ReturnsExactBytes() {
    var jpeg = BuildJpeg(256);
    var wrapper = Concat(new byte[] { 0x01, 0x02 }, jpeg, new byte[] { 0xDE, 0xAD });

    var result = RawPreviewExtractor.FindLargestJpeg(wrapper);

    Assert.That(result, Is.Not.Null);
    Assert.That(result, Is.EqualTo(jpeg).AsCollection);
  }

  [Test]
  public void FindLargestJpeg_MultipleJpegs_PrefersLargest() {
    var thumbnail = BuildJpeg(64);
    var preview = BuildJpeg(4096);
    var tiffWrapper = Concat(
      new byte[] { 0x49, 0x49, 0x2A, 0x00 }, // fake TIFF header
      thumbnail,
      new byte[] { 0x00, 0x00 },
      preview,
      new byte[] { 0xFF, 0xFF }
    );

    var result = RawPreviewExtractor.FindLargestJpeg(tiffWrapper);

    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Length, Is.EqualTo(preview.Length));
    Assert.That(result, Is.EqualTo(preview).AsCollection);
  }

  [Test]
  public void FindLargestJpeg_NestedThumbnail_ReturnsOuterJpegWhole() {
    // Outer JPEG contains an inner thumbnail. The naive single-EOI scanner
    // would truncate at the inner EOI; depth tracking should return the
    // outer span intact.
    var innerThumbnail = BuildJpeg(128);
    var outer = BuildJpegWithPayload(PadTo(innerThumbnail, 2048));

    var result = RawPreviewExtractor.FindLargestJpeg(outer);

    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Length, Is.EqualTo(outer.Length), "outer JPEG should not be truncated at inner EOI");
  }

  [Test]
  public async Task ExtractLargestJpegAsync_NonexistentFile_ReturnsNull() {
    var file = new FileInfo(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".nef"));

    var result = await RawPreviewExtractor.ExtractLargestJpegAsync(file);

    Assert.That(result, Is.Null);
  }

  [Test]
  public async Task ExtractLargestJpegAsync_RealFile_PicksEmbeddedJpeg() {
    var jpeg = BuildJpeg(512);
    var wrapper = Concat(new byte[] { 0xCA, 0xFE }, jpeg, new byte[] { 0xBA, 0xBE });
    var file = new FileInfo(Path.Combine(Path.GetTempPath(), "fake-raw-" + Guid.NewGuid().ToString("N") + ".nef"));
    await File.WriteAllBytesAsync(file.FullName, wrapper);

    try {
      var result = await RawPreviewExtractor.ExtractLargestJpegAsync(file);

      Assert.That(result, Is.Not.Null);
      Assert.That(result, Is.EqualTo(jpeg).AsCollection);
    } finally {
      file.Delete();
    }
  }

  // --- helpers: construct byte sequences that look enough like JPEGs for the
  // byte-scanner, without invoking a real encoder (keeps tests dependency-free).

  private static byte[] BuildJpeg(int innerFillBytes) {
    // SOI FF D8, filler marker FF E0 (so trailing FF keeps scanner happy),
    // then <innerFillBytes> bytes of non-0xFF payload, then EOI FF D9.
    var result = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0 };
    for (var i = 0; i < innerFillBytes; i++)
      result.Add((byte)(i % 0xFE)); // avoid 0xFF inside
    result.Add(0xFF);
    result.Add(0xD9);
    return result.ToArray();
  }

  private static byte[] BuildJpegWithPayload(byte[] payload) {
    var result = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0 };
    result.AddRange(payload);
    result.Add(0xFF);
    result.Add(0xD9);
    return result.ToArray();
  }

  private static byte[] PadTo(byte[] payload, int totalLength) {
    var buf = new byte[Math.Max(payload.Length, totalLength)];
    Array.Copy(payload, buf, payload.Length);
    // Fill remainder with values that aren't 0xFF to avoid fake markers.
    for (var i = payload.Length; i < buf.Length; i++)
      buf[i] = (byte)(i % 0xFE);
    return buf;
  }

  private static byte[] Concat(params byte[][] parts) {
    var len = parts.Sum(p => p.Length);
    var buf = new byte[len];
    var offset = 0;
    foreach (var p in parts) {
      Buffer.BlockCopy(p, 0, buf, offset, p.Length);
      offset += p.Length;
    }
    return buf;
  }
}
