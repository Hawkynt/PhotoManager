using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Benchmarks;

/// <summary>
/// Measures the bulk Span.CopyTo-based ReplaceContents.
/// Run with: dotnet run -c Release -- --filter *ReplaceContents*
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class ReplaceContentsBenchmarks {
  private Image<Rgba32> _src = null!;
  private Image<Rgba32> _dst = null!;

  [Params(512, 1024, 2048, 4096)]
  public int ImageSize { get; set; }

  [GlobalSetup]
  public void Setup() {
    _src = new Image<Rgba32>(ImageSize, ImageSize);
    _dst = new Image<Rgba32>(ImageSize, ImageSize);
    _src.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new Rgba32((byte)(x & 0xFF), (byte)(y & 0xFF), 128, 255);
      }
    });
  }

  [GlobalCleanup]
  public void Cleanup() {
    _src.Dispose();
    _dst.Dispose();
  }

  [Benchmark]
  public void BulkSpanCopyTo() {
    _dst.ProcessPixelRows(_src, (dstAcc, srcAcc) => {
      for (var y = 0; y < dstAcc.Height; y++)
        srcAcc.GetRowSpan(y).CopyTo(dstAcc.GetRowSpan(y));
    });
  }

  [Benchmark(Baseline = true)]
  public void PerPixelAssignment() {
    var pixels = new Rgba32[_src.Width * _src.Height];
    _src.CopyPixelDataTo(pixels);
    _dst.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var srcOff = y * _dst.Width;
        for (var x = 0; x < row.Length; x++)
          row[x] = pixels[srcOff + x];
      }
    });
  }
}
