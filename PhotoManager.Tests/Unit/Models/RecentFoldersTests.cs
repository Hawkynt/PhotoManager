using System.Text.Json;
using PhotoManager.UI.Controllers;
using PhotoManager.UI.Models;

namespace PhotoManager.Tests.Unit.Models;

[TestFixture]
public class RecentFoldersTests {
  [Test]
  public void Promote_OnEmptyList_AddsAtHead() {
    var list = new List<string>();

    MainController.PromoteRecent(list, @"C:\photos\2025", depth: 5);

    Assert.That(list, Is.EqualTo(new[] { @"C:\photos\2025" }));
  }

  [Test]
  public void Promote_NewEntry_PrependsAndKeepsOlder() {
    var list = new List<string> { @"C:\old" };

    MainController.PromoteRecent(list, @"C:\fresh", depth: 5);

    Assert.That(list, Is.EqualTo(new[] { @"C:\fresh", @"C:\old" }));
  }

  [Test]
  public void Promote_DuplicateEntry_MovesToHeadAndDoesNotGrow() {
    var list = new List<string> { @"C:\a", @"C:\b", @"C:\c" };

    MainController.PromoteRecent(list, @"C:\b", depth: 5);

    Assert.That(list, Is.EqualTo(new[] { @"C:\b", @"C:\a", @"C:\c" }));
  }

  [Test]
  public void Promote_CaseInsensitiveDuplicate_DedupesOrdinalIgnoreCase() {
    var list = new List<string> { @"C:\Photos", @"C:\Other" };

    MainController.PromoteRecent(list, @"c:\PHOTOS", depth: 5);

    Assert.That(list.Count, Is.EqualTo(2), "case-insensitive duplicate must not grow the list");
    Assert.That(list[0], Is.EqualTo(@"c:\PHOTOS"));
    Assert.That(list[1], Is.EqualTo(@"C:\Other"));
  }

  [Test]
  public void Promote_OverflowingDepth_TrimsToMax() {
    var list = new List<string> { @"C:\1", @"C:\2", @"C:\3", @"C:\4", @"C:\5" };

    MainController.PromoteRecent(list, @"C:\6", depth: 5);

    Assert.Multiple(() => {
      Assert.That(list.Count, Is.EqualTo(5));
      Assert.That(list[0], Is.EqualTo(@"C:\6"));
      // Oldest entry (was index 4) gets dropped first.
      Assert.That(list, Does.Not.Contain(@"C:\5"));
    });
  }

  [Test]
  public void Promote_DepthZeroOrNegative_NoOp() {
    var list = new List<string> { @"C:\a" };

    MainController.PromoteRecent(list, @"C:\b", depth: 0);
    MainController.PromoteRecent(list, @"C:\b", depth: -1);

    Assert.That(list, Is.EqualTo(new[] { @"C:\a" }));
  }

  [Test]
  public void Promote_NullOrWhitespacePath_NoOp() {
    var list = new List<string> { @"C:\a" };

    MainController.PromoteRecent(list, "", depth: 5);
    MainController.PromoteRecent(list, "   ", depth: 5);

    Assert.That(list, Is.EqualTo(new[] { @"C:\a" }));
  }

  [Test]
  public void RecentFoldersData_RoundTripsThroughJson() {
    var original = new RecentFoldersData {
      SourceFolders = new List<string> { @"C:\a", @"C:\b" },
      OutputFolders = new List<string> { @"D:\out" }
    };

    var json = JsonSerializer.Serialize(original);
    var restored = JsonSerializer.Deserialize<RecentFoldersData>(json);

    Assert.That(restored, Is.Not.Null);
    Assert.That(restored!.SourceFolders, Is.EqualTo(original.SourceFolders));
    Assert.That(restored.OutputFolders, Is.EqualTo(original.OutputFolders));
  }

  [Test]
  public void RecentFoldersData_DefaultLists_AreEmpty() {
    var data = new RecentFoldersData();
    Assert.Multiple(() => {
      Assert.That(data.SourceFolders, Is.Empty);
      Assert.That(data.OutputFolders, Is.Empty);
    });
  }

  [Test]
  public void UserSettingsData_PersistsRecentFoldersThroughJson() {
    var original = new UserSettingsData {
      RecentFolders = new RecentFoldersData {
        SourceFolders = new List<string> { @"C:\src1", @"C:\src2" },
        OutputFolders = new List<string> { @"C:\out1" }
      }
    };

    var json = JsonSerializer.Serialize(original);
    var restored = JsonSerializer.Deserialize<UserSettingsData>(json);

    Assert.That(restored, Is.Not.Null);
    Assert.That(restored!.RecentFolders.SourceFolders, Is.EqualTo(new[] { @"C:\src1", @"C:\src2" }));
    Assert.That(restored.RecentFolders.OutputFolders, Is.EqualTo(new[] { @"C:\out1" }));
  }
}
