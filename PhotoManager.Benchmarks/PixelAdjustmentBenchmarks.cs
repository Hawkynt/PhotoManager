using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Benchmarks;

/// <summary>
/// Measures the per-pixel adjustment pipeline under all four code paths:
/// scalar+sequential, scalar+parallel, SIMD+sequential, SIMD+parallel.
/// Run with: dotnet run -c Release -- --filter *PixelAdjustment*
/// </summary>
[SimpleJob(RuntimeMoniker.Net80)]
[MemoryDiagnoser]
public class PixelAdjustmentBenchmarks {
  private Image<Rgba32> _image = null!;
  private DevelopSettings _basicSettings = null!;
  private DevelopSettings _fullSettings = null!;

  [Params(512, 1024, 2048)]
  public int ImageSize { get; set; }

  [GlobalSetup]
  public void Setup() {
    _image = new Image<Rgba32>(ImageSize, ImageSize);
    _image.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new Rgba32((byte)((x * 7 + y * 3) & 0xFF), (byte)((x * 5 + y * 11) & 0xFF), (byte)((x * 13 + y * 2) & 0xFF), 255);
      }
    });
    _basicSettings = new DevelopSettings(
      ExposureStops: 0.5, ContrastPercent: 20,
      SaturationPercent: 15, TemperatureShift: 10);
    _fullSettings = new DevelopSettings(
      ExposureStops: 0.5, ContrastPercent: 25, SaturationPercent: 15,
      HighlightsPercent: -30, ShadowsPercent: 25,
      TemperatureShift: 12, TintShift: -5,
      ClarityPercent: 20, DehazePercent: 15,
      VibrancePercent: 20,
      GradeShadowHue: 220, GradeShadowSat: 15,
      GradeHighlightHue: 45, GradeHighlightSat: 20,
      VignetteAmount: -25, VignetteMidpoint: 50,
      GrainAmount: 15, GrainSize: 30, GrainFrequency: 60);
  }

  [GlobalCleanup]
  public void Cleanup() => _image.Dispose();

  [Benchmark(Baseline = true)]
  public void ScalarSequential_Basic() {
    ImageDeveloper.ForceScalarPath = true;
    ImageDeveloper.ForceSequentialPath = true;
    using var clone = _image.Clone();
    ImageDeveloper.ApplyPixelAdjustments(clone, _basicSettings);
    ImageDeveloper.ForceScalarPath = false;
    ImageDeveloper.ForceSequentialPath = false;
  }

  [Benchmark]
  public void ScalarParallel_Basic() {
    ImageDeveloper.ForceScalarPath = true;
    ImageDeveloper.ForceSequentialPath = false;
    using var clone = _image.Clone();
    ImageDeveloper.ApplyPixelAdjustments(clone, _basicSettings);
    ImageDeveloper.ForceScalarPath = false;
  }

  [Benchmark]
  public void SimdSequential_Basic() {
    ImageDeveloper.ForceScalarPath = false;
    ImageDeveloper.ForceSequentialPath = true;
    using var clone = _image.Clone();
    ImageDeveloper.ApplyPixelAdjustments(clone, _basicSettings);
    ImageDeveloper.ForceSequentialPath = false;
  }

  [Benchmark]
  public void SimdParallel_Basic() {
    using var clone = _image.Clone();
    ImageDeveloper.ApplyPixelAdjustments(clone, _basicSettings);
  }

  [Benchmark]
  public void ScalarSequential_Full() {
    ImageDeveloper.ForceScalarPath = true;
    ImageDeveloper.ForceSequentialPath = true;
    using var clone = _image.Clone();
    ImageDeveloper.ApplyPixelAdjustments(clone, _fullSettings);
    ImageDeveloper.ForceScalarPath = false;
    ImageDeveloper.ForceSequentialPath = false;
  }

  [Benchmark]
  public void SimdParallel_Full() {
    using var clone = _image.Clone();
    ImageDeveloper.ApplyPixelAdjustments(clone, _fullSettings);
  }
}
