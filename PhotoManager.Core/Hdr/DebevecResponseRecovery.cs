using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Hdr;

/// <summary>
/// Per-channel camera response curves recovered from a bracket plus the
/// per-pixel HDR radiance map (R, G, B planes, linear-light, log-exposure
/// values returned in radiance directly via Exp).
/// </summary>
public sealed record HdrRadianceMap(
  int Width,
  int Height,
  float[] Red,
  float[] Green,
  float[] Blue,
  double[] ResponseRed,
  double[] ResponseGreen,
  double[] ResponseBlue
);

/// <summary>
/// Recover g(Z) (the non-linear camera response, log of exposure for each
/// pixel value 0..255) for each channel, then fold the bracket into one
/// linear-light radiance map. Implements Debevec &amp; Malik SIGGRAPH 1997
/// equations (3) and (5)-(6): minimise
///   sum_{i,j} { w(Z_ij) [g(Z_ij) - lnE_i - ln Δt_j] }^2
///   + λ sum_{z=1..254} { w(z) g''(z) }^2
/// constrained by g(127)=0. ~50 well-distributed samples per channel keep
/// the linear system tractable while staying over-determined.
/// </summary>
public static class DebevecResponseRecovery {
  public const int DefaultSamples = 50;
  public const double DefaultSmoothness = 30.0;
  public const int LevelCount = 256;

  public static HdrRadianceMap Recover(
    IReadOnlyList<Image<Rgba32>> exposures,
    IReadOnlyList<double> exposureTimesSeconds
  ) => Recover(exposures, exposureTimesSeconds, DefaultSamples, DefaultSmoothness);

  public static HdrRadianceMap Recover(
    IReadOnlyList<Image<Rgba32>> exposures,
    IReadOnlyList<double> exposureTimesSeconds,
    int sampleCount,
    double smoothness
  ) {
    if (exposures.Count < 2)
      throw new ArgumentException("Need at least two exposures for Debevec recovery.");
    if (exposures.Count != exposureTimesSeconds.Count)
      throw new ArgumentException("Exposure list and time list must align.");

    var w = exposures[0].Width;
    var h = exposures[0].Height;
    for (var i = 1; i < exposures.Count; i++)
      if (exposures[i].Width != w || exposures[i].Height != h)
        throw new ArgumentException("All bracket frames must share dimensions.");

    var lnDt = new double[exposureTimesSeconds.Count];
    for (var j = 0; j < lnDt.Length; j++)
      lnDt[j] = Math.Log(Math.Max(exposureTimesSeconds[j], 1e-9));

    var samplePixels = PickSamplePixels(w, h, sampleCount);
    var weights = BuildWeights();

    var pixelData = ExtractPixels(exposures, samplePixels);

    var responses = new double[3][];
    responses[0] = SolveResponse(pixelData.Red, lnDt, weights, smoothness);
    responses[1] = SolveResponse(pixelData.Green, lnDt, weights, smoothness);
    responses[2] = SolveResponse(pixelData.Blue, lnDt, weights, smoothness);

    var (red, green, blue) = BuildRadianceMap(exposures, lnDt, weights, responses);

    return new HdrRadianceMap(w, h, red, green, blue, responses[0], responses[1], responses[2]);
  }

  internal static double[] BuildWeights() {
    var w = new double[LevelCount];
    for (var z = 0; z < LevelCount; z++)
      w[z] = z <= 127 ? z + 1 : 256 - z;
    return w;
  }

  internal static (int X, int Y)[] PickSamplePixels(int width, int height, int sampleCount) {
    var n = Math.Max(1, Math.Min(sampleCount, width * height));
    var grid = (int)Math.Ceiling(Math.Sqrt(n));
    var picks = new List<(int X, int Y)>(grid * grid);
    for (var gy = 0; gy < grid; gy++) {
      for (var gx = 0; gx < grid; gx++) {
        var x = (int)((gx + 0.5) * width / grid);
        var y = (int)((gy + 0.5) * height / grid);
        if (x >= width) x = width - 1;
        if (y >= height) y = height - 1;
        picks.Add((x, y));
        if (picks.Count == n)
          return picks.ToArray();
      }
    }
    return picks.ToArray();
  }

  internal sealed record PixelSamples(byte[,] Red, byte[,] Green, byte[,] Blue);

  internal static PixelSamples ExtractPixels(IReadOnlyList<Image<Rgba32>> exposures, (int X, int Y)[] samples) {
    var n = samples.Length;
    var p = exposures.Count;
    var r = new byte[n, p];
    var g = new byte[n, p];
    var b = new byte[n, p];
    for (var j = 0; j < p; j++) {
      var img = exposures[j];
      img.ProcessPixelRows(accessor => {
        for (var i = 0; i < n; i++) {
          var (x, y) = samples[i];
          var pix = accessor.GetRowSpan(y)[x];
          r[i, j] = pix.R;
          g[i, j] = pix.G;
          b[i, j] = pix.B;
        }
      });
    }
    return new PixelSamples(r, g, b);
  }

