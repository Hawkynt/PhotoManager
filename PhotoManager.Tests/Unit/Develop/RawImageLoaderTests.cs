using NUnit.Framework;
using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
[Category("Unit")]
public sealed class RawImageLoaderTests {
  /// <summary>
  /// Regression: developing a transparent GIF used to produce an all-black
  /// preview because the JPEG encoder bakes alpha=0 to black. Loader must
  /// composite onto white so the develop pipeline sees opaque pixels.
  /// </summary>
  [Test]
  public async Task LoadAsync_flattens_alpha_onto_white_for_transparent_gifs() {
    var dir = Path.Combine(Path.GetTempPath(), "pm-loader-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "transparent.gif");

      // Build a 32×32 GIF: half opaque red, half transparent. ImageSharp's
      // GIF encoder maps the transparent half to the palette's transparent
      // index — round-tripping back through Load should leave those pixels
      // with alpha=0, which the loader then composites onto white.
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

      // Every pixel ends up opaque. The previously-transparent half is
      // either white (composited) or whatever GIF wrote — we only require
      // alpha=255 across the board because that's what fixes the bug.
      var allOpaque = true;
      var firstBad = (x: -1, y: -1, r: (byte)0, g: (byte)0, b: (byte)0, a: (byte)0);
      loaded.ProcessPixelRows(accessor => {
        for (var y = 0; y < accessor.Height; y++) {
          var row = accessor.GetRowSpan(y);
          for (var x = 0; x < row.Length; x++) {
            if (row[x].A != 255) {
              if (allOpaque) firstBad = (x, y, row[x].R, row[x].G, row[x].B, row[x].A);
              allOpaque = false;
            }
          }
        }
      });

      Assert.That(allOpaque, Is.True,
        $"Loader must flatten alpha. First non-opaque pixel: ({firstBad.x},{firstBad.y})=R{firstBad.r}G{firstBad.g}B{firstBad.b}A{firstBad.a}.");

      // Spot-check: the previously-transparent right half must NOT be
      // pure black — that's exactly the broken state we're fixing.
      var rightHalfHasNonBlack = false;
      loaded.ProcessPixelRows(accessor => {
        var row = accessor.GetRowSpan(16);
        for (var x = 16; x < 32; x++)
          if (row[x].R > 10 || row[x].G > 10 || row[x].B > 10) rightHalfHasNonBlack = true;
      });
      Assert.That(rightHalfHasNonBlack, Is.True,
        "Previously-transparent region must not be pure black — that's the bug we're regressing against.");
    } finally {
      try { Directory.Delete(dir, recursive: true); } catch { }
    }
  }
}
