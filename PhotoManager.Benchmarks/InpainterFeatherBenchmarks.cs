using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Hawkynt.PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Benchmarks;

/// <summary>
/// Compares the old brute-force ComputeFeatherWeight against the new
/// Chebyshev distance transform for the inpainter's feathered blend.
/// Run with: dotnet run -c Release -- --filter *Feather*
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class InpainterFeatherBenchmarks {
  private float[] _mask = null!;

  [Params(256, 512)]
  public int TileSize { get; set; }

  [Params(25, 50)]
  public int MaskPercent { get; set; }

  [GlobalSetup]
  public void Setup() {
    _mask = new float[TileSize * TileSize];
    var rng = new Random(42);
    for (var i = 0; i < _mask.Length; i++)
      _mask[i] = rng.Next(100) < MaskPercent ? 1f : 0f;
  }

  [Benchmark(Baseline = true)]
  public int BruteForce_PerPixel() {
    const int featherPx = 6;
    var sum = 0;
    for (var y = 0; y < TileSize; y++)
      for (var x = 0; x < TileSize; x++)
        if (_mask[y * TileSize + x] > 0)
          sum += (int)(OnnxInpainter.ComputeFeatherWeight(_mask, TileSize, x, y, featherPx) * 100);
    return sum;
  }

  [Benchmark]
  public int DistanceTransform_Lookup() {
    const int featherPx = 6;
    var distMap = OnnxInpainter.BuildChebyshevDistanceMap(_mask, TileSize, TileSize, 0, 0, TileSize, TileSize, featherPx);
    var sum = 0;
    for (var y = 0; y < TileSize; y++)
      for (var x = 0; x < TileSize; x++)
        if (_mask[y * TileSize + x] > 0) {
          var d = distMap[y * TileSize + x];
          sum += (int)(Math.Min(1f, (float)d / featherPx) * 100);
        }
    return sum;
  }
}
