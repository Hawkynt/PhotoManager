using System.Buffers.Binary;
using System.Text;
using FileFormat.JpegArchive;

namespace PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class TiffRoundTripTests {
  /// <summary>
  /// Builds a minimal valid TIFF area with IFD0 + one sub-IFD (EXIF) so we
  /// have something real to round-trip. Uses little-endian ordering (the
  /// common JPEG EXIF variant).
  /// </summary>
  private static byte[] BuildSampleTiff() {
    // IFD0 will carry two entries:
    //   - ImageDescription (tag 0x010E, ASCII, "hello\0") → 6 bytes OUT OF LINE
    //   - ExifSubIfdPointer (tag 0x8769, Long) → 4 bytes inline
    // EXIF sub-IFD carries one entry:
    //   - UserComment (tag 0x9286, Undefined, 8 bytes starting with "ASCII\0\0\0") → OUT OF LINE
    // Easier: hand-build via TiffReader → TiffWriter round-trip. Construct a
    // TiffImage directly, serialize, parse, compare.
    var exif = new TiffIfd();
    exif.Entries.Add(new TiffEntry(
      TiffTags.UserComment, TiffFieldType.Undefined, 11,
      Encoding.ASCII.GetBytes("ASCII\0\0\0hi!")
    ));

    var ifd0 = new TiffIfd();
    ifd0.Entries.Add(new TiffEntry(
      TiffTags.ImageDescription, TiffFieldType.Ascii, 6,
      Encoding.ASCII.GetBytes("hello\0")
    ));
    ifd0.Entries.Add(new TiffEntry(
      TiffTags.ExifSubIfdPointer, TiffFieldType.Long, 1,
      new byte[4]  // placeholder; writer fills it with real offset
    ));
    ifd0.SubIfds[TiffTags.ExifSubIfdPointer] = exif;

    var image = new TiffImage { LittleEndian = true, Ifd0 = ifd0 };
    return TiffWriter.Serialize(image);
  }

  [Test]
  public void RoundTrip_Inline_And_OutOfLine_Values_Preserved() {
    var bytes = BuildSampleTiff();
    var parsed = TiffReader.Parse(bytes);

    Assert.Multiple(() => {
      Assert.That(parsed.LittleEndian, Is.True);
      Assert.That(parsed.Ifd0.Entries.Select(e => e.Tag),
        Does.Contain(TiffTags.ImageDescription));
      Assert.That(parsed.Ifd0.Entries.Select(e => e.Tag),
        Does.Contain(TiffTags.ExifSubIfdPointer));

      var desc = parsed.Ifd0.FindEntry(TiffTags.ImageDescription);
      Assert.That(desc, Is.Not.Null);
      Assert.That(Encoding.ASCII.GetString(desc!.ValueBytes), Is.EqualTo("hello\0"));

      Assert.That(parsed.Ifd0.SubIfds.ContainsKey(TiffTags.ExifSubIfdPointer), Is.True);
      var uc = parsed.Ifd0.SubIfds[TiffTags.ExifSubIfdPointer].FindEntry(TiffTags.UserComment);
      Assert.That(uc, Is.Not.Null);
      Assert.That(uc!.Count, Is.EqualTo((uint)11));
      Assert.That(Encoding.ASCII.GetString(uc.ValueBytes), Is.EqualTo("ASCII\0\0\0hi!"));
    });
  }

  [Test]
  public void Modify_And_RoundTrip_Keeps_Other_Tags_Intact() {
    var bytes = BuildSampleTiff();
    var image = TiffReader.Parse(bytes);

    // Change ImageDescription; everything else stays.
    image.Ifd0.SetEntry(new TiffEntry(
      TiffTags.ImageDescription, TiffFieldType.Ascii, 10,
      Encoding.ASCII.GetBytes("new descr\0")
    ));

    var reserialized = TiffWriter.Serialize(image);
    var reparsed = TiffReader.Parse(reserialized);

    Assert.Multiple(() => {
      Assert.That(
        Encoding.ASCII.GetString(reparsed.Ifd0.FindEntry(TiffTags.ImageDescription)!.ValueBytes),
        Is.EqualTo("new descr\0")
      );
      // Sub-IFD relationship intact.
      Assert.That(reparsed.Ifd0.SubIfds.ContainsKey(TiffTags.ExifSubIfdPointer), Is.True);
      var uc = reparsed.Ifd0.SubIfds[TiffTags.ExifSubIfdPointer].FindEntry(TiffTags.UserComment);
      Assert.That(uc, Is.Not.Null);
      Assert.That(Encoding.ASCII.GetString(uc!.ValueBytes), Is.EqualTo("ASCII\0\0\0hi!"));
    });
  }

  [Test]
  public void BigEndian_RoundTrip_ProducesValidHeader() {
    var ifd0 = new TiffIfd();
    ifd0.Entries.Add(new TiffEntry(
      TiffTags.Orientation, TiffFieldType.Short, 1,
      new byte[2] { 0x00, 0x01 }  // big-endian encoding of Short(1)
    ));

    var image = new TiffImage { LittleEndian = false, Ifd0 = ifd0 };
    var bytes = TiffWriter.Serialize(image);

    Assert.Multiple(() => {
      Assert.That(bytes[0], Is.EqualTo((byte)'M'));
      Assert.That(bytes[1], Is.EqualTo((byte)'M'));
      // Magic 0x002A, big-endian: 0x00 0x2A
      Assert.That(bytes[2], Is.EqualTo(0x00));
      Assert.That(bytes[3], Is.EqualTo(0x2A));
    });

    var reparsed = TiffReader.Parse(bytes);
    Assert.That(reparsed.LittleEndian, Is.False);
    Assert.That(reparsed.Ifd0.FindEntry(TiffTags.Orientation), Is.Not.Null);
  }

  [Test]
  public void SetEntry_Replaces_Existing_Tag() {
    var ifd = new TiffIfd();
    ifd.Entries.Add(new TiffEntry(TiffTags.Orientation, TiffFieldType.Short, 1, new byte[2] { 0x01, 0x00 }));
    ifd.SetEntry(new TiffEntry(TiffTags.Orientation, TiffFieldType.Short, 1, new byte[2] { 0x06, 0x00 }));

    Assert.That(ifd.Entries, Has.Count.EqualTo(1));
    Assert.That(ifd.FindEntry(TiffTags.Orientation)!.ValueBytes[0], Is.EqualTo(0x06));
  }

  [Test]
  public void RemoveEntry_ReturnsFalse_WhenTagAbsent() {
    var ifd = new TiffIfd();
    Assert.That(ifd.RemoveEntry(0x1234), Is.False);
  }
}
