using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Tests.Unit.Metadata;

[TestFixture]
public class CreditsXmpTests {
  [Test]
  public void RoundTrip_AllCreditsFields_PreservesValues() {
    var original = new FullMetadata {
      Creator = "Jane Photographer",
      Copyright = "© 2026 Jane Photographer",
      Headline = "Evening by Lake Constance",
      Credit = "Courtesy of Jane P.",
      Source = "Press kit 2026-04",
      Instructions = "Do not alter colors.",
      RightsUsage = "Non-commercial use only.",
      DateCreated = new DateTime(2026, 4, 23, 18, 45, 0, DateTimeKind.Unspecified)
    };

    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.Creator, Is.EqualTo(original.Creator));
      Assert.That(parsed.Copyright, Is.EqualTo(original.Copyright));
      Assert.That(parsed.Headline, Is.EqualTo(original.Headline));
      Assert.That(parsed.Credit, Is.EqualTo(original.Credit));
      Assert.That(parsed.Source, Is.EqualTo(original.Source));
      Assert.That(parsed.Instructions, Is.EqualTo(original.Instructions));
      Assert.That(parsed.RightsUsage, Is.EqualTo(original.RightsUsage));
      Assert.That(parsed.DateCreated, Is.EqualTo(original.DateCreated));
    });
  }

  [Test]
  public void RoundTrip_BlankCreditsFields_StayNull() {
    var original = new FullMetadata { Title = "has title but nothing else" };
    var xml = XmpSidecarFormatter.Serialize(original);
    var (parsed, _) = XmpSidecarFormatter.Parse(xml);

    Assert.Multiple(() => {
      Assert.That(parsed.Creator, Is.Null);
      Assert.That(parsed.Copyright, Is.Null);
      Assert.That(parsed.Headline, Is.Null);
      Assert.That(parsed.Credit, Is.Null);
      Assert.That(parsed.Source, Is.Null);
      Assert.That(parsed.Instructions, Is.Null);
      Assert.That(parsed.RightsUsage, Is.Null);
      Assert.That(parsed.DateCreated, Is.Null);
      Assert.That(parsed.Title, Is.EqualTo(original.Title));
    });
  }
}
