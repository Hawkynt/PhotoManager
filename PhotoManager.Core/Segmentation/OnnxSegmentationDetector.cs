using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Segmentation;

/// <summary>
/// ONNX-backed subject / portrait segmentation detector. Mirrors the
/// <see cref="PhotoManager.Core.Faces.OnnxFaceDetector"/> structure so the
/// lifecycle (lazy session, IsAvailable, async-over-Task.Run, IDisposable)
/// is consistent across model wrappers.
///
/// Targets the MODNet photographic-portrait-matting ONNX export
/// (Xenova/modnet on HuggingFace). The export's tensor metadata:
///   Input  name = "input",  shape = [1, 3, H, W] (dynamic H, W)
///   Output name = "output", shape = [1, 1, H, W] (alpha matte 0..1)
/// We resize the input to <see cref="DefaultInputSize"/> for inference;
/// the output alpha matte comes back at the same resolution and is
/// returned as an <see cref="Image{L8}"/> for downstream processing.
///
/// Returns null when the model file is missing — the same degrade-gracefully
/// pattern used by the face detector. The UI layer is expected to call
/// <c>EnsureModelAsync</c> first to drive the user through the download.
/// </summary>
public sealed class OnnxSegmentationDetector : IDisposable {
  /// <summary>The MODNet ONNX export's documented default input size.</summary>
  public const int DefaultInputSize = 512;

  private readonly Lazy<InferenceSession?> _session;
  private readonly int _inputSize;

  public OnnxSegmentationDetector(FileInfo modelFile, int inputSize = DefaultInputSize) {
    ArgumentNullException.ThrowIfNull(modelFile);
    this._inputSize = inputSize;
    this._session = new Lazy<InferenceSession?>(() => TryOpenSession(modelFile));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Runs segmentation on the given image and returns the alpha matte at the
  /// model's input resolution (caller can resize back to source dimensions
  /// or sample directly in normalised coords). Returns null when the model
  /// isn't installed or the source file doesn't exist.
  /// </summary>
  public async Task<Image<L8>?> SegmentAsync(FileInfo imageFile, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(imageFile);
    if (!imageFile.Exists)
      return null;

    var session = this._session.Value;
    if (session == null)
      return null;

    return await Task.Run(() => this.RunInference(session, imageFile), cancellationToken);
  }

  private Image<L8>? RunInference(InferenceSession session, FileInfo imageFile) {
    using var image = Image.Load<Rgba32>(imageFile.FullName);
    var tensor = BuildInputTensor(image, this._inputSize);

    var inputName = session.InputMetadata.Keys.First();
    var input = new DenseTensor<float>(tensor, new[] { 1, 3, this._inputSize, this._inputSize });

    using var results = session.Run(new[] {
      NamedOnnxValue.CreateFromTensor(inputName, input)
    });

    foreach (var r in results) {
      var t = r.AsTensor<float>();
      var dims = t.Dimensions;
      // MODNet output is [1, 1, H, W] alpha matte. Be lenient about shape so
      // a slightly differently exported variant still works.
      if (dims.Length != 4 || dims[0] != 1 || dims[1] != 1)
        continue;
      var h = dims[2];
      var w = dims[3];
      var raw = t.ToArray();
      return BuildAlphaImage(raw, w, h);
    }

    return null;
  }

  internal static float[] BuildInputTensor(Image<Rgba32> image, int inputSize) {
    image.Mutate(c => c.Resize(inputSize, inputSize));

    var tensor = new float[1 * 3 * inputSize * inputSize];
    var pixelCount = inputSize * inputSize;

    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < inputSize; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < inputSize; x++) {
          var px = row[x];
          var offset = y * inputSize + x;
          // MODNet's training pipeline normalises with (pixel/255 - 0.5) / 0.5
          // → values in [-1, 1]. Same as (pixel - 127.5) / 127.5.
          tensor[0 * pixelCount + offset] = (px.R - 127.5f) / 127.5f;
          tensor[1 * pixelCount + offset] = (px.G - 127.5f) / 127.5f;
          tensor[2 * pixelCount + offset] = (px.B - 127.5f) / 127.5f;
        }
      }
    });

    return tensor;
  }

  private static Image<L8> BuildAlphaImage(float[] raw, int width, int height) {
    var alpha = new Image<L8>(width, height);
    alpha.ProcessPixelRows(accessor => {
      for (var y = 0; y < height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < width; x++) {
          var v = raw[y * width + x];
          var byteValue = (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
          row[x] = new L8(byteValue);
        }
      }
    });
    return alpha;
  }

  private static InferenceSession? TryOpenSession(FileInfo modelFile) {
    try {
      if (!modelFile.Exists)
        return null;
      return new InferenceSession(modelFile.FullName);
    } catch {
      return null;
    }
  }

  public void Dispose() {
    if (this._session.IsValueCreated)
      this._session.Value?.Dispose();
  }
}
