using System.Text;
using FileFormat.JpegArchive;

namespace PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class JpegSegmentSurgeryTests {
  private static byte[] MinimalJpeg() => new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }; // SOI + EOI

  private static byte[] JpegWithApp0AndBody() {
    // SOI + APP0 JFIF (16 bytes) + DQT (4 bytes payload placeholder) + EOI
    var ms = new MemoryStream();
    ms.Write(new byte[] { 0xFF, 0xD8 });
    // APP0 JFIF segment: marker + length + "JFIF\0" + version + density fields
    var jfifPayload = new byte[] { 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00 };
    var jfifLen = 2 + jfifPayload.Length;
    ms.Write(new byte[] { 0xFF, 0xE0, (byte)(jfifLen >> 8), (byte)(jfifLen & 0xFF) });
    ms.Write(jfifPayload);
    // DQT stub: marker + length + 1 byte of fake body
    ms.Write(new byte[] { 0xFF, 0xDB, 0x00, 0x03, 0x00 });
    // EOI
    ms.Write(new byte[] { 0xFF, 0xD9 });
    return ms.ToArray();
  }

  [Test]
  public void RoundTrip_MinimalJpeg_XmpEmbeddedAndReadBack() {
    var xmpPayload = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">sample</x:xmpmeta>");
    var withXmp = JpegSegmentSurgery.ReplaceXmpSegment(MinimalJpeg(), xmpPayload);

    var readBack = JpegSegmentSurgery.TryReadXmpSegment(withXmp);
    Assert.Multiple(() => {
      Assert.That(readBack, Is.Not.Null);
      Assert.That(readBack, Is.EqualTo(xmpPayload).AsCollection);
    });
  }

  [Test]
  public void ReplaceXmpSegment_PreservesNonXmpSegmentsAndOrdering() {
    var original = JpegWithApp0AndBody();
    var xmpPayload = Encoding.UTF8.GetBytes("<xmp/>");

    var result = JpegSegmentSurgery.ReplaceXmpSegment(original, xmpPayload);

    // Structure expectation: SOI, APP0 JFIF, APP1 XMP, DQT, EOI.
    Assert.Multiple(() => {
      Assert.That(result[0], Is.EqualTo(0xFF));
      Assert.That(result[1], Is.EqualTo(0xD8), "starts with SOI");

      // APP0 preserved right after SOI
      Assert.That(result[2], Is.EqualTo(0xFF));
      Assert.That(result[3], Is.EqualTo(0xE0), "JFIF APP0 retained in place");

      // EOI at end
      Assert.That(result[^2], Is.EqualTo(0xFF));
      Assert.That(result[^1], Is.EqualTo(0xD9), "ends with EOI");

      // XMP is actually present in the output
      var readBack = JpegSegmentSurgery.TryReadXmpSegment(result);
      Assert.That(readBack, Is.EqualTo(xmpPayload).AsCollection);
    });
  }

  [Test]
  public void ReplaceXmpSegment_ReplacesExistingXmp_NoDuplicate() {
    // Build a JPEG that already contains a small XMP payload.
    var first = JpegSegmentSurgery.ReplaceXmpSegment(JpegWithApp0AndBody(), Encoding.UTF8.GetBytes("<old/>"));

    var newPayload = Encoding.UTF8.GetBytes("<new/>");
    var second = JpegSegmentSurgery.ReplaceXmpSegment(first, newPayload);

    // Only one XMP segment should remain.
    var xmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    var matches = CountOccurrences(second, xmpHeader);

    Assert.Multiple(() => {
      Assert.That(matches, Is.EqualTo(1), "exactly one XMP APP1 segment after replacement");
      Assert.That(JpegSegmentSurgery.TryReadXmpSegment(second), Is.EqualTo(newPayload).AsCollection);
    });
  }

  [Test]
  public void TryReadXmpSegment_NoXmp_ReturnsNull() {
    Assert.That(JpegSegmentSurgery.TryReadXmpSegment(JpegWithApp0AndBody()), Is.Null);
  }

  [Test]
  public void ReplaceXmpSegment_NotJpeg_Throws() {
    var garbage = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
    Assert.Throws<InvalidDataException>(() =>
      JpegSegmentSurgery.ReplaceXmpSegment(garbage, Encoding.UTF8.GetBytes("<xmp/>"))
    );
  }

  [Test]
  public void ReplaceXmpSegment_OversizedXmp_Throws() {
    var huge = new byte[70_000];
    Assert.Throws<InvalidOperationException>(() =>
      JpegSegmentSurgery.ReplaceXmpSegment(MinimalJpeg(), huge)
    );
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
