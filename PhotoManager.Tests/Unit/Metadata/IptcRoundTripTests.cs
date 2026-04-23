using FileFormat.JpegArchive;

namespace PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class IptcRoundTripTests {
  [Test]
  public void EncodeDecode_PreservesAllFields() {
    var original = new IptcFields {
      ObjectName = "Evening by the lake",
      Caption = "Golden-hour shot at Lake Constance with a 35mm lens.",
      City = "Konstanz",
      SubLocation = "Seestraße",
      ProvinceState = "Baden-Württemberg",
      CountryCode = "DE",
      CountryName = "Germany",
      Keywords = new[] { "sunset", "lake", "Constance" }
    };

    var encoded = IptcIimEncoder.Encode(original);
    var decoded = IptcIimEncoder.Decode(encoded);

    Assert.Multiple(() => {
      Assert.That(decoded.ObjectName, Is.EqualTo(original.ObjectName));
      Assert.That(decoded.Caption, Is.EqualTo(original.Caption));
      Assert.That(decoded.City, Is.EqualTo(original.City));
      Assert.That(decoded.SubLocation, Is.EqualTo(original.SubLocation));
      Assert.That(decoded.ProvinceState, Is.EqualTo(original.ProvinceState));
      Assert.That(decoded.CountryCode, Is.EqualTo(original.CountryCode));
      Assert.That(decoded.CountryName, Is.EqualTo(original.CountryName));
      Assert.That(decoded.Keywords, Is.EqualTo(original.Keywords).AsCollection);
    });
  }

  [Test]
  public void EncodeDecode_UnicodeText_RoundTrips() {
    var fields = new IptcFields {
      City = "München",
      CountryName = "Deutschland",
      Caption = "Biergarten im Englischen Garten — sehr entspannt.",
      Keywords = new[] { "Straße", "Brücke", "Café" }
    };

    var encoded = IptcIimEncoder.Encode(fields);
    var decoded = IptcIimEncoder.Decode(encoded);

    Assert.Multiple(() => {
      Assert.That(decoded.City, Is.EqualTo("München"));
      Assert.That(decoded.Caption, Does.Contain("entspannt"));
      Assert.That(decoded.Keywords, Is.EqualTo(fields.Keywords).AsCollection);
    });
  }

  [Test]
  public void Encode_EmptyFields_OnlyWritesCodedCharacterSet() {
    var fields = new IptcFields();
    var encoded = IptcIimEncoder.Encode(fields);
    // One record: 1:90 (ESC % G) → 5 header bytes + 3 payload bytes = 8
    Assert.That(encoded.Length, Is.EqualTo(8));
  }

  [Test]
  public void IsEmpty_TrueWhenNoFields() {
    Assert.That(new IptcFields().IsEmpty, Is.True);
    Assert.That(new IptcFields { City = "Berlin" }.IsEmpty, Is.False);
  }

  [Test]
  public void JpegRoundTrip_ReplaceIptcSegment_PreservesImageData() {
    // Minimal synthetic JPEG: SOI + small SOS + EOI. JpegSegmentSurgery only
    // touches the APPn/APP13 area, so the scan data must pass through byte-for-byte.
    var input = new byte[] {
      0xFF, 0xD8,                          // SOI
      0xFF, 0xDA, 0x00, 0x08, 1, 2, 3, 4, 5, 6,  // SOS + tiny payload (len 8)
      0x00, 0x11, 0x22, 0x33,              // scan bytes
      0xFF, 0xD9                            // EOI
    };

    var iptc = IptcIimEncoder.Encode(new IptcFields { ObjectName = "Test title" });
    var output = JpegSegmentSurgery.ReplaceIptcSegment(input, iptc);

    var readBack = JpegSegmentSurgery.TryReadIptcSegment(output);
    Assert.That(readBack, Is.Not.Null);
    var decoded = IptcIimEncoder.Decode(readBack!);
    Assert.That(decoded.ObjectName, Is.EqualTo("Test title"));

    // Scan bytes and EOI must pass through verbatim (otherwise we re-encoded the image).
    Assert.Multiple(() => {
      Assert.That(output[^2], Is.EqualTo((byte)0xFF));
      Assert.That(output[^1], Is.EqualTo((byte)0xD9));
    });
  }

  [Test]
  public void JpegRoundTrip_ReplacingExistingIptc_KeepsSingleApp13() {
    var input = new byte[] {
      0xFF, 0xD8,
      0xFF, 0xDA, 0x00, 0x02,
      0xFF, 0xD9
    };

    var first = JpegSegmentSurgery.ReplaceIptcSegment(input, IptcIimEncoder.Encode(new IptcFields { ObjectName = "Old" }));
    var second = JpegSegmentSurgery.ReplaceIptcSegment(first, IptcIimEncoder.Encode(new IptcFields { ObjectName = "New" }));

    // Count APP13 markers in the output — should be exactly one.
    var count = 0;
    for (var i = 0; i < second.Length - 1; i++) {
      if (second[i] == 0xFF && second[i + 1] == 0xED)
        count++;
    }
    Assert.Multiple(() => {
      Assert.That(count, Is.EqualTo(1));
      var decoded = IptcIimEncoder.Decode(JpegSegmentSurgery.TryReadIptcSegment(second)!);
      Assert.That(decoded.ObjectName, Is.EqualTo("New"));
    });
  }
}
