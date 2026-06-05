using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.Core.Segmentation;

/// <summary>
/// ONNX-backed sky segmentation detector. Follows the same lifecycle pattern
/// as <see cref="OnnxSegmentationDetector"/> (lazy session, IsAvailable,
/// graceful degradation when the model file is missing, IDisposable).
///
/// Targets a generic semantic-segmentation ONNX export for sky detection.
/// The export's expected tensor metadata:
///   Input  name = first input,  shape = [1, 3, H, W] (fixed or dynamic)
///   Output name = first output, shape = [1, 1, H, W] (probability map 0..1)
/// We resize the input to <see cref="DefaultInputSize"/> for inference;
/// the output probability map is thresholded at 0.5 and returned as an
/// <see cref="Image{L8}"/> resized back to source dimensions.
///
/// Returns null when the model file is missing — the same degrade-gracefully
/// pattern used by the other ONNX wrappers. The UI layer is expected to call
/// <c>EnsureModelAsync</c> first to drive the user through the download.
/// </summary>
public sealed class OnnxSkySegmenter : IDisposable {
  /// <summary>Default input size for the sky segmentation model.</summary>
  public const int DefaultInputSize = 512;

  private readonly Lazy<InferenceSession?> _session;
  private readonly int _inputSize;

  public OnnxSkySegmenter(FileInfo modelFile, int inputSize = DefaultInputSize) {
    ArgumentNullException.ThrowIfNull(modelFile);
    this._inputSize = inputSize;
    this._session = new Lazy<InferenceSession?>(() => TryOpenSession(modelFile));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Runs sky segmentation on the given source image and returns a binary
  /// mask at the source's original dimensions: white (255) = sky, black (0)
  /// = not sky. Returns null when the model isn't installed or the source
  /// image is null.
  /// </summary>
  public Image<L8>? SegmentSky(Image<Rgba32> source, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);

    var session = this._session.Value;
    if (session == null)
      return null;

    ct.ThrowIfCancellationRequested();
    return this.RunInference(session, source, ct);
  }

  /// <summary>
  /// Async convenience wrapper — runs <see cref="SegmentSky"/> on the thread
  /// pool so the UI thread stays responsive on large images.
  /// </summary>
  public async Task<Image<L8>?> SegmentSkyAsync(Image<Rgba32> source, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);

    var session = this._session.Value;
    if (session == null)
      return null;

    return await Task.Run(() => this.RunInference(session, source, ct), ct);
  }

  private Image<L8>? RunInference(InferenceSession session, Image<Rgba32> source, CancellationToken ct) {
    var sourceWidth = source.Width;
    var sourceHeight = source.Height;

    // Clone so we don't mutate the caller's image during resize.
    using var resized = source.Clone(c => c.Resize(this._inputSize, this._inputSize));
    ct.ThrowIfCancellationRequested();

    var tensor = BuildInputTensor(resized, this._inputSize);

    var inputName = session.InputMetadata.Keys.First();
    var input = new DenseTensor<float>(tensor, new[] { 1, 3, this._inputSize, this._inputSize });

    ct.ThrowIfCancellationRequested();

    using var results = session.Run(new[] {
      NamedOnnxValue.CreateFromTensor(inputName, input)
    });

    foreach (var r in results) {
      var t = r.AsTensor<float>();
      var dims = t.Dimensions;
      if (dims.Length != 4 || dims[0] != 1)
        continue;
      var channels = dims[1];
      var h = dims[2];
      var w = dims[3];
      var raw = t.ToArray();
      // Two ONNX layouts are supported:
      //  - [1, 1, H, W] = single-channel sky probability map → 0.5 threshold
      //  - [1, 150, H, W] = SegFormer-B0 ADE20K logits → argmax + sky class
      // The same `sky-segmenter.onnx` file is now SegFormer-B0; the legacy
      // single-channel path stays for any user who dropped in a different
      // sky-only model.
      Image<L8> mask;
      if (channels == 1) {
        mask = BuildBinaryMask(raw, w, h);
      } else {
        // ADE20K sky is class index 2; argmax across channels per pixel.
        mask = BuildArgmaxMask(raw, w, h, channels, Ade20kClasses.Sky);
      }

      // Resize mask back to source dimensions.
      if (mask.Width != sourceWidth || mask.Height != sourceHeight)
        mask.Mutate(c => c.Resize(sourceWidth, sourceHeight));

      return mask;
    }

    return null;
  }

  /// <summary>Per-pixel argmax over <paramref name="channels"/> class logits;
  /// pixel becomes 255 when the winning class equals <paramref name="targetClass"/>.
  /// Mirrors <see cref="OnnxAdeSegmenter"/>; duplicated here to keep
  /// <see cref="OnnxSkySegmenter"/> self-contained and free of cross-class
  /// dependencies.</summary>
  private static Image<L8> BuildArgmaxMask(float[] raw, int width, int height, int channels, int targetClass) {
    var mask = new Image<L8>(width, height);
    var planeSize = width * height;
    mask.ProcessPixelRows(accessor => {
      for (var y = 0; y < height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < width; x++) {
          var pix = y * width + x;
          var bestClass = 0;
          var bestVal = raw[0 * planeSize + pix];
          for (var c = 1; c < channels; c++) {
            var v = raw[c * planeSize + pix];
            if (v > bestVal) { bestVal = v; bestClass = c; }
          }
          row[x] = new L8(bestClass == targetClass ? (byte)255 : (byte)0);
        }
      }
    });
    return mask;
  }

  /// <summary>
  /// Builds the NCHW float32 input tensor normalised to [0, 1].
  /// Many segmentation models use simple [0, 1] normalisation rather than
  /// the [-1, 1] range MODNet uses.
  /// </summary>
  internal static float[] BuildInputTensor(Image<Rgba32> image, int inputSize) {
    var tensor = new float[1 * 3 * inputSize * inputSize];
    var pixelCount = inputSize * inputSize;

    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < inputSize; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < inputSize; x++) {
          var px = row[x];
          var offset = y * inputSize + x;
          tensor[0 * pixelCount + offset] = px.R / 255f;
          tensor[1 * pixelCount + offset] = px.G / 255f;
          tensor[2 * pixelCount + offset] = px.B / 255f;
        }
      }
    });

    return tensor;
  }

  /// <summary>
  /// Converts the raw probability map into a binary mask: pixels with
  /// probability >= 0.5 become 255 (sky), everything else becomes 0.
  /// </summary>
  private static Image<L8> BuildBinaryMask(float[] raw, int width, int height) {
    var mask = new Image<L8>(width, height);
    mask.ProcessPixelRows(accessor => {
      for (var y = 0; y < height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < width; x++) {
          var v = raw[y * width + x];
          row[x] = new L8(v >= 0.5f ? (byte)255 : (byte)0);
        }
      }
    });
    return mask;
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
