using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Segmentation;

/// <summary>
/// ONNX-backed image dehazer. Reference checkpoint is AOD-Net
/// (Li et al., ICCV 2017) — a ~1.8 K-parameter CNN that predicts a
/// joint atmospheric-light + transmission map and applies the
/// atmospheric scattering model to recover the haze-free image.
/// Tiny model (~9 KB ONNX); modest quality vs SOTA dehazers like
/// DehazeFormer / FFA-Net but ships immediately. A future drop-in
/// at the same file path (e.g. a DehazeFormer-T ONNX export) is the
/// natural upgrade path.
///
/// Input:  [1, 3, H, W] float32 in [0, 1].
/// Output: [1, 3, H, W] dehazed image in [0, 1].
///
/// Returns null when the model isn't installed — same
/// degrade-gracefully pattern as the other ONNX wrappers.
/// </summary>
public sealed class OnnxDehazer : IDisposable {
  public const string DefaultModelFileName = "aod-net.onnx";

  private readonly Lazy<InferenceSession?> _session;

  public OnnxDehazer(FileInfo? modelFile = null) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._session = new Lazy<InferenceSession?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  public Image<Rgba32>? Dehaze(Image<Rgba32> source, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    var session = this._session.Value;
    if (session == null)
      return null;
    ct.ThrowIfCancellationRequested();
    return RunInference(session, source, ct);
  }

  public async Task<Image<Rgba32>?> DehazeAsync(Image<Rgba32> source, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    var session = this._session.Value;
    if (session == null)
      return null;
    return await Task.Run(() => RunInference(session, source, ct), ct);
  }

  private static Image<Rgba32>? RunInference(InferenceSession session, Image<Rgba32> source, CancellationToken ct) {
    var w = source.Width;
    var h = source.Height;
    var pixelCount = w * h;
    var tensorSize = 1 * 3 * pixelCount;
    var input = new float[tensorSize];

    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < w; x++) {
          var px = row[x];
          var off = y * w + x;
          input[0 * pixelCount + off] = px.R / 255f;
          input[1 * pixelCount + off] = px.G / 255f;
          input[2 * pixelCount + off] = px.B / 255f;
        }
      }
    });

    ct.ThrowIfCancellationRequested();

    float[] output;
    try {
      var inputName = session.InputMetadata.Keys.First();
      var tensor = new DenseTensor<float>(input, new[] { 1, 3, h, w });
      using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
      output = results.First().AsTensor<float>().ToArray();
    } catch {
      return null;
    }

    ct.ThrowIfCancellationRequested();

    var result = new Image<Rgba32>(w, h);
    result.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < w; x++) {
          var off = y * w + x;
          var r = Math.Clamp(output[0 * pixelCount + off], 0f, 1f);
          var g = Math.Clamp(output[1 * pixelCount + off], 0f, 1f);
          var b = Math.Clamp(output[2 * pixelCount + off], 0f, 1f);
          row[x] = new Rgba32(ToByte(r), ToByte(g), ToByte(b), (byte)255);
        }
      }
    });
    return result;
  }

  private static byte ToByte(float v) {
    if (float.IsNaN(v) || v <= 0f) return 0;
    if (v >= 1f) return 255;
    return (byte)Math.Round(v * 255f);
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
