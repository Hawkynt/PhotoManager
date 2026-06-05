using NUnit.Framework;
using Hawkynt.PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Tests.Unit.Develop;

[TestFixture]
[Category("Unit")]
public sealed class DevelopGifTests {
  /// <summary>
  /// End-to-end regression: load a partly-transparent GIF, run the develop
  /// pipeline with identity settings, save as JPEG. The JPEG bytes must not
  /// be a pure-black image — this is the smartphone-screenshot bug the
  /// user reported where opening a GIF in develop produced a black preview.
  /// </summary>
  [Test]
  public async Task Loading_developing_and_jpeg_encoding_a_transparent_gif_does_not_yield_black() {
    var dir = Path.Combine(Path.GetTempPath(), "pm-dev-gif-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "halfred.gif");

      // 32×32: left half opaque red, right half fully transparent.
      using (var src = new Image<Rgba32>(32, 32)) {
        src.ProcessPixelRows(accessor => {
          for (var y = 0; y < 32; y++) {
            var row = accessor.GetRowSpan(y);
            for (var x = 0; x < 32; x++)
              row[x] = x < 16
                ? new Rgba32((byte)255, (byte)0, (byte)0, (byte)255)
                : new Rgba32((byte)0, (byte)0, (byte)0, (byte)0);
          }
        });
        await src.SaveAsync(path, new GifEncoder());
      }

      using var loaded = await RawImageLoader.LoadAsync(new FileInfo(path));
      using var developed = ImageDeveloper.Apply(loaded, new DevelopSettings());
      using var ms = new MemoryStream();
      developed.SaveAsJpeg(ms);
      ms.Position = 0;
      using var roundtrip = await Image.LoadAsync<Rgba32>(ms);

      // At least one pixel must be non-black. JPEG round-tripping a pure
      // black image would yield all-zero RGB; if anything non-zero comes
      // back, we've avoided the original bug.
      var brightestSeen = (byte)0;
      roundtrip.ProcessPixelRows(accessor => {
        for (var y = 0; y < accessor.Height; y++) {
          var row = accessor.GetRowSpan(y);
          for (var x = 0; x < row.Length; x++) {
            var max = Math.Max(row[x].R, Math.Max(row[x].G, row[x].B));
            if (max > brightestSeen) brightestSeen = max;
          }
        }
      });

      Assert.That(brightestSeen, Is.GreaterThan(50),
        $"Developed JPEG of a transparent GIF is too dark — brightest channel = {brightestSeen}. Suggests the flatten / develop chain dropped colour.");
    } finally {
      try { Directory.Delete(dir, recursive: true); } catch { }
    }
  }
}
