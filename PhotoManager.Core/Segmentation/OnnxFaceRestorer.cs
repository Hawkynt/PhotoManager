using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.Core.Segmentation;

/// <summary>
/// ONNX-backed face restorer — handles GFPGAN (1 input) and CodeFormer
/// (2 inputs: image + fidelity weight scalar). Same degrade-gracefully
/// pattern as <see cref="OnnxDenoiser"/>: <see cref="IsAvailable"/> false
/// when the model is missing and <see cref="Restore"/> returns null in
/// that case.
///
/// GFPGAN: 512×512 face crop (RGB), normalised to [-1, 1] —
/// each channel is <c>(value/255 - 0.5) / 0.5</c>. Output is the same
/// shape and range; denormalise via <c>output * 0.5 + 0.5</c> and clamp
/// to [0, 1].
///
/// CodeFormer: 512×512 face crop with the same [-1, 1] normalisation
/// PLUS a scalar "weight" input (fidelity 0..1). 0.0 → strongest
/// restoration (may diverge from source identity); 1.0 → closest to
/// source. Default 0.7 keeps identity while still cleaning damage.
///
/// The caller is responsible for detecting / cropping the face region
/// and blending the restored output back into the source.
/// </summary>
public sealed class OnnxFaceRestorer : IDisposable {
  public const string DefaultModelFileName = "face-restore-gfpgan.onnx";

  /// <summary>The dimension GFPGAN's ONNX export is fixed to. Crops are resized to this before inference and the result resized back.</summary>
  public const int InputSize = 512;

  /// <summary>Default CodeFormer fidelity — biases toward preserving source identity while still cleaning damage.</summary>
  public const float DefaultCodeFormerWeight = 0.7f;

  private readonly Lazy<SessionInfo?> _session;
  private readonly float _codeFormerWeight;

  public OnnxFaceRestorer(FileInfo? modelFile = null, float codeFormerWeight = DefaultCodeFormerWeight) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._codeFormerWeight = Math.Clamp(codeFormerWeight, 0f, 1f);
    this._session = new Lazy<SessionInfo?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Run face restoration on a crop. <paramref name="faceCrop"/> is
  /// resized internally to 512×512 before inference. Returns a freshly
  /// allocated 512×512 restored image (caller resizes back to the
  /// source's face region). Returns null when no model is available.
  /// </summary>
  /// <param name="faceCrop">Square-ish RGB crop centred on a face. Any
  ///   resolution; we resize to 512×512 inside.</param>
  /// <param name="ct">Cancellation throws between pre/post and inference.</param>
  public Image<Rgba32>? Restore(Image<Rgba32> faceCrop, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(faceCrop);
    var info = this._session.Value;
    if (info == null)
      return null;

    ct.ThrowIfCancellationRequested();

    using var resized = faceCrop.Clone(c => c.Resize(InputSize, InputSize));
    var input = BuildInput(resized);

    ct.ThrowIfCancellationRequested();

    float[] output;
    try {
      var tensor = new DenseTensor<float>(input, new[] { 1, 3, InputSize, InputSize });
      var inputs = new List<NamedOnnxValue> {
        NamedOnnxValue.CreateFromTensor(info.InputName, tensor)
      };
      if (info.WeightInputName != null) {
        // CodeFormer's "weight" is a scalar (0-d tensor).
        var weightTensor = new DenseTensor<double>(new[] { (double)this._codeFormerWeight }, Array.Empty<int>());
        inputs.Add(NamedOnnxValue.CreateFromTensor(info.WeightInputName, weightTensor));
      }
      using var results = info.Session.Run(inputs);
      // Pick the named "output" tensor when present (CodeFormer also emits
      // logits + features auxiliary outputs); fall back to first.
      var outResult = results.FirstOrDefault(r => string.Equals(r.Name, "output", StringComparison.OrdinalIgnoreCase))
                      ?? results.First();
      output = outResult.AsTensor<float>().ToArray();
    } catch {
      return null;
    }

    ct.ThrowIfCancellationRequested();

    return TensorToImage(output, InputSize, InputSize);
  }

  /// <summary>
  /// Pack RGB pixels into a [1, 3, 512, 512] float tensor normalised to
  /// [-1, 1] — the convention every GFPGAN ONNX export uses. Channel
  /// ordering is RGB (not BGR); the upstream PyTorch path converts BGR
  /// numpy arrays to RGB tensors before feeding the network, and the
  /// ONNX export preserves that.
  /// </summary>
  private static float[] BuildInput(Image<Rgba32> image) {
    var pixelCount = InputSize * InputSize;
    var tensor = new float[3 * pixelCount];
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < InputSize; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < InputSize; x++) {
          var px = row[x];
          var off = y * InputSize + x;
          tensor[0 * pixelCount + off] = (px.R / 255f - 0.5f) / 0.5f;
          tensor[1 * pixelCount + off] = (px.G / 255f - 0.5f) / 0.5f;
          tensor[2 * pixelCount + off] = (px.B / 255f - 0.5f) / 0.5f;
        }
      }
    });
    return tensor;
  }

  private static Image<Rgba32> TensorToImage(float[] tensor, int w, int h) {
    var pixelCount = h * w;
    var image = new Image<Rgba32>(w, h);
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < w; x++) {
          var off = y * w + x;
          var r = Math.Clamp(tensor[0 * pixelCount + off] * 0.5f + 0.5f, 0f, 1f);
          var g = Math.Clamp(tensor[1 * pixelCount + off] * 0.5f + 0.5f, 0f, 1f);
          var b = Math.Clamp(tensor[2 * pixelCount + off] * 0.5f + 0.5f, 0f, 1f);
          row[x] = new Rgba32(ToByte(r), ToByte(g), ToByte(b), (byte)255);
        }
      }
    });
    return image;
  }

  private static byte ToByte(float v) {
    if (float.IsNaN(v) || v <= 0f) return 0;
    if (v >= 1f) return 255;
    return (byte)Math.Round(v * 255f);
  }

  private static SessionInfo? TryOpenSession(FileInfo modelFile) {
    try {
      if (!modelFile.Exists)
        return null;
      var session = OnnxAcceleration.CreateSession(modelFile.FullName);
      var keys = session.InputMetadata.Keys.ToList();
      // First 4-D NCHW input is the image. Any other input (typically the
      // CodeFormer "weight" scalar) goes in WeightInputName so we can feed it.
      string? imageName = null;
      string? weightName = null;
      foreach (var k in keys) {
        var dims = session.InputMetadata[k].Dimensions;
        if (imageName == null && dims.Length == 4)
          imageName = k;
        else
          weightName = k;
      }
      imageName ??= keys.First();
      return new SessionInfo(session, imageName, weightName);
    } catch {
      return null;
    }
  }

  public void Dispose() {
    // Sessions are cached by OnnxAcceleration and shared across
    // instances; disposing them here would break the cache.
    // OnnxAcceleration.ResetCache() handles teardown if needed.
  }

  private sealed record SessionInfo(InferenceSession Session, string InputName, string? WeightInputName);
}
