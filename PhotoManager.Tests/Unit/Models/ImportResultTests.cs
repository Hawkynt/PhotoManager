using PhotoManager.Core.Models;

namespace PhotoManager.Tests.Unit.Models;

[TestFixture]
public class ImportResultTests {
  [Test]
  public void Default_HasZeroCountsAndEmptyFileResults() {
    var result = new ImportResult();
    Assert.Multiple(() => {
      Assert.That(result.TotalFiles, Is.EqualTo(0));
      Assert.That(result.SuccessfullyProcessed, Is.EqualTo(0));
      Assert.That(result.Failed, Is.EqualTo(0));
      Assert.That(result.Skipped, Is.EqualTo(0));
      Assert.That(result.FileResults, Is.Empty);
      Assert.That(result.ElapsedTime, Is.EqualTo(TimeSpan.Zero));
    });
  }

  [Test]
  public void With_PreservesUnchangedFields() {
    var src = new ImportResult { TotalFiles = 5, Failed = 1 };
    var copy = src with { Failed = 2 };
    Assert.Multiple(() => {
      Assert.That(copy.TotalFiles, Is.EqualTo(5));
      Assert.That(copy.Failed, Is.EqualTo(2));
    });
  }

  [Test]
  public void ImportFileResult_FromException_ProducesFailureRecord() {
    var src = new FileInfo(@"C:\nope.jpg");
    var fr = ImportFileResult.FromException(src, "boom");
    Assert.Multiple(() => {
      Assert.That(fr.SourcePath, Is.SameAs(src));
      Assert.That(fr.DestinationPath, Is.Null);
      Assert.That(fr.Success, Is.False);
      Assert.That(fr.ErrorMessage, Is.EqualTo("boom"));
      Assert.That(fr.DetectedDate, Is.Null);
    });
  }

  [Test]
  public void ImportFileResult_PrimaryConstructor_CarriesAllFields() {
    var src = new FileInfo(@"C:\src.jpg");
    var dst = new FileInfo(@"C:\dst.jpg");
    var date = new DateTime(2024, 1, 2, 3, 4, 5);
    var fr = new ImportFileResult(src, dst, true, null, date);
    Assert.Multiple(() => {
      Assert.That(fr.SourcePath, Is.SameAs(src));
      Assert.That(fr.DestinationPath, Is.SameAs(dst));
      Assert.That(fr.Success, Is.True);
      Assert.That(fr.ErrorMessage, Is.Null);
      Assert.That(fr.DetectedDate, Is.EqualTo(date));
    });
  }
}
