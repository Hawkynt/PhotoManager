using Hawkynt.PhotoManager.Core.Previews;

namespace Hawkynt.PhotoManager.Tests.Unit.Previews;

[TestFixture]
public class PreviewCacheKeyTests {
  [Test]
  public void For_ReturnsKey_WithFilePathSizeAndMtime() {
    var path = Path.Combine(Path.GetTempPath(), $"preview-key-{Guid.NewGuid():N}.bin");
    File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
    try {
      var fi = new FileInfo(path);
      var key = PreviewCacheKey.For(fi, maxEdgePixels: 256);
      Assert.Multiple(() => {
        Assert.That(key.FullPath, Is.EqualTo(fi.FullName));
        Assert.That(key.SizeBytes, Is.EqualTo(5));
        Assert.That(key.LastWriteTicks, Is.EqualTo(fi.LastWriteTimeUtc.Ticks));
        Assert.That(key.MaxEdgePixels, Is.EqualTo(256));
      });
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void For_DifferentSize_ProducesDifferentKey() {
    var path = Path.Combine(Path.GetTempPath(), $"preview-key-{Guid.NewGuid():N}.bin");
    File.WriteAllBytes(path, new byte[] { 1, 2 });
    try {
      var keySmall = PreviewCacheKey.For(new FileInfo(path), 128);
      File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
      var keyLarger = PreviewCacheKey.For(new FileInfo(path), 128);
      Assert.That(keySmall, Is.Not.EqualTo(keyLarger));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void For_DifferentMaxEdge_ProducesDifferentKey() {
    var path = Path.Combine(Path.GetTempPath(), $"preview-key-{Guid.NewGuid():N}.bin");
    File.WriteAllBytes(path, new byte[] { 1 });
    try {
      var fi = new FileInfo(path);
      Assert.That(PreviewCacheKey.For(fi, 64), Is.Not.EqualTo(PreviewCacheKey.For(fi, 128)));
    } finally {
      File.Delete(path);
    }
  }
}
