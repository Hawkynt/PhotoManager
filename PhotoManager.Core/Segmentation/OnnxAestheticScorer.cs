using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.Core.Segmentation;

/// <summary>
/// ONNX-backed aesthetic-quality scorer using NIMA (Talebi & Milanfar,
/// "Neural Image Assessment", 2017) with a MobileNetV2 backbone. Predicts
/// a probability distribution over 10 score bins (1.0 to 10.0); the
/// reported aesthetic score is the expected value
/// <c>Σ_i (i + 1) · p_i</c> for i = 0..9.
///
/// Typical aesthetic scores on real photos: 3.5–4.5 for snapshots,
/// 5.0–6.5 for competent shots, 6.5+ for portfolio-quality work. The
/// AVA dataset that NIMA was trained on rarely exceeds 7.5 even for
/// top-rated images.
///
/// Input:  [1, 3, 224, 224] float32 ImageNet-normalised RGB.
/// Output: [1, 10] softmax probabilities.
///
/// Returns NaN when the model isn't installed — same degrade-gracefully
/// pattern as the other ONNX wrappers (callers should null-check
/// <see cref="IsAvailable"/> or check for double.NaN before storing).
/// </summary>
public sealed class OnnxAestheticScorer : IDisposable {
  public const string DefaultModelFileName = "nima-mobilenetv2.onnx";
  public const int InputSize = 224;

  private static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
  private static readonly float[] ImageNetStd  = [0.229f, 0.224f, 0.225f];

  private readonly Lazy<InferenceSession?> _session;

  public OnnxAestheticScorer(FileInfo? modelFile = null) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._session = new Lazy<InferenceSession?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Returns the predicted aesthetic score in [1.0, 10.0], or
  /// <see cref="double.NaN"/> when the model isn't installed / fails.
  /// Also returns the standard deviation of the predicted distribution
  /// — high std indicates the model is uncertain, low std means a
  /// confident prediction.
  /// </summary>
  public AestheticScore? Score(Image<Rgba32> source, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    var session = this._session.Value;
    if (session == null)
      return null;
    ct.ThrowIfCancellationRequested();
    return RunInference(session, source, ct);
  }

  public async Task<AestheticScore?> ScoreAsync(Image<Rgba32> source, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    var session = this._session.Value;
    if (session == null)
      return null;
    return await Task.Run(() => RunInference(session, source, ct), ct);
  }

  private static AestheticScore? RunInference(InferenceSession session, Image<Rgba32> source, CancellationToken ct) {
    using var resized = source.Clone(c => c.Resize(InputSize, InputSize));
    ct.ThrowIfCancellationRequested();

    var input = BuildInput(resized);
    float[] distribution;
    try {
      var inputName = session.InputMetadata.Keys.First();
      var tensor = new DenseTensor<float>(input, new[] { 1, 3, InputSize, InputSize });
      using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
      distribution = results.First().AsTensor<float>().ToArray();
    } catch {
      return null;
    }

    if (distribution.Length != 10)
      return null;

    // Expected value over score bins 1..10 — that's the NIMA "mean score".
    double mean = 0;
    for (var i = 0; i < 10; i++)
      mean += (i + 1) * distribution[i];

    // Standard deviation — uncertainty signal.
    double variance = 0;
    for (var i = 0; i < 10; i++)
      variance += distribution[i] * Math.Pow((i + 1) - mean, 2);
    var std = Math.Sqrt(variance);

    return new AestheticScore(mean, std, distribution);
  }

  /// <summary>
  /// Pack RGB pixels into [1, 3, 224, 224] float tensor with ImageNet
  /// normalisation.
  /// </summary>
  internal static float[] BuildInput(Image<Rgba32> image) {
    var pixelCount = InputSize * InputSize;
    var tensor = new float[3 * pixelCount];
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < InputSize; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < InputSize; x++) {
          var px = row[x];
          var off = y * InputSize + x;
          tensor[0 * pixelCount + off] = (px.R / 255f - ImageNetMean[0]) / ImageNetStd[0];
          tensor[1 * pixelCount + off] = (px.G / 255f - ImageNetMean[1]) / ImageNetStd[1];
          tensor[2 * pixelCount + off] = (px.B / 255f - ImageNetMean[2]) / ImageNetStd[2];
        }
      }
    });
    return tensor;
  }

  private static InferenceSession? TryOpenSession(FileInfo modelFile) {
    try {
      if (!modelFile.Exists)
        return null;
      return OnnxAcceleration.CreateSession(modelFile.FullName);
    } catch {
      return null;
    }
  }

  public void Dispose() {
    // Sessions are cached by OnnxAcceleration and shared across instances.
  }
}

/// <summary>One NIMA prediction: mean score in [1, 10], standard deviation, and the raw 10-bin distribution.</summary>
public sealed record AestheticScore(double Mean, double StdDev, float[] Distribution);
