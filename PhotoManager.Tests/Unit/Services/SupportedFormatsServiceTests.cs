using Hawkynt.PhotoManager.Core.Services;

namespace Hawkynt.PhotoManager.Tests.Unit.Services;

[TestFixture]
public class SupportedFormatsServiceTests {
  private readonly SupportedFormatsService _service = new();

  [Test]
  public async Task GetSupportedExtensionsAsync_HasJpegPngAndRawSamples() {
    var ext = await this._service.GetSupportedExtensionsAsync();
    Assert.Multiple(() => {
      Assert.That(ext, Contains.Item("*.jpg"));
      Assert.That(ext, Contains.Item("*.png"));
      Assert.That(ext, Contains.Item("*.cr2"));
      // Sorted alphabetically.
      Assert.That(ext, Is.Ordered);
    });
  }

  [Test]
  public async Task GetSupportedExtensionsWithoutWildcardsAsync_NoLeadingStar() {
    var ext = await this._service.GetSupportedExtensionsWithoutWildcardsAsync();
    Assert.That(ext.All(e => e.StartsWith('.')), Is.True);
    Assert.That(ext, Contains.Item(".jpg"));
  }

  [TestCase(".jpg",  true)]
  [TestCase("JPG",   true,  Description = "Case-insensitive + auto-prepends dot")]
  [TestCase(".cr2",  true)]
  [TestCase(".heic", true,  Description = "iPhone HEIC — wired in via FileFormat.Heif")]
  [TestCase(".heif", true,  Description = "HEIC alias")]
  [TestCase(".avif", true,  Description = "Modern AVIF — wired in via FileFormat.Avif")]
  [TestCase(".psd",  true,  Description = "Photoshop")]
  [TestCase(".jp2",  true,  Description = "JPEG 2000")]
  [TestCase(".hdr",  true,  Description = "Radiance HDR")]
  [TestCase(".exr",  true,  Description = "OpenEXR")]
  [TestCase(".apng", true,  Description = "Animated PNG")]
  [TestCase(".dds",  true,  Description = "DirectDraw Surface (game textures)")]
  [TestCase(".pcx",  true)]
  [TestCase(".ico",  true)]
  [TestCase(".xyz",  false, Description = "Unknown extension")]
  [TestCase("",      false)]
  [TestCase(null,    false)]
  public void IsExtensionSupported_HandlesVariants(string? input, bool expected) {
    Assert.That(this._service.IsExtensionSupported(input!), Is.EqualTo(expected));
  }

  [Test]
  public async Task GetExtensionsByFormatAsync_ContainsJpegFamily() {
    var map = await this._service.GetExtensionsByFormatAsync();
    Assert.Multiple(() => {
      Assert.That(map.ContainsKey("JPEG"), Is.True);
      Assert.That(map["JPEG"], Contains.Item(".jpg"));
      Assert.That(map["JPEG"], Contains.Item(".jpeg"));
    });
  }
}
