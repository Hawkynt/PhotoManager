using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Benchmarks;

/// <summary>
/// Measures the separable dilation vs the naive multi-pass dilation.
/// Run with: dotnet run -c Release -- --filter *Dilate*
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class DilateMaskBenchmarks {
  private Image<Rgba32> _mask = null!;

  [Params(256, 512, 1024)]
  public int MaskSize { get; set; }

  [Params(1, 2, 4)]
  public int Radius { get; set; }

  [GlobalSetup]
  public void Setup() {
    _mask = new Image<Rgba32>(MaskSize, MaskSize);
    var rng = new Random(42);
    _mask.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          if (rng.Next(100) < 10)
            row[x] = new Rgba32(255, 0, 0, 200);
      }
    });
  }

  [GlobalCleanup]
  public void Cleanup() => _mask.Dispose();

  [Benchmark]
  public void SeparableDilation() {
    using var result = AutoScratchPipeline.DilateMask(_mask, Radius);
  }
}
