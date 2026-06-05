using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.Core.Segmentation;

/// <summary>
/// ONNX-backed monocular depth estimator. The reference checkpoint is
/// Depth Anything V2 small (DINOv2 backbone). Input is ImageNet-normalised
/// RGB at a multiple-of-14 dimension (the ViT patch size); output is a
/// single-channel relative depth map (closer = larger value, by convention).
///
/// Returns null when the model isn't installed — same degrade-gracefully
/// pattern as the other ONNX wrappers.
/// </summary>
public sealed class OnnxDepthEstimator : IDisposable {
  /// <summary>Default model file expected on disk.</summary>
  public const string DefaultModelFileName = "depth-anything-v2-small.onnx";

  /// <summary>Default working size. 518 = 14×37, the standard small-model ViT input.</summary>
  public const int DefaultInputSize = 518;

  private static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
  private static readonly float[] ImageNetStd  = [0.229f, 0.224f, 0.225f];

  private readonly Lazy<InferenceSession?> _session;
  private readonly int _inputSize;

  public OnnxDepthEstimator(FileInfo? modelFile = null, int inputSize = DefaultInputSize) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    if (inputSize % 14 != 0)
      throw new ArgumentException($"Input size must be a multiple of 14 (ViT patch size); got {inputSize}.", nameof(inputSize));
    this._inputSize = inputSize;
    this._session = new Lazy<InferenceSession?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Run depth estimation. Returns a float[] containing the raw depth
  /// values for each source-pixel position (length = width × height,
  /// row-major). Caller can normalise / colour-map / threshold it as
  /// needed — see <see cref="DepthBokehBlur"/>. Returns null when no
  /// model is available.
  /// </summary>
  public DepthMap? Estimate(Image<Rgba32> source, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    var session = this._session.Value;
    if (session == null)
      return null;
    ct.ThrowIfCancellationRequested();
    return this.RunInference(session, source, ct);
  }

  public async Task<DepthMap?> EstimateAsync(Image<Rgba32> source, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    var session = this._session.Value;
    if (session == null)
      return null;
    return await Task.Run(() => this.RunInference(session, source, ct), ct);
  }

  private DepthMap? RunInference(InferenceSession session, Image<Rgba32> source, CancellationToken ct) {
    var sourceWidth = source.Width;
    var sourceHeight = source.Height;

    using var resized = source.Clone(c => c.Resize(this._inputSize, this._inputSize));
    ct.ThrowIfCancellationRequested();

    var tensor = BuildInput(resized, this._inputSize);
    var inputName = session.InputMetadata.Keys.First();
    var input = new DenseTensor<float>(tensor, new[] { 1, 3, this._inputSize, this._inputSize });

    ct.ThrowIfCancellationRequested();

    using var results = session.Run(new[] {
      NamedOnnxValue.CreateFromTensor(inputName, input)
    });

    foreach (var r in results) {
      var t = r.AsTensor<float>();
      var dims = t.Dimensions;
      // Output is [N, H', W'] (no channel dim) for Depth Anything; the
      // sizes are rounded down to the nearest multiple of 14.
      int outH, outW;
      float[] raw = t.ToArray();
      if (dims.Length == 3) {
        outH = dims[1];
        outW = dims[2];
      } else if (dims.Length == 4 && dims[1] == 1) {
        outH = dims[2];
        outW = dims[3];
      } else {
        continue;
      }

      // Bilinear-resample the depth grid onto source dimensions. The
      // depth is a smooth scalar field, so bilinear is perceptually
      // sufficient; no need to plug ImageSharp's heavier samplers.
      var resampled = BilinearResample(raw, outW, outH, sourceWidth, sourceHeight);
      return new DepthMap(resampled, sourceWidth, sourceHeight);
    }

    return null;
  }

  /// <summary>
  /// Pack RGB pixels into [1, 3, S, S] float tensor with ImageNet
  /// normalisation. Channel order is RGB (matches transformers'
  /// SegformerImageProcessor default).
  /// </summary>
  internal static float[] BuildInput(Image<Rgba32> image, int inputSize) {
    var pixelCount = inputSize * inputSize;
    var tensor = new float[3 * pixelCount];
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < inputSize; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < inputSize; x++) {
          var px = row[x];
          var off = y * inputSize + x;
          tensor[0 * pixelCount + off] = (px.R / 255f - ImageNetMean[0]) / ImageNetStd[0];
          tensor[1 * pixelCount + off] = (px.G / 255f - ImageNetMean[1]) / ImageNetStd[1];
          tensor[2 * pixelCount + off] = (px.B / 255f - ImageNetMean[2]) / ImageNetStd[2];
        }
      }
    });
    return tensor;
  }

  /// <summary>
  /// Simple bilinear upsample of a single-channel grid from
  /// (<paramref name="srcW"/>, <paramref name="srcH"/>) to
  /// (<paramref name="dstW"/>, <paramref name="dstH"/>). Output values
  /// are in the same scale as the input (no normalisation).
  /// </summary>
  private static float[] BilinearResample(float[] src, int srcW, int srcH, int dstW, int dstH) {
    var dst = new float[dstW * dstH];
    var sx = (float)(srcW - 1) / Math.Max(1, dstW - 1);
    var sy = (float)(srcH - 1) / Math.Max(1, dstH - 1);
    for (var y = 0; y < dstH; y++) {
      var fy = y * sy;
      var y0 = (int)fy;
      var y1 = Math.Min(srcH - 1, y0 + 1);
      var dy = fy - y0;
      for (var x = 0; x < dstW; x++) {
        var fx = x * sx;
        var x0 = (int)fx;
        var x1 = Math.Min(srcW - 1, x0 + 1);
        var dx = fx - x0;
        var v00 = src[y0 * srcW + x0];
        var v01 = src[y0 * srcW + x1];
        var v10 = src[y1 * srcW + x0];
        var v11 = src[y1 * srcW + x1];
        var v0 = v00 * (1 - dx) + v01 * dx;
        var v1 = v10 * (1 - dx) + v11 * dx;
        dst[y * dstW + x] = v0 * (1 - dy) + v1 * dy;
      }
    }
    return dst;
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

/// <summary>
/// Raw depth-estimator output: a row-major float[] aligned to the
/// source image's dimensions. Values are in arbitrary relative units
/// (larger = closer for Depth Anything). Use the Min/Max helpers to
/// normalise to [0, 1] when you need a colour-mappable display.
/// </summary>
public sealed record DepthMap(float[] Values, int Width, int Height) {
  /// <summary>Smallest depth value present in <see cref="Values"/>.</summary>
  public float Min => this.Values.Length == 0 ? 0f : this.Values.Min();
  /// <summary>Largest depth value present in <see cref="Values"/>.</summary>
  public float Max => this.Values.Length == 0 ? 0f : this.Values.Max();

  /// <summary>Normalised value at (x, y) in [0, 1] where 0 = farthest, 1 = closest.</summary>
  public float NormalisedAt(int x, int y) {
    var min = this.Min;
    var max = this.Max;
    var span = max - min;
    if (span <= 1e-6f)
      return 0.5f;
    var v = this.Values[y * this.Width + x];
    return (v - min) / span;
  }
}
