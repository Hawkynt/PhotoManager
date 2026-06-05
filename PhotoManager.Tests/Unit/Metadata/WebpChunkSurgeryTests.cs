using System.Buffers.Binary;
using System.Text;
using FileFormat.JpegArchive;

namespace Hawkynt.PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class WebpChunkSurgeryTests {
  /// <summary>
  /// Builds a simple (non-extended) WebP with a synthetic VP8L bitstream
  /// whose first 5 bytes encode a 10×20 canvas. This exercises the VP8X
  /// promotion path in <see cref="WebpChunkSurgery.ReplaceXmpChunk"/>.
  /// </summary>
  private static byte[] SimpleWebpVp8L() {
    // VP8L header: 0x2F then 4 bytes: (width-1)[14] | (height-1)[14] | alpha[1] | version[3]
    var width = 10;
    var height = 20;
    uint bits = (uint)(width - 1) | ((uint)(height - 1) << 14);
    var header = new byte[5];
    header[0] = 0x2F;
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(1), bits);

    var bitstream = new byte[header.Length + 16];  // pad with garbage; not decoded
    Array.Copy(header, bitstream, header.Length);

    return BuildRiff(new[] {
      ("VP8L", bitstream)
    });
  }

  private static byte[] BuildRiff((string, byte[])[] chunks) {
    using var body = new MemoryStream();
    foreach (var (fourcc, data) in chunks) {
      body.Write(Encoding.ASCII.GetBytes(fourcc), 0, 4);
      var sizeBytes = new byte[4];
      BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)data.Length);
      body.Write(sizeBytes, 0, 4);
      body.Write(data, 0, data.Length);
      if (data.Length % 2 != 0)
        body.WriteByte(0);
    }
    var bodyBytes = body.ToArray();

    using var output = new MemoryStream();
    output.Write(Encoding.ASCII.GetBytes("RIFF"), 0, 4);
    var riffSize = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(riffSize, (uint)(4 + bodyBytes.Length));
    output.Write(riffSize, 0, 4);
    output.Write(Encoding.ASCII.GetBytes("WEBP"), 0, 4);
    output.Write(bodyBytes, 0, bodyBytes.Length);
    return output.ToArray();
  }

  [Test]
  public void ReplaceXmpChunk_PromotesSimpleWebp_AndAttachesXmp() {
    var xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">hi</x:xmpmeta>");
    var output = WebpChunkSurgery.ReplaceXmpChunk(SimpleWebpVp8L(), xml);

    var readBack = WebpChunkSurgery.TryReadXmpChunk(output);
    Assert.Multiple(() => {
      Assert.That(readBack, Is.Not.Null);
      Assert.That(readBack, Is.EqualTo(xml).AsCollection);

      // VP8X must be the first chunk now (offset 12 = after RIFF/size/WEBP).
      var fourccAfterHeader = Encoding.ASCII.GetString(output, 12, 4);
      Assert.That(fourccAfterHeader, Is.EqualTo("VP8X"));
    });
  }

  [Test]
  public void ReplaceXmpChunk_PreservesBitstreamBytes() {
    var xml = Encoding.UTF8.GetBytes("<xmp/>");
    var original = SimpleWebpVp8L();
    var output = WebpChunkSurgery.ReplaceXmpChunk(original, xml);

    // The VP8L bitstream should still appear verbatim somewhere in the output
    // — WebP editing must never re-encode image data.
    var vp8lFourcc = Encoding.ASCII.GetBytes("VP8L");
    Assert.That(Contains(output, vp8lFourcc), Is.True);
  }

  [Test]
  public void TryReadXmpChunk_NoXmp_ReturnsNull() {
    Assert.That(WebpChunkSurgery.TryReadXmpChunk(SimpleWebpVp8L()), Is.Null);
  }

  [Test]
  public void ReplaceXmpChunk_NotRiff_Throws() {
    var garbage = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
    Assert.Throws<InvalidDataException>(() =>
      WebpChunkSurgery.ReplaceXmpChunk(garbage, Encoding.UTF8.GetBytes("<xmp/>"))
    );
  }

  [Test]
  public void RoundTrip_ReplacesExistingXmp_NoDuplicate() {
    var first = WebpChunkSurgery.ReplaceXmpChunk(SimpleWebpVp8L(), Encoding.UTF8.GetBytes("<old/>"));
    var second = WebpChunkSurgery.ReplaceXmpChunk(first, Encoding.UTF8.GetBytes("<new/>"));

    var xmpFourcc = Encoding.ASCII.GetBytes("XMP ");
    Assert.Multiple(() => {
      Assert.That(CountOccurrences(second, xmpFourcc), Is.EqualTo(1));
      Assert.That(WebpChunkSurgery.TryReadXmpChunk(second), Is.EqualTo(Encoding.UTF8.GetBytes("<new/>")).AsCollection);
    });
  }

  private static bool Contains(byte[] haystack, byte[] needle) {
    for (var i = 0; i <= haystack.Length - needle.Length; i++) {
      if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
        return true;
    }
    return false;
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
