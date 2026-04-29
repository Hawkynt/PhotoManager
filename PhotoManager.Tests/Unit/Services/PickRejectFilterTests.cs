using PhotoManager.UI.Models;
using PhotoManager.UI.Services;

namespace PhotoManager.Tests.Unit.Services;

[TestFixture]
public class PickRejectFilterTests {
  [Test]
  public void Matches_ShowAll_AcceptsEverything() {
    Assert.Multiple(() => {
      Assert.That(PickRejectFilter.Matches(false, false, PickRejectFilterMode.ShowAll), Is.True);
      Assert.That(PickRejectFilter.Matches(true, false, PickRejectFilterMode.ShowAll), Is.True);
      Assert.That(PickRejectFilter.Matches(false, true, PickRejectFilterMode.ShowAll), Is.True);
    });
  }

  [Test]
  public void Matches_PicksOnly_DropsUnflaggedAndRejects() {
    Assert.Multiple(() => {
      Assert.That(PickRejectFilter.Matches(true,  false, PickRejectFilterMode.PicksOnly), Is.True);
      Assert.That(PickRejectFilter.Matches(false, false, PickRejectFilterMode.PicksOnly), Is.False);
      Assert.That(PickRejectFilter.Matches(false, true,  PickRejectFilterMode.PicksOnly), Is.False);
    });
  }

  [Test]
  public void Matches_RejectsOnly_KeepsOnlyRejects() {
    Assert.Multiple(() => {
      Assert.That(PickRejectFilter.Matches(false, true,  PickRejectFilterMode.RejectsOnly), Is.True);
      Assert.That(PickRejectFilter.Matches(true,  false, PickRejectFilterMode.RejectsOnly), Is.False);
      Assert.That(PickRejectFilter.Matches(false, false, PickRejectFilterMode.RejectsOnly), Is.False);
    });
  }

  [Test]
  public void Matches_HideRejects_DropsRejectsKeepsPicksAndUnflagged() {
    Assert.Multiple(() => {
      Assert.That(PickRejectFilter.Matches(false, false, PickRejectFilterMode.HideRejects), Is.True);
      Assert.That(PickRejectFilter.Matches(true,  false, PickRejectFilterMode.HideRejects), Is.True);
      Assert.That(PickRejectFilter.Matches(false, true,  PickRejectFilterMode.HideRejects), Is.False);
    });
  }

  [Test]
  public void Filter_AppliedToList_ProducesExpectedSubset() {
    var items = new[] {
      new FileItemModel { FileName = "a", IsPick = true },
      new FileItemModel { FileName = "b" },
      new FileItemModel { FileName = "c", IsReject = true },
      new FileItemModel { FileName = "d", IsPick = true },
      new FileItemModel { FileName = "e", IsReject = true }
    };

    var picks = items.Where(i => PickRejectFilter.Matches(i, PickRejectFilterMode.PicksOnly)).Select(i => i.FileName).ToArray();
    var rejects = items.Where(i => PickRejectFilter.Matches(i, PickRejectFilterMode.RejectsOnly)).Select(i => i.FileName).ToArray();
    var hideRej = items.Where(i => PickRejectFilter.Matches(i, PickRejectFilterMode.HideRejects)).Select(i => i.FileName).ToArray();

    Assert.Multiple(() => {
      Assert.That(picks, Is.EquivalentTo(new[] { "a", "d" }));
      Assert.That(rejects, Is.EquivalentTo(new[] { "c", "e" }));
      Assert.That(hideRej, Is.EquivalentTo(new[] { "a", "b", "d" }));
    });
  }

  [Test]
  public void FileItemModel_ChangingPickRaisesGlyphPropertyChanged() {
    var item = new FileItemModel();
    var raised = new List<string?>();
    item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

    item.IsPick = true;

    Assert.That(raised, Does.Contain(nameof(FileItemModel.PickRejectGlyph)));
    Assert.That(item.PickRejectGlyph, Is.EqualTo("✅"));
  }

  [Test]
  public void FileItemModel_ChangingRejectRaisesGlyphPropertyChanged() {
    var item = new FileItemModel();
    var raised = new List<string?>();
    item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

    item.IsReject = true;

    Assert.That(raised, Does.Contain(nameof(FileItemModel.PickRejectGlyph)));
    Assert.That(item.PickRejectGlyph, Is.EqualTo("❌"));
  }

  [Test]
  public void FileItemModel_NoFlag_GlyphIsEmpty() {
    Assert.That(new FileItemModel().PickRejectGlyph, Is.EqualTo(string.Empty));
  }
}
