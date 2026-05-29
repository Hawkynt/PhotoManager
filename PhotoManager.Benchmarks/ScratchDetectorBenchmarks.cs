using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Benchmarks;

/// <summary>
/// Measures the classical Frangi scratch detector.
/// Run with: dotnet run -c Release -- --filter *ScratchDetector*
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class ScratchDetectorBenchmarks {
  private Image<Rgba32> _image = null!;

  [Params(256, 512)]
  public int ImageSize { get; set; }

  [GlobalSetup]
  public void Setup() {
    _image = new Image<Rgba32>(ImageSize, ImageSize);
    var rng = new Random(42);
    _image.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var v = (byte)(128 + rng.Next(-60, 60));
          row[x] = new Rgba32(v, v, v, 255);
        }
      }
    });
  }

  [GlobalCleanup]
  public void Cleanup() => _image.Dispose();

  [Benchmark]
  public void FrangiDetect() {
    using var mask = ScratchDetector.Detect(_image);
  }
}
