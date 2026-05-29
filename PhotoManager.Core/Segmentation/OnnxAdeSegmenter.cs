using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Segmentation;

/// <summary>
/// ONNX-backed multi-class semantic segmenter using SegFormer-B0 trained
/// on ADE20K (150 classes). Same degrade-gracefully + lazy-session pattern
/// as <see cref="OnnxSkySegmenter"/>: <see cref="IsAvailable"/> is false
/// when the model file is missing and <see cref="SegmentClass"/> returns
/// null in that case.
///
/// Input  shape: [1, 3, 512, 512] float32 in [0,1].
/// Output shape: [1, 150, 128, 128] float32 logits (SegFormer outputs at
/// 1/4 resolution; we upscale the resulting binary mask back to source).
///
/// To get a mask for a specific ADE20K class, call
/// <see cref="SegmentClass(Image{Rgba32}, int, CancellationToken)"/>
/// with one of the indices in <see cref="Ade20kClasses"/>.
/// </summary>
public sealed class OnnxAdeSegmenter : IDisposable {
  public const int DefaultInputSize = 512;
  public const int NumClasses = 150;

  private readonly Lazy<InferenceSession?> _session;
  private readonly int _inputSize;

  public OnnxAdeSegmenter(FileInfo modelFile, int inputSize = DefaultInputSize) {
    ArgumentNullException.ThrowIfNull(modelFile);
    this._inputSize = inputSize;
    this._session = new Lazy<InferenceSession?>(() => TryOpenSession(modelFile));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Returns a binary L8 mask at source dimensions: 255 where the
  /// argmax class equals <paramref name="classIndex"/>, 0 elsewhere.
  /// Returns null when the model isn't installed.
  /// </summary>
  public Image<L8>? SegmentClass(Image<Rgba32> source, int classIndex, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    if (classIndex < 0 || classIndex >= NumClasses)
      throw new ArgumentOutOfRangeException(nameof(classIndex), classIndex, $"Must be in [0,{NumClasses - 1}].");

    var session = this._session.Value;
    if (session == null)
      return null;

    ct.ThrowIfCancellationRequested();
    return this.RunInference(session, source, classIndex, ct);
  }

  public async Task<Image<L8>?> SegmentClassAsync(Image<Rgba32> source, int classIndex, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    var session = this._session.Value;
    if (session == null)
      return null;
    return await Task.Run(() => this.RunInference(session, source, classIndex, ct), ct);
  }

  private Image<L8>? RunInference(InferenceSession session, Image<Rgba32> source, int classIndex, CancellationToken ct) {
    var sourceWidth = source.Width;
    var sourceHeight = source.Height;

    using var resized = source.Clone(c => c.Resize(this._inputSize, this._inputSize));
    ct.ThrowIfCancellationRequested();

    var tensor = OnnxSkySegmenter.BuildInputTensor(resized, this._inputSize);

    var inputName = session.InputMetadata.Keys.First();
    var input = new DenseTensor<float>(tensor, new[] { 1, 3, this._inputSize, this._inputSize });

    ct.ThrowIfCancellationRequested();

    using var results = session.Run(new[] {
      NamedOnnxValue.CreateFromTensor(inputName, input)
    });

    foreach (var r in results) {
      var t = r.AsTensor<float>();
      var dims = t.Dimensions;
      // Expected output is [1, 150, H, W].
      if (dims.Length != 4 || dims[0] != 1 || dims[1] != NumClasses)
        continue;
      var classes = dims[1];
      var h = dims[2];
      var w = dims[3];
      var raw = t.ToArray();
      var mask = BuildArgmaxMask(raw, w, h, classes, classIndex);

      if (mask.Width != sourceWidth || mask.Height != sourceHeight)
        mask.Mutate(c => c.Resize(sourceWidth, sourceHeight));

      return mask;
    }

    return null;
  }

  /// <summary>
  /// Per-pixel argmax across the 150 class logits. Pixels where the
  /// winning class equals <paramref name="targetClass"/> become 255,
  /// everything else 0. This is the standard semantic-segmentation
  /// post-processing — no soft probabilities, no fractional alphas.
  /// Tensor layout is [1, C, H, W] in row-major (C is the fastest
  /// stride before H*W spans).
  /// </summary>
  private static Image<L8> BuildArgmaxMask(float[] raw, int width, int height, int classes, int targetClass) {
    var mask = new Image<L8>(width, height);
    var planeSize = width * height;
    mask.ProcessPixelRows(accessor => {
      for (var y = 0; y < height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < width; x++) {
          var pix = y * width + x;
          var bestClass = 0;
          var bestVal = raw[0 * planeSize + pix];
          for (var c = 1; c < classes; c++) {
            var v = raw[c * planeSize + pix];
            if (v > bestVal) {
              bestVal = v;
              bestClass = c;
            }
          }
          row[x] = new L8(bestClass == targetClass ? (byte)255 : (byte)0);
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
  }
}
