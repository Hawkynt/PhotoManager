using PhotoManager.Core.Metadata;

namespace PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class PickRejectXmpTests {
  [Test]
  public void Serialize_PickTrue_EmitsXmpPickElement() {
    var state = new FullMetadata { IsPick = true };

    var xml = XmpSidecarFormatter.Serialize(state);

    Assert.That(xml, Does.Contain("Pick"));
    Assert.That(xml, Does.Contain("true"));
  }

  [Test]
  public void Serialize_RejectTrue_EmitsXmpRejectElement() {
    var state = new FullMetadata { IsReject = true };

    var xml = XmpSidecarFormatter.Serialize(state);

    Assert.That(xml, Does.Contain("Reject"));
  }

  [Test]
  public void Serialize_NeitherFlagSet_OmitsBothElements() {
    var state = new FullMetadata { Rating = 3 };

    var xml = XmpSidecarFormatter.Serialize(state);

    Assert.Multiple(() => {
      Assert.That(xml, Does.Not.Contain("xmp:Pick"));
      Assert.That(xml, Does.Not.Contain("xmp:Reject"));
    });
  }

  [Test]
  public void RoundTrip_PickTrue_Preserved() {
    var original = new FullMetadata { IsPick = true };
    var xml = XmpSidecarFormatter.Serialize(original);

    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.That(parsed.IsPick, Is.True);
    Assert.That(parsed.IsReject, Is.Null);
  }

  [Test]
  public void RoundTrip_RejectTrue_Preserved() {
    var original = new FullMetadata { IsReject = true };
    var xml = XmpSidecarFormatter.Serialize(original);

    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.That(parsed.IsReject, Is.True);
    Assert.That(parsed.IsPick, Is.Null);
  }

  [Test]
  public void RoundTrip_PickAndOtherFields_BothPreserved() {
    var original = new FullMetadata {
      IsPick = true,
      Rating = 5,
      ColorLabel = "Green",
      Title = "Pick Me",
      Keywords = new[] { "a", "b" }
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.IsPick, Is.True);
      Assert.That(parsed.Rating, Is.EqualTo(5));
      Assert.That(parsed.ColorLabel, Is.EqualTo("Green"));
      Assert.That(parsed.Title, Is.EqualTo("Pick Me"));
      Assert.That(parsed.Keywords, Is.EquivalentTo(new[] { "a", "b" }));
    });
  }

  [Test]
  public void Parse_LegacyDocWithoutFlags_ReturnsNullForBoth() {
    const string xml = """
      <?xml version="1.0" encoding="UTF-8"?>
      <x:xmpmeta xmlns:x="adobe:ns:meta/">
        <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:xmp="http://ns.adobe.com/xap/1.0/">
            <xmp:Rating>2</xmp:Rating>
          </rdf:Description>
        </rdf:RDF>
      </x:xmpmeta>
      """;

    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.IsPick, Is.Null);
      Assert.That(parsed.IsReject, Is.Null);
      Assert.That(parsed.Rating, Is.EqualTo(2));
    });
  }

  [Test]
  public void Parse_HandlesUppercaseTrue() {
    const string xml = """
      <?xml version="1.0" encoding="UTF-8"?>
      <x:xmpmeta xmlns:x="adobe:ns:meta/">
        <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:xmp="http://ns.adobe.com/xap/1.0/">
            <xmp:Pick>True</xmp:Pick>
          </rdf:Description>
        </rdf:RDF>
      </x:xmpmeta>
      """;

    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.That(parsed.IsPick, Is.True);
  }

  [Test]
  public async Task SidecarWriter_PickAndRejectAreMutuallyClearable() {
    var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "PickReject-" + Guid.NewGuid().ToString("N")));
    dir.Create();
    try {
      var image = new FileInfo(Path.Combine(dir.FullName, "photo.jpg"));
      File.WriteAllBytes(image.FullName, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
      var writer = new XmpSidecarWriter();

      await writer.ApplyAsync(image, new MetadataEdit { IsPick = Optional<bool?>.Set(true) });
      var reader = new MetadataReader();
      var afterPick = await reader.ReadAsync(image);
      Assert.That(afterPick.IsPick, Is.True, "Pick should be persisted");

      // Switching to Reject must clear Pick — the writer relies on the
      // caller passing both fields, but at least confirm that explicit
      // null-of-Pick removes the flag.
      await writer.ApplyAsync(image, new MetadataEdit {
        IsPick = Optional<bool?>.Set(null),
        IsReject = Optional<bool?>.Set(true)
      });
      var afterReject = await reader.ReadAsync(image);
      Assert.Multiple(() => {
        Assert.That(afterReject.IsPick, Is.Null, "Pick should clear");
        Assert.That(afterReject.IsReject, Is.True, "Reject should be set");
      });

      // Clear both.
      await writer.ApplyAsync(image, new MetadataEdit {
        IsPick = Optional<bool?>.Set(null),
        IsReject = Optional<bool?>.Set(null)
      });
      var afterClear = await reader.ReadAsync(image);
      Assert.Multiple(() => {
        Assert.That(afterClear.IsPick, Is.Null);
        Assert.That(afterClear.IsReject, Is.Null);
      });
    } finally {
      if (dir.Exists)
        dir.Delete(recursive: true);
    }
  }
}
