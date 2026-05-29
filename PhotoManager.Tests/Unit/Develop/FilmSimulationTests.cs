using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
public sealed class FilmSimulationTests {
  [Test]
  public void All_ContainsExpectedCount() {
    Assert.That(FilmPreset.All, Has.Count.EqualTo(8));
  }

  [Test]
  public void All_HaveUniqueNames() {
    var names = FilmPreset.All.Select(p => p.Name).ToList();
    Assert.That(names, Is.Unique);
  }

  [Test]
  public void All_ProduceNonDefaultSettings() {
    foreach (var preset in FilmPreset.All) {
      Assert.That(preset.Adjustments.IsIdentity, Is.False,
        $"Preset '{preset.Name}' should not be identity.");
    }
  }

  [Test]
  public void All_HaveNonEmptyNameAndDescription() {
    foreach (var preset in FilmPreset.All) {
      Assert.Multiple(() => {
        Assert.That(preset.Name, Is.Not.Null.And.Not.Empty,
          "Every preset must have a name.");
        Assert.That(preset.Description, Is.Not.Null.And.Not.Empty,
          $"Preset '{preset.Name}' must have a description.");
      });
    }
  }

  [Test]
  public void Velvia_HasHighSaturation() {
    var velvia = FilmPreset.All.First(p => p.Name == "Velvia 50");
    Assert.That(velvia.Adjustments.SaturationPercent, Is.GreaterThan(30));
  }

  [Test]
  public void TriX_IsGrayscale() {
    var triX = FilmPreset.All.First(p => p.Name == "Tri-X 400");
    Assert.That(triX.Adjustments.ConvertToGrayscale, Is.True);
  }

  [Test]
  public void InfraredBW_IsGrayscale() {
    var infrared = FilmPreset.All.First(p => p.Name == "Infrared B&W");
    Assert.That(infrared.Adjustments.ConvertToGrayscale, Is.True);
  }

  [Test]
  public void CinematicTealOrange_HasSplitToning() {
    var cto = FilmPreset.All.First(p => p.Name == "Cinematic Teal-Orange");
    Assert.Multiple(() => {
      Assert.That(cto.Adjustments.SplitToningShadowSaturation, Is.GreaterThan(0),
        "Should have teal shadow toning.");
      Assert.That(cto.Adjustments.SplitToningHighlightSaturation, Is.GreaterThan(0),
        "Should have orange highlight toning.");
    });
  }

  [Test]
  public void FadedVintage_HasLiftedBlacks() {
    var vintage = FilmPreset.All.First(p => p.Name == "Faded Vintage");
    Assert.That(vintage.Adjustments.BlacksPercent, Is.GreaterThan(0),
      "Faded look requires lifted (positive) blacks.");
  }

  [Test]
  public void MergeOnto_PreservesUserExposureAndWhiteBalance() {
    var userBase = new DevelopSettings(ExposureStops: 1.5, TemperatureShift: -20, TintShift: 10);
    var velvia = FilmPreset.All.First(p => p.Name == "Velvia 50");

    DevelopSettings merged = FilmPreset.MergeOnto(userBase, velvia.Adjustments);

    Assert.Multiple(() => {
      Assert.That(merged.ExposureStops, Is.EqualTo(1.5),
        "User exposure must be preserved.");
      // Temperature is additive: user -20 + velvia +8 = -12
      Assert.That(merged.TemperatureShift, Is.EqualTo(userBase.TemperatureShift),
        "User temperature should be preserved (merge doesn't touch WB).");
      Assert.That(merged.TintShift, Is.EqualTo(userBase.TintShift),
        "User tint should be preserved.");
    });
  }

  [Test]
  public void MergeOnto_AdditiveSaturationIsClamped() {
    var userBase = new DevelopSettings(SaturationPercent: 80);
    var velvia = FilmPreset.All.First(p => p.Name == "Velvia 50");

    DevelopSettings merged = FilmPreset.MergeOnto(userBase, velvia.Adjustments);

    Assert.That(merged.SaturationPercent, Is.LessThanOrEqualTo(100),
      "Merged saturation must not exceed 100.");
  }

  [Test]
  public void MergeOnto_PreservesUserCropAndRotation() {
    var userBase = new DevelopSettings(RotationDegrees: 90, CropLeft: 0.1, CropTop: 0.2, CropRight: 0.9, CropBottom: 0.8);
    var preset = FilmPreset.All.First().Adjustments;

    DevelopSettings merged = FilmPreset.MergeOnto(userBase, preset);

    Assert.Multiple(() => {
      Assert.That(merged.RotationDegrees, Is.EqualTo(90));
      Assert.That(merged.CropLeft, Is.EqualTo(0.1));
      Assert.That(merged.CropTop, Is.EqualTo(0.2));
      Assert.That(merged.CropRight, Is.EqualTo(0.9));
      Assert.That(merged.CropBottom, Is.EqualTo(0.8));
    });
  }

  [Test]
  public void ApplyPresetToImage_DoesNotThrow() {
    using var image = new Image<Rgba32>(64, 64);
    foreach (var preset in FilmPreset.All) {
      Assert.DoesNotThrow(() => {
        ImageDeveloper.Apply(image.Clone(), preset.Adjustments, previewMode: true);
      }, $"Applying preset '{preset.Name}' must not throw.");
    }
  }

  [Test]
  public void MergeOnto_NoneReturnsUserBase() {
    var userBase = new DevelopSettings(ExposureStops: 2.0, ContrastPercent: 30);
    var identity = new DevelopSettings();

    DevelopSettings merged = FilmPreset.MergeOnto(userBase, identity);

    Assert.Multiple(() => {
      Assert.That(merged.ExposureStops, Is.EqualTo(2.0));
      Assert.That(merged.ContrastPercent, Is.EqualTo(30));
    });
  }
}
