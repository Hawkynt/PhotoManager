using Hawkynt.PhotoManager.Core.Library;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Tests.Unit.Library;

[TestFixture]
public class RenameTokenExpanderTests {
  private static readonly FileInfo SampleFile = new("/photos/IMG_0042.jpg");
  private static readonly DateTime CaptureDate = new(2024, 6, 15, 13, 24, 56);

  private static FullMetadata SampleMetadata() => new() {
    Rating = 4,
    ColorLabel = "Yellow",
    City = "Rome",
    Country = "Italy",
    CountryCode = "IT",
    State = "Lazio",
    Title = "Trevi fountain",
    Caption = "Late afternoon light",
    Creator = "Alice"
  };

  [Test]
  public void Expand_PassesThroughLiterals() {
    var result = RenameTokenExpander.Expand("hello-world", SampleFile, null, null, 1, 1);
    Assert.That(result, Is.EqualTo("hello-world"));
  }

  [Test]
  public void Expand_SubstitutesNameAndExtension() {
    var result = RenameTokenExpander.Expand("{name}.{ext}", SampleFile, null, null, 1, 1);
    Assert.That(result, Is.EqualTo("IMG_0042.jpg"));
  }

  [Test]
  public void Expand_FormatsDateWithCustomFormat() {
    var result = RenameTokenExpander.Expand("{date:yyyy-MM-dd}_{time:HHmmss}", SampleFile, null, CaptureDate, 1, 1);
    Assert.That(result, Is.EqualTo("2024-06-15_132456"));
  }

  [Test]
  public void Expand_DateComponents_ExposeIndividualParts() {
    var result = RenameTokenExpander.Expand("{year}-{month}-{day}", SampleFile, null, CaptureDate, 1, 1);
    Assert.That(result, Is.EqualTo("2024-06-15"));
  }

  [Test]
  public void Expand_LiteralPathSeparatorsAreSanitised() {
    // The user might accidentally type {year}/{month}/{day} thinking it
    // creates subfolders. Batch rename only renames within the same
    // directory, so we substitute path separators in the final output.
    var result = RenameTokenExpander.Expand("{year}/{month}/{day}", SampleFile, null, CaptureDate, 1, 1);
    Assert.That(result, Is.EqualTo("2024_06_15"));
  }

  [Test]
  public void Expand_RatingAndLabelTokens() {
    var result = RenameTokenExpander.Expand("r{rating}_{label}", SampleFile, SampleMetadata(), null, 1, 1);
    Assert.That(result, Is.EqualTo("r4_Yellow"));
  }

  [Test]
  public void Expand_PlaceNameTokens() {
    var result = RenameTokenExpander.Expand("{city}-{country}-{countrycode}", SampleFile, SampleMetadata(), null, 1, 1);
    Assert.That(result, Is.EqualTo("Rome-Italy-IT"));
  }

  [Test]
  public void Expand_IndexWithFormatting() {
    var result = RenameTokenExpander.Expand("img_{index:000}_of_{total:000}", SampleFile, null, null, 7, 42);
    Assert.That(result, Is.EqualTo("img_007_of_042"));
  }

  [Test]
  public void Expand_UnknownTokenPassesThroughVerbatim() {
    var result = RenameTokenExpander.Expand("{unknown}_{name}", SampleFile, null, null, 1, 1);
    Assert.That(result, Is.EqualTo("{unknown}_IMG_0042"),
      "leaving unknown tokens visible helps the user spot template typos");
  }

  [Test]
  public void Expand_MissingMetadata_ProducesEmptyForMetadataTokens() {
    var result = RenameTokenExpander.Expand("[{rating}][{city}]_{name}", SampleFile, null, null, 1, 1);
    Assert.That(result, Is.EqualTo("[][]_IMG_0042"));
  }

  [Test]
  public void Expand_SanitisesIllegalCharsInTokenValues() {
    var meta = new FullMetadata { City = "New/York" };
    var result = RenameTokenExpander.Expand("{city}_{name}", SampleFile, meta, null, 1, 1);
    Assert.That(result, Is.EqualTo("New_York_IMG_0042"),
      "slashes inside metadata values must be neutralised so they can't create unintended subdirectories");
  }

  [Test]
  public void Expand_UnmatchedOpenBrace_PassesThroughTail() {
    var result = RenameTokenExpander.Expand("{name}_{unfinished", SampleFile, null, null, 1, 1);
    Assert.That(result, Is.EqualTo("IMG_0042_{unfinished"));
  }

  [Test]
  public void SanitizeForFilename_ReplacesInvalidChars() {
    Assert.That(RenameTokenExpander.SanitizeForFilename("a/b\\c?d"), Is.EqualTo("a_b_c_d"));
    Assert.That(RenameTokenExpander.SanitizeForFilename("  ok  "), Is.EqualTo("ok"));
  }
}
