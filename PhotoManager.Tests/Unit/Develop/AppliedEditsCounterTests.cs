using PhotoManager.Core.Develop;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
[Category("Unit")]
public sealed class AppliedEditsCounterTests {
  [Test]
  public void Default_settings_count_zero_edits() {
    Assert.That(AppliedEditsCounter.Count(new DevelopSettings()), Is.EqualTo(0));
  }

  [Test]
  public void Single_slider_change_counts_one() {
    var s = new DevelopSettings(ExposureStops: 1.0);
    Assert.That(AppliedEditsCounter.Count(s), Is.EqualTo(1));
  }

  [Test]
  public void Multiple_independent_slider_changes_count_separately() {
    var s = new DevelopSettings(
      ExposureStops:     1.0,
      ContrastPercent:   20,
      HighlightsPercent: -10,
      ShadowsPercent:    15,
      WhitesPercent:     5,
      BlacksPercent:     -5);
    Assert.That(AppliedEditsCounter.Count(s), Is.EqualTo(6));
  }

  [Test]
  public void White_balance_pair_counts_twice() {
    var s = new DevelopSettings(TemperatureShift: 30, TintShift: -10);
    Assert.That(AppliedEditsCounter.Count(s), Is.EqualTo(2));
  }

  [Test]
  public void Rgb_gains_each_count_one() {
    var s = new DevelopSettings(RedGain: 10, GreenGain: 5, BlueGain: -3);
    Assert.That(AppliedEditsCounter.Count(s), Is.EqualTo(3));
  }

  [Test]
  public void Rotation_counts_when_nonzero() {
    Assert.That(AppliedEditsCounter.Count(new DevelopSettings(RotationDegrees: 90)), Is.EqualTo(1));
    Assert.That(AppliedEditsCounter.Count(new DevelopSettings(CropAngleDegrees: 2.5)), Is.EqualTo(1));
    Assert.That(AppliedEditsCounter.Count(new DevelopSettings(RotationDegrees: 90, CropAngleDegrees: 2.5)), Is.EqualTo(2));
  }

  [Test]
  public void Crop_rectangle_counts_once_regardless_of_which_edges_moved() {
    Assert.That(AppliedEditsCounter.Count(new DevelopSettings(CropLeft:   0.1)),  Is.EqualTo(1));
    Assert.That(AppliedEditsCounter.Count(new DevelopSettings(CropRight:  0.9)),  Is.EqualTo(1));
    Assert.That(AppliedEditsCounter.Count(new DevelopSettings(CropTop:    0.1)),  Is.EqualTo(1));
    Assert.That(AppliedEditsCounter.Count(new DevelopSettings(CropBottom: 0.9)),  Is.EqualTo(1));
    // All four moved at once still counts as a single "crop applied".
    Assert.That(AppliedEditsCounter.Count(new DevelopSettings(
      CropLeft: 0.1, CropTop: 0.1, CropRight: 0.9, CropBottom: 0.9)), Is.EqualTo(1));
  }

  [Test]
  public void Ai_stages_each_count() {
    var s = new DevelopSettings(
      AiDenoiseStrength: 0.5,
      AiUpscaleFactor:   2,
      AiColorizeAmount:  0.3);
    Assert.That(AppliedEditsCounter.Count(s), Is.EqualTo(3));
  }

  [Test]
  public void Look_lut_requires_both_name_and_nonzero_opacity() {
    // Just a name with default opacity 1.0 counts.
    Assert.That(AppliedEditsCounter.Count(new DevelopSettings(LookName: "Velvia")), Is.EqualTo(1));
    // Opacity is zero → look has no visible effect → no count.
    Assert.That(AppliedEditsCounter.Count(new DevelopSettings(LookName: "Velvia", LookOpacity: 0)), Is.EqualTo(0));
    // No name → no count regardless of opacity.
    Assert.That(AppliedEditsCounter.Count(new DevelopSettings(LookOpacity: 1.0)), Is.EqualTo(0));
  }

  [Test]
  public void Local_adjustments_contribute_their_count() {
    var locals = new[] {
      new LocalAdjustment(new LocalMask()),
      new LocalAdjustment(new LocalMask()),
      new LocalAdjustment(new LocalMask())
    };
    var s = new DevelopSettings(LocalAdjustments: locals);
    Assert.That(AppliedEditsCounter.Count(s), Is.EqualTo(3));
  }

  [Test]
  public void Tolerance_threshold_excludes_microscopic_values() {
    // 1e-7 is below the 1e-6 tolerance — must NOT count.
    var s = new DevelopSettings(ExposureStops: 1e-7);
    Assert.That(AppliedEditsCounter.Count(s), Is.EqualTo(0));
  }

  [Test]
  public void Throws_on_null_settings() {
    Assert.Throws<ArgumentNullException>(() => AppliedEditsCounter.Count(null!));
  }
}
