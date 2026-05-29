using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Benchmarks;

/// <summary>
/// Measures histogram computation.
/// Run with: dotnet run -c Release -- --filter *Histogram*
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class HistogramBenchmarks {
  private Image<Rgba32> _image = null!;

  [Params(512, 1024, 2048)]
  public int ImageSize { get; set; }

  [GlobalSetup]
  public void Setup() {
    _image = new Image<Rgba32>(ImageSize, ImageSize);
    var rng = new Random(42);
    _image.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 255);
      }
    });
  }

  [GlobalCleanup]
  public void Cleanup() => _image.Dispose();

  [Benchmark]
  public void ComputeHistogram() {
    var h = HistogramAnalyzer.Compute(_image);
  }
}
