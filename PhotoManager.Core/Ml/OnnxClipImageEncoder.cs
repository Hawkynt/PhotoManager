using Microsoft.ML.OnnxRuntime;
using PhotoManager.Core.Segmentation;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Ml;

/// <summary>
/// CLIP / SigLIP vision encoder. Loads the ONNX model from
/// <see cref="AppDataPaths.ModelFile"/> lazily; if the model is missing the
/// encoder simply reports <see cref="IsAvailable"/> = false and every call
/// returns null so the caller can fall back gracefully.
///
/// Default normalisation is the SigLIP convention <c>(pixel/255 - 0.5) / 0.5</c>
/// (= [-1, 1]). Pass <see cref="NormalizationMode.ImageNet"/> to switch to the
/// classic CLIP / OpenAI ImageNet mean/std.
/// </summary>
public sealed class OnnxClipImageEncoder : IDisposable {
  public const string DefaultModelFileName = "siglip-vision.onnx";
  public const int DefaultInputSize = 224;

  public enum NormalizationMode {
    SigLIP = 0,
    ImageNet = 1
  }

  private static readonly float[] ImageNetMean = [0.48145466f, 0.4578275f, 0.40821073f];
  private static readonly float[] ImageNetStd = [0.26862954f, 0.26130258f, 0.27577711f];

  private readonly Lazy<InferenceSession?> _session;
  private readonly int _inputSize;
  private readonly NormalizationMode _normalization;

  public OnnxClipImageEncoder(
    FileInfo? modelFile = null,
    int inputSize = DefaultInputSize,
    NormalizationMode normalization = NormalizationMode.SigLIP
  ) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._inputSize = inputSize;
    this._normalization = normalization;
    this._session = new Lazy<InferenceSession?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Embeds <paramref name="source"/> by resizing to the model's expected
  /// square input, normalising per the configured convention, and running the
  /// session. The returned vector is L2-normalised.
  /// </summary>
  public float[]? Embed(Image<Rgba32> source) {
    ArgumentNullException.ThrowIfNull(source);
    var session = this._session.Value;
    if (session == null)
      return null;

    using var resized = source.Clone(c => c.Resize(this._inputSize, this._inputSize));
    var tensor = this.BuildInputTensor(resized);
    var inputName = session.InputMetadata.Keys.First();
    var input = new DenseTensor<float>(tensor, [1, 3, this._inputSize, this._inputSize]);

    using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
    var output = results.First().AsTensor<float>().ToArray();
    return AutoKeywordTagger.L2Normalize(output);
  }

  /// <summary>
  /// Convenience: loads the file via ImageSharp and embeds it. Returns null
  /// when the file can't be decoded.
  /// </summary>
  public float[]? Embed(FileInfo imageFile) {
    ArgumentNullException.ThrowIfNull(imageFile);
    if (!imageFile.Exists)
      return null;

    Image<Rgba32> image;
    try {
      image = Image.Load<Rgba32>(imageFile.FullName);
    } catch {
      return null;
    }
    using var owned = image;
    return this.Embed(owned);
  }

  private float[] BuildInputTensor(Image<Rgba32> image) {
    var size = this._inputSize;
    var tensor = new float[1 * 3 * size * size];
    var pixelCount = size * size;

    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < size; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < size; x++) {
          var px = row[x];
          var offset = y * size + x;
          var r = px.R / 255f;
          var g = px.G / 255f;
          var b = px.B / 255f;
          if (this._normalization == NormalizationMode.ImageNet) {
            tensor[0 * pixelCount + offset] = (r - ImageNetMean[0]) / ImageNetStd[0];
            tensor[1 * pixelCount + offset] = (g - ImageNetMean[1]) / ImageNetStd[1];
            tensor[2 * pixelCount + offset] = (b - ImageNetMean[2]) / ImageNetStd[2];
          } else {
            tensor[0 * pixelCount + offset] = (r - 0.5f) / 0.5f;
            tensor[1 * pixelCount + offset] = (g - 0.5f) / 0.5f;
            tensor[2 * pixelCount + offset] = (b - 0.5f) / 0.5f;
          }
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
    // Sessions are cached by OnnxAcceleration and shared across
    // instances; disposing them here would break the cache.
    // OnnxAcceleration.ResetCache() handles teardown if needed.
  }
}
