using Hawkynt.PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Segmentation;

[TestFixture]
public class OnnxUpscalerTests {
  private static FileInfo NonexistentModel() => new(Path.Combine(
    Path.GetTempPath(),
    "nonexistent-upscale-model-" + Guid.NewGuid().ToString("N") + ".onnx"));

  [Test]
  public void Upscaler_NoModel_IsNotAvailable() {
    using var upscaler = new OnnxUpscaler(NonexistentModel());
    Assert.That(upscaler.IsAvailable, Is.False);
  }

  [Test]
  public void Upscale_FactorOne_ReturnsClone_NoInferenceNeeded() {
    using var upscaler = new OnnxUpscaler(NonexistentModel());
    using var image = new Image<Rgba32>(48, 32, new Rgba32(200, 100, 50));

    using var result = upscaler.Upscale(image, factor: 1);

    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Width, Is.EqualTo(48));
    Assert.That(result.Height, Is.EqualTo(32));
  }

  [Test]
  public void Upscale_FactorTwo_NoModel_ReturnsNull() {
    using var upscaler = new OnnxUpscaler(NonexistentModel());
    using var image = new Image<Rgba32>(32, 24, new Rgba32(64, 64, 64));

    var result = upscaler.Upscale(image, factor: 2);

    Assert.That(result, Is.Null);
  }

  [Test]
  public void Upscale_FactorFour_NoModel_ReturnsNull() {
    using var upscaler = new OnnxUpscaler(NonexistentModel());
    using var image = new Image<Rgba32>(16, 16, new Rgba32(255, 0, 0));

    var result = upscaler.Upscale(image, factor: 4);

    Assert.That(result, Is.Null);
  }

  [Test]
  public void Upscale_NullSource_Throws() {
    using var upscaler = new OnnxUpscaler(NonexistentModel());
    Assert.Throws<ArgumentNullException>(() => upscaler.Upscale(null!));
  }

  [Test]
  public void Upscale_FactorZero_TreatedAsOff_ReturnsClone() {
    using var upscaler = new OnnxUpscaler(NonexistentModel());
    using var image = new Image<Rgba32>(20, 20, new Rgba32(10, 20, 30));

    using var result = upscaler.Upscale(image, factor: 0);

    Assert.That(result, Is.Not.Null);
    Assert.That(result!.Width, Is.EqualTo(20));
    Assert.That(result.Height, Is.EqualTo(20));
  }
}
