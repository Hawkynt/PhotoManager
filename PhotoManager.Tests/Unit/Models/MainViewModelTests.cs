using System.ComponentModel;
using Hawkynt.PhotoManager.Core.Enums;
using Hawkynt.PhotoManager.UI.Models;

namespace Hawkynt.PhotoManager.Tests.Unit.Models;

[TestFixture]
public class MainViewModelTests {
  [Test]
  public void NewInstance_HasReasonableDefaults() {
    var vm = new MainViewModel();
    Assert.Multiple(() => {
      Assert.That(vm.SourceDirectory, Is.Empty);
      Assert.That(vm.DestinationDirectory, Is.Empty);
      Assert.That(vm.IsProcessing, Is.False);
      Assert.That(vm.HasSelectedFile, Is.False);
      Assert.That(vm.Recursive, Is.True);
      Assert.That(vm.PreserveOriginals, Is.False);
      Assert.That(vm.DuplicateHandling, Is.EqualTo(DuplicateHandling.Smart));
      Assert.That(vm.ProgressValue, Is.EqualTo(0));
      Assert.That(vm.StatusMessage, Is.EqualTo("Ready"));
      Assert.That(vm.LastResult, Is.Null);
      Assert.That(vm.TreeViewPaths, Is.Empty);
      Assert.That(vm.SavedSearches, Is.Empty);
    });
  }

  [Test]
  public void SettingProperty_RaisesPropertyChanged() {
    var vm = new MainViewModel();
    var raised = new List<string?>();
    vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

    vm.SourceDirectory = "C:\\src";
    vm.IsProcessing = true;
    vm.HasSelectedFile = true;
    vm.ProgressValue = 42;

    Assert.That(raised, Is.EquivalentTo(new[] {
      nameof(MainViewModel.SourceDirectory),
      nameof(MainViewModel.IsProcessing),
      nameof(MainViewModel.HasSelectedFile),
      nameof(MainViewModel.ProgressValue),
    }));
  }

  [Test]
  public void SettingSameValue_DoesNotRaisePropertyChanged() {
    var vm = new MainViewModel { SourceDirectory = "C:\\same" };
    var raised = new List<string?>();
    vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

    vm.SourceDirectory = "C:\\same";

    Assert.That(raised, Is.Empty);
  }

  [Test]
  public void SavedSearchData_RecordEqualityWorks() {
    var a = new SavedSearchData { Name = "Stars", Text = "alice", MinStars = 4, ColorLabel = "Green" };
    var b = new SavedSearchData { Name = "Stars", Text = "alice", MinStars = 4, ColorLabel = "Green" };
    var c = a with { MinStars = 5 };

    Assert.Multiple(() => {
      Assert.That(a, Is.EqualTo(b));
      Assert.That(a, Is.Not.EqualTo(c));
      Assert.That(c.MinStars, Is.EqualTo(5));
    });
  }

  [Test]
  public void TreeViewPathData_RecordWithDefaults() {
    var t = new TreeViewPathData();
    Assert.Multiple(() => {
      Assert.That(t.Path, Is.Empty);
      Assert.That(t.Recursive, Is.True);
      Assert.That(t.Checked, Is.True);
    });
  }
}
