using System.Text;
using FileFormat.JpegArchive;

namespace PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class PngChunkSurgeryTests {
  /// <summary>
  /// Minimal valid-shape PNG: signature + IHDR + IDAT + IEND. We don't care
  /// about decoding the image; we just need the chunk walker to see well-
  /// formed length/type/CRC entries.
  /// </summary>
  private static byte[] MinimalPng() {
    using var ms = new MemoryStream();
    // Signature
    ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    // IHDR: 13-byte payload (just padding here; CRC doesn't have to match what a decoder expects for our purposes)
    WriteChunk(ms, "IHDR", new byte[13]);
    // IDAT: one-byte payload (ignored by our chunk walker)
    WriteChunk(ms, "IDAT", new byte[] { 0x00 });
    // IEND: no payload
    WriteChunk(ms, "IEND", Array.Empty<byte>());
    return ms.ToArray();
  }

  private static void WriteChunk(Stream s, string type, byte[] data) {
    var lenBytes = new byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)data.Length);
    var typeBytes = Encoding.ASCII.GetBytes(type);
    var crcInput = new byte[typeBytes.Length + data.Length];
    Array.Copy(typeBytes, crcInput, typeBytes.Length);
    Array.Copy(data, 0, crcInput, typeBytes.Length, data.Length);
    var crc = PngChunkSurgeryAccess.ComputeCrc32(crcInput);
    var crcBytes = new byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);

    s.Write(lenBytes, 0, 4);
    s.Write(typeBytes, 0, 4);
    s.Write(data, 0, data.Length);
    s.Write(crcBytes, 0, 4);
  }

  [Test]
  public void RoundTrip_InsertsXmp_AndReadsItBack() {
    var xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">hi</x:xmpmeta>");
    var png = PngChunkSurgery.ReplaceXmpChunk(MinimalPng(), xml);

    var readBack = PngChunkSurgery.TryReadXmpChunk(png);
    Assert.That(readBack, Is.Not.Null);
    Assert.That(readBack, Is.EqualTo(xml).AsCollection);
  }

  [Test]
  public void ReplaceXmpChunk_RemovesExistingXmpBeforeInserting() {
    var first = PngChunkSurgery.ReplaceXmpChunk(MinimalPng(), Encoding.UTF8.GetBytes("<old/>"));
    var second = PngChunkSurgery.ReplaceXmpChunk(first, Encoding.UTF8.GetBytes("<new/>"));

    var xmpKeywordBytes = Encoding.ASCII.GetBytes("XML:com.adobe.xmp");
    var matches = CountOccurrences(second, xmpKeywordBytes);

    Assert.That(matches, Is.EqualTo(1), "exactly one XMP iTXt chunk after replacement");
    Assert.That(PngChunkSurgery.TryReadXmpChunk(second), Is.EqualTo(Encoding.UTF8.GetBytes("<new/>")).AsCollection);
  }

  [Test]
  public void TryReadXmpChunk_NoXmp_ReturnsNull() {
    Assert.That(PngChunkSurgery.TryReadXmpChunk(MinimalPng()), Is.Null);
  }

  [Test]
  public void ReplaceXmpChunk_NotPng_Throws() {
    var garbage = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x00, 0x00, 0x00 };
    Assert.Throws<InvalidDataException>(() =>
      PngChunkSurgery.ReplaceXmpChunk(garbage, Encoding.UTF8.GetBytes("<xmp/>"))
    );
  }

  [Test]
  public void ReplaceXmpChunk_XmpInsertedAfterIHDR() {
    var png = PngChunkSurgery.ReplaceXmpChunk(MinimalPng(), Encoding.UTF8.GetBytes("<xmp/>"));

    // After signature (8 bytes) + IHDR (4+4+13+4=25 bytes) = offset 33.
    // Next chunk type should be iTXt at offset 33+4 = 37.
    var typeAtITXt = Encoding.ASCII.GetString(png, 37, 4);
    Assert.That(typeAtITXt, Is.EqualTo("iTXt"));
  }

  private static int CountOccurrences(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) {
    var count = 0;
    for (var i = 0; i <= haystack.Length - needle.Length; i++) {
      if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
        count++;
    }
    return count;
  }
}

/// <summary>
/// Test-only accessor for the internal CRC-32 helper so synthetic PNGs
/// assembled here have valid checksums (otherwise the chunk walker would
/// still parse them, but real tools wouldn't).
/// </summary>
internal static class PngChunkSurgeryAccess {
  public static uint ComputeCrc32(byte[] data) {
    // Replicate the PngChunkSurgery.ComputeCrc32 logic so tests don't need
    // InternalsVisibleTo across repos.
    var crc = 0xFFFFFFFFu;
    foreach (var b in data) {
      var idx = (crc ^ b) & 0xFF;
      var c = idx;
      for (var k = 0; k < 8; k++)
        c = ((c & 1) != 0) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
      crc = c ^ (crc >> 8);
    }
    return crc ^ 0xFFFFFFFFu;
  }
}
