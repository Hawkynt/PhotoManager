using PhotoManager.Core.Develop;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
public sealed class CompareModeStateTests {
  [Test]
  public void Default_StartsInAfterMode() {
    var state = new CompareModeState();
    Assert.Multiple(() => {
      Assert.That(state.Mode, Is.EqualTo(CompareMode.After));
      Assert.That(state.IsAfterVisible,  Is.True);
      Assert.That(state.IsSplitVisible,  Is.False);
      Assert.That(state.IsSliderVisible, Is.False);
      Assert.That(state.NeedsBaseline,   Is.False);
    });
  }

  [Test]
  public void Cycle_AfterToSplitToSliderToAfter() {
    var state = new CompareModeState();

    Assert.That(state.Cycle(), Is.EqualTo(CompareMode.Split));
    Assert.That(state.IsSplitVisible, Is.True);
    Assert.That(state.NeedsBaseline,  Is.True);

    Assert.That(state.Cycle(), Is.EqualTo(CompareMode.Slider));
    Assert.That(state.IsSliderVisible, Is.True);
    Assert.That(state.NeedsBaseline,   Is.True);

    Assert.That(state.Cycle(), Is.EqualTo(CompareMode.After));
    Assert.That(state.IsAfterVisible, Is.True);
    Assert.That(state.NeedsBaseline,  Is.False);
  }

  [Test]
  public void Cycle_FullLoopReturnsToAfter() {
    var state = new CompareModeState();
    state.Cycle();
    state.Cycle();
    state.Cycle();
    Assert.That(state.Mode, Is.EqualTo(CompareMode.After));
  }

  [Test]
  public void Set_IsIdempotentForCurrentMode() {
    var state = new CompareModeState(CompareMode.Split);
    state.Set(CompareMode.Split);
    state.Set(CompareMode.Split);
    Assert.Multiple(() => {
      Assert.That(state.Mode, Is.EqualTo(CompareMode.Split));
      Assert.That(state.IsSplitVisible, Is.True);
    });
  }

  [Test]
  public void Set_SwitchesDirectlyToTargetMode() {
    var state = new CompareModeState();
    state.Set(CompareMode.Slider);
    Assert.Multiple(() => {
      Assert.That(state.Mode, Is.EqualTo(CompareMode.Slider));
      Assert.That(state.IsSliderVisible, Is.True);
      Assert.That(state.IsAfterVisible,  Is.False);
      Assert.That(state.IsSplitVisible,  Is.False);
    });
  }

  [TestCase(CompareMode.After,  true,  false, false)]
  [TestCase(CompareMode.Split,  false, true,  false)]
  [TestCase(CompareMode.Slider, false, false, true)]
  public void VisibilityFlags_MatchMode(CompareMode mode, bool after, bool split, bool slider) {
    var state = new CompareModeState(mode);
    Assert.Multiple(() => {
      Assert.That(state.IsAfterVisible,  Is.EqualTo(after));
      Assert.That(state.IsSplitVisible,  Is.EqualTo(split));
      Assert.That(state.IsSliderVisible, Is.EqualTo(slider));
    });
  }

  [Test]
  public void NeedsBaseline_OnlyTrueOutsideAfterMode() {
    Assert.Multiple(() => {
      Assert.That(new CompareModeState(CompareMode.After).NeedsBaseline,  Is.False);
      Assert.That(new CompareModeState(CompareMode.Split).NeedsBaseline,  Is.True);
      Assert.That(new CompareModeState(CompareMode.Slider).NeedsBaseline, Is.True);
    });
  }

  [Test]
  public void ButtonContent_ChangesPerMode() {
    var distinct = new HashSet<string>();
    foreach (var m in new[] { CompareMode.After, CompareMode.Split, CompareMode.Slider })
      distinct.Add(new CompareModeState(m).ButtonContent);
    Assert.That(distinct.Count, Is.EqualTo(3));
  }

  [Test]
  public void ButtonTooltip_ChangesPerMode() {
    var distinct = new HashSet<string>();
    foreach (var m in new[] { CompareMode.After, CompareMode.Split, CompareMode.Slider })
      distinct.Add(new CompareModeState(m).ButtonTooltip);
    Assert.That(distinct.Count, Is.EqualTo(3));
  }
}
