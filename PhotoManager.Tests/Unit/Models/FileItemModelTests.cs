using PhotoManager.UI.Models;

namespace PhotoManager.Tests.Unit.Models;

[TestFixture]
public class FileItemModelTests {
  [Test]
  public void Defaults_AreEmpty() {
    var m = new FileItemModel();
    Assert.Multiple(() => {
      Assert.That(m.FileName, Is.Empty);
      Assert.That(m.TargetLocation, Is.Empty);
      Assert.That(m.SourcePath, Is.Empty);
      Assert.That(m.SearchIndex, Is.Empty);
      Assert.That(m.FileInfo, Is.Null);
      Assert.That(m.Rating, Is.Null);
      Assert.That(m.ColorLabel, Is.Null);
    });
  }

  [Test]
  public void SettingFileName_RaisesPropertyChanged() {
    var m = new FileItemModel();
    var seen = new List<string?>();
    m.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

    m.FileName = "alpha.jpg";
    m.TargetLocation = "2024/01";
    m.SourcePath = "Pictures";

    Assert.That(seen, Is.EquivalentTo(new[] {
      nameof(FileItemModel.FileName),
      nameof(FileItemModel.TargetLocation),
      nameof(FileItemModel.SourcePath),
    }));
  }

  [Test]
  public void SettingSameValue_DoesNotRaise() {
    var m = new FileItemModel { FileName = "x.jpg" };
    var raised = false;
    m.PropertyChanged += (_, _) => raised = true;
    m.FileName = "x.jpg";
    Assert.That(raised, Is.False);
  }
}
