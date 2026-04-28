using System.Text;
using PhotoManager.Core.Metadata.Containers;
using PhotoManager.Tests.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Metadata.Containers;

[TestFixture]
public class ContainerMetadataWriterTests {
  private DirectoryInfo _tempDir = null!;

  [SetUp]
  public void SetUp() {
    this._tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "pm-container-" + Guid.NewGuid().ToString("N")));
    this._tempDir.Create();
  }

  [TearDown]
  public void TearDown() {
    if (this._tempDir.Exists)
      this._tempDir.Delete(recursive: true);
  }

  private static byte[] MakeXmpBytes(string title) {
    var xmp = $@"<?xml version=""1.0""?>
<x:xmpmeta xmlns:x=""adobe:ns:meta/""><rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#""><rdf:Description xmlns:dc=""http://purl.org/dc/elements/1.1/""><dc:title><rdf:Alt><rdf:li xml:lang=""x-default"">{title}</rdf:li></rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta>";
    return Encoding.UTF8.GetBytes(xmp);
  }

  [TestCase(".jpg", true)]
  [TestCase(".JPEG", true)]
  [TestCase(".png", false)]
  [TestCase(".raw", false)]
  public void Jpeg_SupportsExtension_RecognisesJpegFamily(string ext, bool expected)
    => Assert.That(new JpegContainerMetadataWriter().SupportsExtension(ext), Is.EqualTo(expected));

  [Test]
  public async Task Jpeg_WriteXmpAsync_RealJpeg_ReturnsWritten() {
    var path = Path.Combine(this._tempDir.FullName, "x.jpg");
    TestJpegFactory.Write(path);  // emits a real JPEG

    var writer = new JpegContainerMetadataWriter();
    var result = await writer.WriteXmpAsync(new FileInfo(path), MakeXmpBytes("Hello"));

    Assert.That(result, Is.EqualTo(ContainerWriteResult.Written));

    // Round-trip: the bytes on disk must contain the new title.
    var raw = await File.ReadAllBytesAsync(path);
    var asString = Encoding.UTF8.GetString(raw);
    Assert.That(asString, Does.Contain("Hello"));
  }

  [Test]
  public async Task Jpeg_WriteXmpAsync_MissingFile_ReturnsFailed() {
    var path = Path.Combine(this._tempDir.FullName, "ghost.jpg");
    var writer = new JpegContainerMetadataWriter();
    var result = await writer.WriteXmpAsync(new FileInfo(path), MakeXmpBytes("x"));
    Assert.That(result, Is.EqualTo(ContainerWriteResult.Failed));
  }

  [Test]
  public async Task Jpeg_WriteXmpAsync_NonJpegExtension_ReturnsNotSupported() {
    var path = Path.Combine(this._tempDir.FullName, "fake.png");
    File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    var writer = new JpegContainerMetadataWriter();
    var result = await writer.WriteXmpAsync(new FileInfo(path), MakeXmpBytes("x"));
    Assert.That(result, Is.EqualTo(ContainerWriteResult.NotSupported));
  }

  [Test]
  public async Task Jpeg_WriteXmpAsync_GarbageContent_ReturnsFailed() {
    // .jpg extension but the bytes aren't a parseable JPEG.
    var path = Path.Combine(this._tempDir.FullName, "lying.jpg");
    File.WriteAllBytes(path, new byte[] { 0x00, 0x00, 0x00, 0x00 });
    var writer = new JpegContainerMetadataWriter();
    var result = await writer.WriteXmpAsync(new FileInfo(path), MakeXmpBytes("x"));
    Assert.That(result, Is.EqualTo(ContainerWriteResult.Failed));
  }

  [TestCase(".png", true)]
  [TestCase(".jpg", false)]
  public void Png_SupportsExtension(string ext, bool expected)
    => Assert.That(new PngContainerMetadataWriter().SupportsExtension(ext), Is.EqualTo(expected));

  [Test]
  public async Task Png_WriteXmpAsync_RealPng_ReturnsWritten() {
    var path = Path.Combine(this._tempDir.FullName, "x.png");
    using (var img = new Image<Rgb24>(2, 2))
      img.SaveAsPng(path);

    var writer = new PngContainerMetadataWriter();
    var result = await writer.WriteXmpAsync(new FileInfo(path), MakeXmpBytes("PngTitle"));

    Assert.That(result, Is.EqualTo(ContainerWriteResult.Written));
    var asString = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
    Assert.That(asString, Does.Contain("PngTitle"));
  }

  [TestCase(".webp", true)]
  [TestCase(".jpg", false)]
  public void Webp_SupportsExtension(string ext, bool expected)
    => Assert.That(new WebpContainerMetadataWriter().SupportsExtension(ext), Is.EqualTo(expected));

  [Test]
  public async Task Webp_WriteXmpAsync_RealWebp_ReturnsWritten() {
    var path = Path.Combine(this._tempDir.FullName, "x.webp");
    using (var img = new Image<Rgb24>(4, 4))
      img.SaveAsWebp(path);

    var writer = new WebpContainerMetadataWriter();
    var result = await writer.WriteXmpAsync(new FileInfo(path), MakeXmpBytes("WebpTitle"));

    Assert.That(result, Is.EqualTo(ContainerWriteResult.Written));
  }

  [TestCase(".tif", true)]
  [TestCase(".tiff", true)]
  [TestCase(".jpg", false)]
  public void Tiff_SupportsExtension(string ext, bool expected)
    => Assert.That(new TiffContainerMetadataWriter().SupportsExtension(ext), Is.EqualTo(expected));

  [Test]
  public async Task Tiff_WriteXmpAsync_RealTiff_ReturnsWritten() {
    var path = Path.Combine(this._tempDir.FullName, "x.tif");
    using (var img = new Image<Rgb24>(2, 2))
      img.SaveAsTiff(path);

    var writer = new TiffContainerMetadataWriter();
    var result = await writer.WriteXmpAsync(new FileInfo(path), MakeXmpBytes("TiffTitle"));

    Assert.That(result, Is.EqualTo(ContainerWriteResult.Written));
  }
}
