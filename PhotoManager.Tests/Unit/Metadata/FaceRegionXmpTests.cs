using Hawkynt.PhotoManager.Core.Detection;
using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.Core.Regions;

namespace Hawkynt.PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class FaceRegionXmpTests {
  private static TaggedRegion Face(NormalizedBoundingBox box, string? name = null)
    => new(box, RegionCategory.Person, name, RegionStatus.Accepted);

  [Test]
  public void RoundTrip_NamedFace_PreservesNameAndBox() {
    var original = new FullMetadata {
      Regions = new[] {
        Face(new NormalizedBoundingBox(0.25f, 0.30f, 0.10f, 0.15f), "Alice")
      }
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.Faces, Has.Count.EqualTo(1));
      var face = parsed.Faces[0];
      Assert.That(face.PersonName, Is.EqualTo("Alice"));
      Assert.That(face.Box.X, Is.EqualTo(0.25f).Within(1e-4));
      Assert.That(face.Box.Y, Is.EqualTo(0.30f).Within(1e-4));
      Assert.That(face.Box.Width, Is.EqualTo(0.10f).Within(1e-4));
      Assert.That(face.Box.Height, Is.EqualTo(0.15f).Within(1e-4));
    });
  }

  [Test]
  public void RoundTrip_UnnamedFace_PersonNameStaysNull() {
    var original = new FullMetadata {
      Regions = new[] {
        Face(new NormalizedBoundingBox(0.5f, 0.5f, 0.1f, 0.1f))
      }
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.That(parsed.Faces, Has.Count.EqualTo(1));
    Assert.That(parsed.Faces[0].PersonName, Is.Null);
  }

  [Test]
  public void RoundTrip_MultipleFaces_AllPreserved() {
    var original = new FullMetadata {
      Regions = new[] {
        Face(new NormalizedBoundingBox(0.1f, 0.1f, 0.1f, 0.1f), "Alice"),
        Face(new NormalizedBoundingBox(0.5f, 0.5f, 0.1f, 0.1f), "Bob"),
        Face(new NormalizedBoundingBox(0.8f, 0.8f, 0.1f, 0.1f))
      }
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.Faces, Has.Count.EqualTo(3));
      Assert.That(parsed.Faces.Select(f => f.PersonName), Is.EqualTo(new[] { "Alice", "Bob", (string?)null }).AsCollection);
    });
  }

  [Test]
  public void Parse_LightroomStyleRegions_Extracted() {
    // Shape a Lightroom-exported XMP would produce: center-based area coords,
    // rdf:parseType="Resource" on each region, "Face" type.
    const string xml = """
      <?xml version="1.0" encoding="UTF-8"?>
      <x:xmpmeta xmlns:x="adobe:ns:meta/">
        <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:mwg-rs="http://www.metadataworkinggroup.com/schemas/regions/"
            xmlns:stArea="http://ns.adobe.com/xap/1.0/sType/Area#">
            <mwg-rs:Regions rdf:parseType="Resource">
              <mwg-rs:RegionList>
                <rdf:Bag>
                  <rdf:li rdf:parseType="Resource">
                    <mwg-rs:Name>Alice</mwg-rs:Name>
                    <mwg-rs:Type>Face</mwg-rs:Type>
                    <mwg-rs:Area stArea:x="0.3" stArea:y="0.35" stArea:w="0.1" stArea:h="0.15" stArea:unit="normalized"/>
                  </rdf:li>
                </rdf:Bag>
              </mwg-rs:RegionList>
            </mwg-rs:Regions>
          </rdf:Description>
        </rdf:RDF>
      </x:xmpmeta>
      """;

    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.Faces, Has.Count.EqualTo(1));
      Assert.That(parsed.Faces[0].PersonName, Is.EqualTo("Alice"));
      // center x 0.3, width 0.1 → top-left x = 0.25
      Assert.That(parsed.Faces[0].Box.X, Is.EqualTo(0.25f).Within(1e-4));
      Assert.That(parsed.Faces[0].Box.Y, Is.EqualTo(0.275f).Within(1e-4));
    });
  }

  [Test]
  public void Parse_NonFaceRegionType_Skipped() {
    // A "BarCode" or "Pet" region type should not be returned as a face.
    const string xml = """
      <?xml version="1.0" encoding="UTF-8"?>
      <x:xmpmeta xmlns:x="adobe:ns:meta/">
        <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:mwg-rs="http://www.metadataworkinggroup.com/schemas/regions/"
            xmlns:stArea="http://ns.adobe.com/xap/1.0/sType/Area#">
            <mwg-rs:Regions rdf:parseType="Resource">
              <mwg-rs:RegionList>
                <rdf:Bag>
                  <rdf:li rdf:parseType="Resource">
                    <mwg-rs:Name>Barcode</mwg-rs:Name>
                    <mwg-rs:Type>BarCode</mwg-rs:Type>
                    <mwg-rs:Area stArea:x="0.5" stArea:y="0.5" stArea:w="0.1" stArea:h="0.1" stArea:unit="normalized"/>
                  </rdf:li>
                </rdf:Bag>
              </mwg-rs:RegionList>
            </mwg-rs:Regions>
          </rdf:Description>
        </rdf:RDF>
      </x:xmpmeta>
      """;

    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.That(parsed.Faces, Is.Empty);
  }

  /// <summary>
  /// Bug regression: dismissing the last region must actually remove it.
  /// The "preserve foreign elements" pass in Serialize used to copy the old
  /// Regions element back from the source doc when we emitted none, so
  /// clearing regions never took effect on a re-save.
  /// </summary>
  [Test]
  public void RoundTrip_ClearingRegions_RemovesThemFromOutput() {
    var original = new FullMetadata {
      Title = "Stays",
      Regions = new[] {
        new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Animal, "cat")
      }
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, sourceDoc) = XmpSidecarFormatter.Parse(xml);

    // Now clear the regions and re-serialize using the source doc to preserve
    // unrelated fields — the old Regions element must NOT leak back in.
    var cleared = parsed with { Regions = Array.Empty<TaggedRegion>() };
    var clearedXml = XmpSidecarFormatter.Serialize(cleared, sourceDoc);

    Assert.Multiple(() => {
      Assert.That(clearedXml, Does.Not.Contain("mwg-rs:Regions"),
        "the old Regions block must not be re-added from the source doc");
      var (reparsed, _) = XmpSidecarFormatter.Parse(clearedXml);
      Assert.That(reparsed.Regions, Is.Empty);
      Assert.That(reparsed.Title, Is.EqualTo("Stays"), "unrelated fields still round-trip");
    });
  }
}
