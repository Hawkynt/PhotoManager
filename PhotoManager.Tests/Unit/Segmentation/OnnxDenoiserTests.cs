using Hawkynt.PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Segmentation;

[TestFixture]
public class OnnxDenoiserTests {
  private static FileInfo NonexistentModel() => new(Path.Combine(
    Path.GetTempPath(),
    "nonexistent-denoise-model-" + Guid.NewGuid().ToString("N") + ".onnx"));

  [Test]
  public void Denoiser_NoModel_IsNotAvailable() {
    using var denoiser = new OnnxDenoiser(NonexistentModel());
    Assert.That(denoiser.IsAvailable, Is.False);
  }

  [Test]
  public void Denoise_NoModel_ReturnsNull() {
    using var denoiser = new OnnxDenoiser(NonexistentModel());
    using var image = new Image<Rgba32>(64, 64, new Rgba32(128, 128, 128));

    var result = denoiser.Denoise(image, strength: 1.0);

    Assert.That(result, Is.Null);
  }

  [Test]
  public void Denoise_NullSource_Throws() {
    using var denoiser = new OnnxDenoiser(NonexistentModel());
    Assert.Throws<ArgumentNullException>(() => denoiser.Denoise(null!));
  }

  [Test]
  public void Dispose_BeforeUse_DoesNotThrow() {
    var denoiser = new OnnxDenoiser(NonexistentModel());
    Assert.DoesNotThrow(() => denoiser.Dispose());
  }

  [Test]
  public void Denoise_SecondCall_StillReturnsNull_NoModel() {
    using var denoiser = new OnnxDenoiser(NonexistentModel());
    using var image = new Image<Rgba32>(32, 32, new Rgba32(64, 96, 128));

    var first = denoiser.Denoise(image);
    var second = denoiser.Denoise(image, strength: 0.5);

    Assert.That(first, Is.Null);
    Assert.That(second, Is.Null);
  }
}
