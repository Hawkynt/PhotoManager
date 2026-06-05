using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Hawkynt.PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.Core.Faces;

/// <summary>
/// ONNX-backed face detector. Expects an UltraFace-style model that takes a
/// 320×240 NCHW tensor normalized with (input - 127.0) / 128.0 and returns
/// two outputs: scores [1, N, 2] and boxes [1, N, 4]. Works with both the
/// <c>version-RFB-320.onnx</c> and <c>version-slim-320.onnx</c> variants.
///
/// Returns an empty list when the model file is missing, following the same
/// degrade-gracefully pattern as <see cref="Hawkynt.PhotoManager.Core.Detection.YoloObjectDetector"/>.
/// Drop the model at <see cref="AppDataPaths.ModelFile"/>("face-detector.onnx")
/// to turn detection on.
/// </summary>
public sealed class OnnxFaceDetector : IFaceDetector, IDisposable {
  public const string DefaultModelFileName = "face-detector.onnx";

  private const int InputWidth = 320;
  private const int InputHeight = 240;

  private readonly Lazy<InferenceSession?> _session;
  private readonly float _scoreThreshold;
  private readonly float _iouThreshold;

  public OnnxFaceDetector(FileInfo? modelFile = null, float scoreThreshold = 0.7f, float iouThreshold = 0.3f) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._scoreThreshold = scoreThreshold;
    this._iouThreshold = iouThreshold;
    this._session = new Lazy<InferenceSession?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  public async Task<IReadOnlyList<DetectedFace>> DetectAsync(FileInfo imageFile, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(imageFile);
    if (!imageFile.Exists)
      return Array.Empty<DetectedFace>();

    var session = this._session.Value;
    if (session == null)
      return Array.Empty<DetectedFace>();

    return await Task.Run(() => this.RunInference(session, imageFile), cancellationToken);
  }

  private IReadOnlyList<DetectedFace> RunInference(InferenceSession session, FileInfo imageFile) {
    using var image = Image.Load<Rgba32>(imageFile.FullName);
    var tensor = BuildInputTensor(image);

    var inputName = session.InputMetadata.Keys.First();
    var input = new DenseTensor<float>(tensor, new[] { 1, 3, InputHeight, InputWidth });

    using var results = session.Run(new[] {
      NamedOnnxValue.CreateFromTensor(inputName, input)
    });

    // UltraFace exports use output names like "scores" + "boxes" or numeric
    // ordinals. Pick them by shape rather than by name so both naming
    // conventions work.
    float[]? scores = null;
    float[]? boxes = null;
    int numAnchors = 0;

    foreach (var r in results) {
      var t = r.AsTensor<float>();
      var dims = t.Dimensions;
      if (dims.Length != 3 || dims[0] != 1)
        continue;

      if (dims[2] == 2) {
        scores = t.ToArray();
        numAnchors = dims[1];
      } else if (dims[2] == 4) {
        boxes = t.ToArray();
        numAnchors = dims[1];
      }
    }

    if (scores == null || boxes == null)
      return Array.Empty<DetectedFace>();

    return UltraFacePostProcess.Parse(
      scores, boxes, numAnchors,
      this._scoreThreshold, this._iouThreshold
    );
  }

  private static float[] BuildInputTensor(Image<Rgba32> image) {
    // Resize (stretch) to the model's fixed input — UltraFace isn't
    // letterboxed like YOLO, it expects a direct resize.
    image.Mutate(c => c.Resize(InputWidth, InputHeight));

    var tensor = new float[1 * 3 * InputHeight * InputWidth];
    var pixelCount = InputHeight * InputWidth;

    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < InputHeight; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < InputWidth; x++) {
          var px = row[x];
          var offset = y * InputWidth + x;
          // UltraFace normalization: (pixel - 127) / 128 into [-1, 1).
          tensor[0 * pixelCount + offset] = (px.R - 127f) / 128f;
          tensor[1 * pixelCount + offset] = (px.G - 127f) / 128f;
          tensor[2 * pixelCount + offset] = (px.B - 127f) / 128f;
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
