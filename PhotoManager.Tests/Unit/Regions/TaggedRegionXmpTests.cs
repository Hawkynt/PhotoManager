using Hawkynt.PhotoManager.Core.Detection;
using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.Core.Regions;

namespace Hawkynt.PhotoManager.Tests.Unit.Regions;

[TestFixture]
public class TaggedRegionXmpTests {
  private static TaggedRegion Make(RegionCategory category, string label, RegionStatus status = RegionStatus.Accepted, string? source = null)
    => new(new NormalizedBoundingBox(0.2f, 0.25f, 0.1f, 0.15f), category, label, status, source);

  [Test]
  public void RoundTrip_AnimalRegion_KeepsCategory() {
    var original = new FullMetadata {
      Regions = new[] { Make(RegionCategory.Animal, "cat") }
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.Regions, Has.Count.EqualTo(1));
      Assert.That(parsed.Regions[0].Category, Is.EqualTo(RegionCategory.Animal));
      Assert.That(parsed.Regions[0].Label, Is.EqualTo("cat"));
    });
  }

  [Test]
  public void RoundTrip_ItemRegion_UsesFocusMwgTypeButKeepsItemCategory() {
    var original = new FullMetadata {
      Regions = new[] { Make(RegionCategory.Item, "spoon") }
    };

    var xml = XmpSidecarFormatter.Serialize(original);

    // MWG-RS only defines a few types — we emit Focus for our non-face/pet
    // categories, but the Pm category element pins it back to Item on parse.
    Assert.That(xml, Does.Contain("<mwg-rs:Type>Focus</mwg-rs:Type>"));

    var (parsed, _) = XmpSidecarFormatter.Parse(xml);
    Assert.That(parsed.Regions[0].Category, Is.EqualTo(RegionCategory.Item));
  }

  [Test]
  public void RoundTrip_ProposedStatus_Preserved() {
    var original = new FullMetadata {
      Regions = new[] { Make(RegionCategory.Item, "mug", RegionStatus.Proposed, TaggedRegion.YoloSource) }
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.Regions[0].Status, Is.EqualTo(RegionStatus.Proposed));
      Assert.That(parsed.Regions[0].Source, Is.EqualTo(TaggedRegion.YoloSource));
    });
  }

  [Test]
  public void Parse_LightroomFaceWithoutOurExtensions_InferredAsAcceptedPerson() {
    // Lightroom-exported XMP has no pm:* elements — we should still classify
    // the face as Person + Accepted so existing libraries look right out of
    // the box.
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
                    <mwg-rs:Area stArea:x="0.3" stArea:y="0.3" stArea:w="0.1" stArea:h="0.15" stArea:unit="normalized"/>
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
      Assert.That(parsed.Regions, Has.Count.EqualTo(1));
      Assert.That(parsed.Regions[0].Category, Is.EqualTo(RegionCategory.Person));
      Assert.That(parsed.Regions[0].Status, Is.EqualTo(RegionStatus.Accepted));
      Assert.That(parsed.Regions[0].Label, Is.EqualTo("Alice"));
    });
  }

  [Test]
  public void Faces_View_ReflectsOnlyAcceptedPersonRegions() {
    var md = new FullMetadata {
      Regions = new[] {
        new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Person, "Alice", RegionStatus.Accepted),
        new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Person, null, RegionStatus.Proposed),
        new TaggedRegion(new NormalizedBoundingBox(0, 0, 0.1f, 0.1f), RegionCategory.Animal, "cat", RegionStatus.Accepted)
      }
    };

    Assert.Multiple(() => {
      Assert.That(md.Faces, Has.Count.EqualTo(1));
      Assert.That(md.Faces[0].PersonName, Is.EqualTo("Alice"));
    });
  }
}
