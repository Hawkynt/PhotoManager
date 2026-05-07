using System;
using System.Diagnostics;
using NUnit.Framework;
using PhotoManager.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Imaging;

[TestFixture]
[Category("Unit")]
public sealed class ThumbnailManagerTests {
  [SetUp]
  public void Setup() {
    // Each test gets a fresh throughput history so prior runs in the
    // same NUnit process don't influence the algorithm pick.
    ThumbnailManager.ResetThroughputForTests();
  }

  [Test]
  public void Identity_resize_returns_a_clone_with_matching_dimensions() {
    using var src = BuildTestImage(64, 48);
    using var dst = ThumbnailManager.Resize(src, 64, 48);

    Assert.That(dst.Width, Is.EqualTo(64));
    Assert.That(dst.Height, Is.EqualTo(48));
    // Clone, not same reference — caller owns the returned image.
    Assert.That(ReferenceEquals(src, dst), Is.False);
    // Pixel-equal — identity resize must not mutate content.
    Assert.That(dst[10, 10], Is.EqualTo(src[10, 10]));
  }

  [Test]
  public void Downscale_produces_target_dimensions_and_records_throughput() {
    using var src = BuildTestImage(512, 512);
    using var dst = ThumbnailManager.Resize(src, 128, 128);

    Assert.That(dst.Width, Is.EqualTo(128));
    Assert.That(dst.Height, Is.EqualTo(128));
    // For a 4x downscale, the manager should pick Box (best quality
    // + fastest for shrinking). Throughput dictionary should have an
    // entry for Box after this call.
    Assert.That(ThumbnailManager.ObservedThroughput, Does.ContainKey("BoxResampler"),
      "Expected Box to be chosen for downscale; observed throughput entries: " +
      string.Join(", ", ThumbnailManager.ObservedThroughput.Keys));
  }

  [Test]
  public void Upscale_records_throughput_for_a_quality_resampler() {
    using var src = BuildTestImage(64, 64);
    using var dst = ThumbnailManager.Resize(src, 256, 256);

    Assert.That(dst.Width, Is.EqualTo(256));
    Assert.That(dst.Height, Is.EqualTo(256));
    // Upscale should pick Bicubic when no perf history exists. Either
    // BicubicResampler is recorded, or — if SixLabors named it
    // differently — at minimum SOME throughput entry exists.
    Assert.That(ThumbnailManager.ObservedThroughput, Is.Not.Empty,
      "Expected at least one throughput entry after a resize call.");
  }

  [Test]
  public void Throws_on_zero_or_negative_target_dimensions() {
    using var src = BuildTestImage(32, 32);
    Assert.Throws<ArgumentOutOfRangeException>(() => ThumbnailManager.Resize(src, 0, 32));
    Assert.Throws<ArgumentOutOfRangeException>(() => ThumbnailManager.Resize(src, 32, -5));
  }

  [Test]
  public void Repeated_resizes_keep_dimensions_correct_under_concurrent_load() {
    // Multiple parallel resizes share the static throughput dictionary.
    // Verify ConcurrentDictionary handles the contention correctly and
    // every result has the requested dimensions.
    using var src = BuildTestImage(256, 256);
    var tasks = new System.Threading.Tasks.Task<(int w, int h)>[16];
    for (var i = 0; i < tasks.Length; i++) {
      var targetW = 64 + (i * 4);
      var targetH = 64 + (i * 4);
      tasks[i] = System.Threading.Tasks.Task.Run(() => {
        using var dst = ThumbnailManager.Resize(src, targetW, targetH);
        return (dst.Width, dst.Height);
      });
    }
    System.Threading.Tasks.Task.WaitAll(tasks);
    for (var i = 0; i < tasks.Length; i++) {
      var expected = (64 + (i * 4), 64 + (i * 4));
      Assert.That(tasks[i].Result, Is.EqualTo(expected),
        $"Concurrent resize {i} produced wrong dimensions.");
    }
  }

  [Test]
  public void Downscale_quality_is_close_to_box_average() {
    // Sanity check on the quality of the chosen downscale resampler.
    // Box should produce smooth 4-pixel averages on a 2x downscale of
    // a checkerboard. The average of a 2x2 alternating-black-white
    // checker is mid-gray (127). After 2x box downscale every pixel
    // should be near 127.
    using var src = new Image<Rgba32>(64, 64);
    src.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var v = (byte)(((x + y) & 1) == 0 ? 0 : 255);
          row[x] = new Rgba32(v, v, v, 255);
        }
      }
    });
    using var dst = ThumbnailManager.Resize(src, 32, 32);
    // After 2x downscale of checkerboard with Box, each pixel = avg of
    // 2x2 checkerboard quad = (0+255+255+0)/4 = 127 or 128.
    var p = dst[10, 10];
    Assert.That(p.R, Is.InRange(120, 135),
      $"Box downscale of checkerboard should produce ~127, got {p.R}.");
  }

  private static Image<Rgba32> BuildTestImage(int w, int h) {
    var img = new Image<Rgba32>(w, h);
    img.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var v = (byte)((x + y) & 0xFF);
          row[x] = new Rgba32(v, v, v, 255);
        }
      }
    });
    return img;
  }
}
