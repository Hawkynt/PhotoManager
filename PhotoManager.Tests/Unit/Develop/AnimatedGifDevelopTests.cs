using NUnit.Framework;
using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
[Category("Unit")]
public sealed class AnimatedGifDevelopTests {
  /// <summary>
  /// Multi-frame animated GIF: frame 0 = red, frame 1 = blue, frame 2 = transparent.
  /// After load + develop + JPEG encode, the saved JPEG must reflect frame 0
  /// (red), not a transparent / black later frame, and the develop preview
  /// must show coloured pixels — not black, not blank.
  /// </summary>
  [Test]
  public async Task Animated_gif_develop_preview_shows_frame_zero_correctly() {
    var dir = Path.Combine(Path.GetTempPath(), "pm-anim-gif-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, "anim.gif");

      // Frame 0: opaque red. Frame 1: opaque blue. Frame 2: transparent.
      using (var src = new Image<Rgba32>(32, 32, new Rgba32((byte)255, (byte)0, (byte)0, (byte)255))) {
        var blueFrame = new Image<Rgba32>(32, 32, new Rgba32((byte)0, (byte)0, (byte)255, (byte)255));
        src.Frames.AddFrame(blueFrame.Frames.RootFrame);
        var transparentFrame = new Image<Rgba32>(32, 32, new Rgba32((byte)0, (byte)0, (byte)0, (byte)0));
        src.Frames.AddFrame(transparentFrame.Frames.RootFrame);
        await src.SaveAsync(path, new GifEncoder());
      }

      using var loaded = await RawImageLoader.LoadAsync(new FileInfo(path));
      using var developed = ImageDeveloper.Apply(loaded, new DevelopSettings());
      using var ms = new MemoryStream();
      developed.SaveAsJpeg(ms);
      ms.Position = 0;
      using var roundtrip = await Image.LoadAsync<Rgba32>(ms);

      // Sample several pixels — frame 0 was opaque red, so the rendered
      // JPEG must show red-dominant pixels everywhere (high R, low G+B).
      var redCount = 0;
      var blackCount = 0;
      roundtrip.ProcessPixelRows(accessor => {
        for (var y = 0; y < accessor.Height; y++) {
          var row = accessor.GetRowSpan(y);
          for (var x = 0; x < row.Length; x++) {
            if (row[x].R > 150 && row[x].G < 100 && row[x].B < 100) redCount++;
            if (row[x].R < 30 && row[x].G < 30 && row[x].B < 30) blackCount++;
          }
        }
      });

      Assert.That(redCount, Is.GreaterThan(500),
        $"Frame-0 red should dominate the developed JPEG, but only {redCount} red pixels found.");
      Assert.That(blackCount, Is.LessThan(100),
        $"Developed JPEG must not be mostly black, but {blackCount} near-black pixels found.");
    } finally {
      try { Directory.Delete(dir, recursive: true); } catch { }
    }
  }
}
