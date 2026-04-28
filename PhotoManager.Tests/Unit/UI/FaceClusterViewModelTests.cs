using PhotoManager.Core.Detection;
using PhotoManager.Core.Faces;
using PhotoManager.Core.Regions;
using PhotoManager.UI.Models;

namespace PhotoManager.Tests.Unit.UI;

[TestFixture]
public class FaceClusterViewModelTests {
  private static FaceCluster MakeCluster(string? label, int count = 1) {
    var box = new NormalizedBoundingBox(0, 0, 0.1f, 0.1f);
    var members = Enumerable.Range(0, count)
      .Select(_ => new ScannedFace(new FileInfo("a.jpg"),
        new TaggedRegion(box, RegionCategory.Person, Label: label)))
      .ToArray();
    return new FaceCluster(1, members);
  }

  [Test]
  public void NewVm_DerivesFieldsFromCluster() {
    var cluster = MakeCluster("Alice", count: 3);
    var vm = new FaceClusterViewModel(cluster);

    Assert.Multiple(() => {
      Assert.That(vm.Cluster, Is.SameAs(cluster));
      Assert.That(vm.DisplayName, Is.EqualTo("Alice"));
      Assert.That(vm.Count, Is.EqualTo(3));
      Assert.That(vm.CountText, Does.Contain("3"));
      Assert.That(vm.IsNamed, Is.True);
      Assert.That(vm.Name, Is.EqualTo("Alice"));
    });
  }

  [Test]
  public void NewVm_UnnamedCluster_DisplayNameIsUnknownN() {
    var cluster = MakeCluster(label: null);
    var vm = new FaceClusterViewModel(cluster);
    Assert.Multiple(() => {
      Assert.That(vm.IsNamed, Is.False);
      Assert.That(vm.DisplayName, Does.StartWith("Unknown-"));
      Assert.That(vm.Name, Is.Empty);
    });
  }

  [Test]
  public void Name_SetSameValue_DoesNotRaise() {
    var vm = new FaceClusterViewModel(MakeCluster("Bob")) { Name = "Bob" };
    var raised = false;
    vm.PropertyChanged += (_, _) => raised = true;
    vm.Name = "Bob";
    Assert.That(raised, Is.False);
  }

  [Test]
  public void Name_NewValue_RaisesPropertyChanged() {
    var vm = new FaceClusterViewModel(MakeCluster("Bob")) { Name = "Bob" };
    string? heard = null;
    vm.PropertyChanged += (_, e) => heard = e.PropertyName;
    vm.Name = "Charlie";
    Assert.That(heard, Is.EqualTo(nameof(FaceClusterViewModel.Name)));
  }

  [Test]
  public void IsSelected_RaisesPropertyChanged() {
    var vm = new FaceClusterViewModel(MakeCluster("Bob"));
    var raised = 0;
    vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FaceClusterViewModel.IsSelected)) raised++; };
    vm.IsSelected = true;
    vm.IsSelected = true;  // no change → no event
    vm.IsSelected = false;
    Assert.That(raised, Is.EqualTo(2));
  }

  [Test]
  public void FaceMemberThumbnailViewModel_DerivesFromFace() {
    var box = new NormalizedBoundingBox(0, 0, 0.1f, 0.1f);
    var face = new ScannedFace(new FileInfo("photo123.jpg"),
      new TaggedRegion(box, RegionCategory.Person, Label: "Alice"));

    var vm = new FaceMemberThumbnailViewModel(face);
    Assert.Multiple(() => {
      Assert.That(vm.Face, Is.SameAs(face));
      Assert.That(vm.FileName, Is.EqualTo("photo123.jpg"));
      Assert.That(vm.IsSelected, Is.False);
    });
  }
}
