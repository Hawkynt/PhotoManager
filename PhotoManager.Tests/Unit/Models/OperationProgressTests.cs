using Hawkynt.PhotoManager.UI.Models;

namespace Hawkynt.PhotoManager.Tests.Unit.Models;

[TestFixture]
public class OperationProgressTests {
  [Test]
  public void NewInstance_DefaultsToIndeterminateRunning() {
    var op = new OperationProgress("scanning");
    Assert.Multiple(() => {
      Assert.That(op.IsRunning, Is.True);
      Assert.That(op.Description, Is.EqualTo("scanning"));
      Assert.That(op.Fraction, Is.NaN);
      Assert.That(op.IsIndeterminate, Is.True);
      Assert.That(op.Percentage, Is.EqualTo(0));
      Assert.That(op.Cancel, Is.Null);
    });
  }

  [Test]
  public void SettingDescription_RaisesPropertyChanged() {
    var op = new OperationProgress("a");
    var raised = new List<string?>();
    op.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

    op.Description = "b";

    Assert.That(raised, Is.EqualTo(new[] { nameof(OperationProgress.Description) }));
  }

  [Test]
  public void SettingFraction_RaisesFractionAndDerivedProperties() {
    var op = new OperationProgress("scan");
    var raised = new List<string?>();
    op.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

    op.Fraction = 0.5;

    Assert.That(raised, Is.EquivalentTo(new[] {
      nameof(OperationProgress.Fraction),
      nameof(OperationProgress.IsIndeterminate),
      nameof(OperationProgress.Percentage),
    }));
  }

  [Test]
  public void Fraction_DeterminateValue_IsNotIndeterminate() {
    var op = new OperationProgress("scan") { Fraction = 0.25 };
    Assert.Multiple(() => {
      Assert.That(op.IsIndeterminate, Is.False);
      Assert.That(op.Percentage, Is.EqualTo(25));
    });
  }

  [Test]
  public void Fraction_OutOfRange_PercentageClampsTo0_100() {
    var op = new OperationProgress("scan");

    op.Fraction = -0.5;
    Assert.That(op.Percentage, Is.EqualTo(0));

    op.Fraction = 2.0;
    Assert.That(op.Percentage, Is.EqualTo(100));
  }

  [Test]
  public void Cancel_InvokesProvidedCallback() {
    var hits = 0;
    var op = new OperationProgress("scan", () => hits++);

    op.Cancel?.Invoke();
    op.Cancel?.Invoke();

    Assert.That(hits, Is.EqualTo(2));
  }

  [Test]
  public void SettingSameDescription_DoesNotRaiseEvent() {
    var op = new OperationProgress("same");
    var raised = new List<string?>();
    op.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

    op.Description = "same";

    Assert.That(raised, Is.Empty);
  }

  [Test]
  public void IsRunning_RaisesPropertyChangedWhenFlipped() {
    var op = new OperationProgress("scan");
    var raised = new List<string?>();
    op.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

    op.IsRunning = false;

    Assert.That(raised, Is.EqualTo(new[] { nameof(OperationProgress.IsRunning) }));
  }

  [Test]
  public void MainViewModel_CurrentOperation_RaisesHasCurrentOperation() {
    var vm = new MainViewModel();
    var raised = new List<string?>();
    vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

    vm.CurrentOperation = new OperationProgress("scan");

    Assert.Multiple(() => {
      Assert.That(raised, Contains.Item(nameof(MainViewModel.CurrentOperation)));
      Assert.That(raised, Contains.Item(nameof(MainViewModel.HasCurrentOperation)));
      Assert.That(vm.HasCurrentOperation, Is.True);
    });
  }

  [Test]
  public void MainViewModel_CurrentOperationCleared_HasFlagFalse() {
    var vm = new MainViewModel { CurrentOperation = new OperationProgress("scan") };

    vm.CurrentOperation = null;

    Assert.That(vm.HasCurrentOperation, Is.False);
  }
}
