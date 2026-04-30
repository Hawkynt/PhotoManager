using System.Collections.Generic;
using NUnit.Framework;
using PhotoManager.Core.Detection;
using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
[Category("Unit")]
public sealed class RestorationPipelineTests {
  [Test]
  public void Identity_settings_returns_a_clone_with_matching_dimensions() {
    using var src = new Image<Rgba32>(64, 48);
    src[3, 2] = new Rgba32(200, 100, 50, 255);

    using var output = RestorationPipeline.Apply(src, new List<NormalizedBoundingBox>(), new RestorationSettings());

    Assert.That(output.Width, Is.EqualTo(src.Width));
    Assert.That(output.Height, Is.EqualTo(src.Height));
    Assert.That(output[3, 2], Is.EqualTo(src[3, 2]));
  }

  [Test]
  public void Auto_tone_runs_without_throwing_on_low_contrast_input() {
    using var src = new Image<Rgba32>(32, 32);
    src.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new Rgba32((byte)(90 + (x + y)), (byte)(90 + (x + y)), (byte)(90 + (x + y)), 255);
      }
    });
    var settingsAutoTone = new RestorationSettings(AutoTone: true);
    Assert.DoesNotThrow(() => {
      using var _ = RestorationPipeline.Apply(src, new List<NormalizedBoundingBox>(), settingsAutoTone);
    });
  }

  [Test]
  public void Missing_models_make_AI_stages_no_op_without_throwing() {
    using var src = new Image<Rgba32>(32, 32);
    var settings = new RestorationSettings(
      DenoiseStrength: 1.0,
      RecolourStrength: 1.0,
      FaceRestoreStrength: 1.0,
      DenoiseModel: "this-file-does-not-exist.onnx",
      ColorizeModel: "neither-does-this.onnx",
      UpscaleModel: "or-this.onnx",
      UpscaleFactor: 2
    );
    var faces = new List<NormalizedBoundingBox> {
      new(0.25f, 0.25f, 0.5f, 0.5f)
    };
    Assert.DoesNotThrow(() => {
      using var output = RestorationPipeline.Apply(src, faces, settings);
      // Upscale failed → stays at source size; everything else no-ops.
      Assert.That(output.Width, Is.EqualTo(src.Width));
      Assert.That(output.Height, Is.EqualTo(src.Height));
    });
  }

  [Test]
  public void Presets_are_non_identity_and_distinct() {
    Assert.That(RestorationSettings.OldBlackAndWhite.IsIdentity, Is.False);
    Assert.That(RestorationSettings.DamagedColour.IsIdentity, Is.False);
    Assert.That(RestorationSettings.FadedSlide.IsIdentity, Is.False);
    Assert.That(RestorationSettings.SubtleCleanup.IsIdentity, Is.False);

    Assert.That(RestorationSettings.OldBlackAndWhite, Is.Not.EqualTo(RestorationSettings.DamagedColour));
    Assert.That(RestorationSettings.OldBlackAndWhite.RecolourStrength, Is.GreaterThan(0));
    Assert.That(RestorationSettings.DamagedColour.RecolourStrength, Is.EqualTo(0));
  }
}