  internal static double[] SolveResponse(byte[,] z, double[] lnDt, double[] weights, double smoothness) {
    var n = z.GetLength(0);
    var p = z.GetLength(1);
    var rows = n * p + (LevelCount - 2) + 1;
    var cols = LevelCount + n;

    var a = new double[rows, cols];
    var bv = new double[rows];

    var k = 0;
    for (var i = 0; i < n; i++) {
      for (var j = 0; j < p; j++) {
        var zij = z[i, j];
        var w = weights[zij];
        a[k, zij] = w;
        a[k, LevelCount + i] = -w;
        bv[k] = w * lnDt[j];
        k++;
      }
    }

    a[k, 127] = 1.0;
    bv[k] = 0.0;
    k++;

    for (var zi = 1; zi < LevelCount - 1; zi++) {
      var w = weights[zi] * smoothness;
      a[k, zi - 1] = w;
      a[k, zi] = -2 * w;
      a[k, zi + 1] = w;
      bv[k] = 0.0;
      k++;
    }

    var x = SolveLeastSquares(a, bv, rows, cols);
    var g = new double[LevelCount];
    Array.Copy(x, 0, g, 0, LevelCount);
    return g;
  }

  internal static double[] SolveLeastSquares(double[,] a, double[] b, int rows, int cols) {
    var ata = new double[cols, cols];
    var atb = new double[cols];
    for (var i = 0; i < cols; i++) {
      for (var j = i; j < cols; j++) {
        var s = 0.0;
        for (var r = 0; r < rows; r++)
          s += a[r, i] * a[r, j];
        ata[i, j] = s;
        ata[j, i] = s;
      }
      var sb = 0.0;
      for (var r = 0; r < rows; r++)
        sb += a[r, i] * b[r];
      atb[i] = sb;
    }

    return SolveSpd(ata, atb, cols);
  }

  internal static double[] SolveSpd(double[,] m, double[] rhs, int n) {
    const double eps = 1e-9;
    for (var i = 0; i < n; i++)
      m[i, i] += eps;

    var l = new double[n, n];
    for (var i = 0; i < n; i++) {
      for (var j = 0; j <= i; j++) {
        var sum = m[i, j];
        for (var k = 0; k < j; k++)
          sum -= l[i, k] * l[j, k];
        if (i == j) {
          if (sum <= 0)
            sum = 1e-12;
          l[i, i] = Math.Sqrt(sum);
        } else {
          l[i, j] = sum / l[j, j];
        }
      }
    }

    var y = new double[n];
    for (var i = 0; i < n; i++) {
      var sum = rhs[i];
      for (var k = 0; k < i; k++)
        sum -= l[i, k] * y[k];
      y[i] = sum / l[i, i];
    }

    var x = new double[n];
    for (var i = n - 1; i >= 0; i--) {
      var sum = y[i];
      for (var k = i + 1; k < n; k++)
        sum -= l[k, i] * x[k];
      x[i] = sum / l[i, i];
    }
    return x;
  }

  internal static (float[] Red, float[] Green, float[] Blue) BuildRadianceMap(
    IReadOnlyList<Image<Rgba32>> exposures,
    double[] lnDt,
    double[] weights,
    double[][] responses
  ) {
    var w = exposures[0].Width;
    var h = exposures[0].Height;
    var pixels = w * h;
    var red = new float[pixels];
    var green = new float[pixels];
    var blue = new float[pixels];
    var sumR = new double[pixels];
    var sumG = new double[pixels];
    var sumB = new double[pixels];
    var wR = new double[pixels];
    var wG = new double[pixels];
    var wB = new double[pixels];

    for (var j = 0; j < exposures.Count; j++) {
      var img = exposures[j];
      var dt = lnDt[j];
      img.ProcessPixelRows(accessor => {
        for (var y = 0; y < h; y++) {
          var row = accessor.GetRowSpan(y);
          var off = y * w;
          for (var x = 0; x < w; x++) {
            var px = row[x];
            var rWeight = weights[px.R];
            sumR[off + x] += rWeight * (responses[0][px.R] - dt);
            wR[off + x] += rWeight;
            var gWeight = weights[px.G];
            sumG[off + x] += gWeight * (responses[1][px.G] - dt);
            wG[off + x] += gWeight;
            var bWeight = weights[px.B];
            sumB[off + x] += bWeight * (responses[2][px.B] - dt);
            wB[off + x] += bWeight;
          }
        }
      });
    }

    for (var i = 0; i < pixels; i++) {
      var lnER = wR[i] > 0 ? sumR[i] / wR[i] : -10;
      var lnEG = wG[i] > 0 ? sumG[i] / wG[i] : -10;
      var lnEB = wB[i] > 0 ? sumB[i] / wB[i] : -10;
      red[i] = (float)Math.Exp(lnER);
      green[i] = (float)Math.Exp(lnEG);
      blue[i] = (float)Math.Exp(lnEB);
    }

    return (red, green, blue);
  }
}
