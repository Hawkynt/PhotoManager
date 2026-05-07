using Microsoft.ML.OnnxRuntime;
using PhotoManager.Core.Segmentation;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoManager.Core.Detection;

/// <summary>
/// ONNX-backed YOLO v8 object detector. Looks for <c>yolov8n.onnx</c> (or a
/// caller-specified file) at <see cref="AppDataPaths.ModelFile"/>. If the
/// model is missing, <see cref="DetectAsync"/> returns <see cref="DetectionResult.Empty"/>
/// so callers can gracefully fall back to a different detector rather than
/// crashing with "file not found".
///
/// The detector is thread-safe for concurrent <see cref="DetectAsync"/> calls
/// on different files because <see cref="InferenceSession"/> supports parallel
/// <see cref="InferenceSession.Run"/> invocations.
/// </summary>
public sealed class YoloObjectDetector : IDetector, IDisposable {
  public const string DefaultModelFileName = "yolov8n.onnx";

  private readonly Lazy<InferenceSession?> _session;
  private readonly float _scoreThreshold;
  private readonly float _iouThreshold;

  public YoloObjectDetector(FileInfo? modelFile = null, float scoreThreshold = 0.25f, float iouThreshold = 0.45f) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._scoreThreshold = scoreThreshold;
    this._iouThreshold = iouThreshold;
    this._session = new Lazy<InferenceSession?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  public async Task<DetectionResult> DetectAsync(FileInfo imageFile, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(imageFile);

    if (!imageFile.Exists)
      return DetectionResult.Empty;

    var session = this._session.Value;
    if (session == null)
      return DetectionResult.Empty;

    return await Task.Run(() => this.RunInference(session, imageFile), cancellationToken);
  }

  private DetectionResult RunInference(InferenceSession session, FileInfo imageFile) {
    var (tensorData, letterbox) = YoloPreProcess.BuildInput(imageFile);

    var inputName = session.InputMetadata.Keys.First();
    var inputTensor = new DenseTensor<float>(tensorData, new[] { 1, 3, YoloPreProcess.InputSize, YoloPreProcess.InputSize });

    using var results = session.Run(new[] {
      NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
    });

    var output = results.First().AsTensor<float>();
    var dims = output.Dimensions;
    if (dims.Length != 3 || dims[0] != 1)
      return DetectionResult.Empty;

    var numChannels = dims[1]; // 84 for COCO v8
    var numCells = dims[2];    // 8400 for 640 input

    var flat = output.ToArray();
    var detections = YoloPostProcess.Parse(
      flat,
      numChannels,
      numCells,
      letterbox,
      this._scoreThreshold,
      this._iouThreshold
    );

    var labels = detections
      .Select(d => {
        var name = CocoLabels.Resolve(d.ClassId);
        if (string.IsNullOrEmpty(name))
          return null;
        return new DetectionLabel(
          name,
          Confidence: d.Score,
          Kind: DetectionKind.Object,
          Region: ToNormalizedBox(d.Box, letterbox)
        );
      })
      .Where(l => l != null)
      .Select(l => l!)
      .ToArray();

    return new DetectionResult(labels);
  }

  private static NormalizedBoundingBox ToNormalizedBox(PixelBox box, LetterboxInfo letterbox)
    => new(
      X: box.X / letterbox.OriginalWidth,
      Y: box.Y / letterbox.OriginalHeight,
      Width: box.Width / letterbox.OriginalWidth,
      Height: box.Height / letterbox.OriginalHeight
    );

  private static InferenceSession? TryOpenSession(FileInfo modelFile) {
    try {
      if (!modelFile.Exists)
        return null;
      return OnnxAcceleration.CreateSession(modelFile.FullName);
    } catch {
      // Corrupt / incompatible / wrong ABI — degrade gracefully.
      return null;
    }
  }

  public void Dispose() {
    // Sessions are cached by OnnxAcceleration and shared across
    // instances; disposing them here would break the cache.
    // OnnxAcceleration.ResetCache() handles teardown if needed.
  }
}
