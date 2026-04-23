using System.Text;
using FileFormat.JpegArchive;

namespace PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class TiffMetadataEditorTests {
  private static byte[] MinimalTiff() {
    // Build a tiny TIFF with IFD0 containing just ImageWidth=1 so TiffWriter
    // has something non-trivial to round-trip.
    var image = new TiffImage { LittleEndian = true, Ifd0 = new TiffIfd() };
    image.Ifd0.SetEntry(new TiffEntry(0x0100, TiffFieldType.Long, 1, new byte[4] { 1, 0, 0, 0 }));
    return TiffWriter.Serialize(image);
  }

  [Test]
  public void ReplaceXmpPacket_Inserts_ThenReadsBack() {
    var payload = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">tiff</x:xmpmeta>");
    var withXmp = TiffMetadataEditor.ReplaceXmpPacket(MinimalTiff(), payload);

    var readBack = TiffMetadataEditor.TryReadXmpPacket(withXmp);
    Assert.That(readBack, Is.EqualTo(payload).AsCollection);
  }

  [Test]
  public void ApplyPatch_WritesGpsAndXmp_InOnePass() {
    var xml = Encoding.UTF8.GetBytes("<xmp/>");
    var patch = new ExifPatch {
      Gps = new GpsPoint(48.8584, 2.2945, 35.0),
      ImageDirectionDegrees = 123,
      ImageDescription = "Paris"
    };

    var withBoth = TiffMetadataEditor.ApplyPatch(MinimalTiff(), patch, xml);

    var reparsed = TiffReader.Parse(withBoth);
    Assert.Multiple(() => {
      // XMP packet tag present.
      Assert.That(reparsed.Ifd0.FindEntry(TiffMetadataEditor.XmpPacketTag), Is.Not.Null);
      // ImageDescription present.
      var descEntry = reparsed.Ifd0.FindEntry(TiffTags.ImageDescription);
      Assert.That(descEntry, Is.Not.Null);
      Assert.That(Encoding.ASCII.GetString(descEntry!.ValueBytes).TrimEnd('\0'), Is.EqualTo("Paris"));
      // GPS sub-IFD present with latitude tag.
      Assert.That(reparsed.Ifd0.SubIfds.ContainsKey(TiffTags.GpsSubIfdPointer), Is.True);
      Assert.That(reparsed.Ifd0.SubIfds[TiffTags.GpsSubIfdPointer].FindEntry(TiffTags.GpsLatitude), Is.Not.Null);
    });
  }
}
