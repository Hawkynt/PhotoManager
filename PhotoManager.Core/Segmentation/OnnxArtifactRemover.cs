using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Segmentation;

/// <summary>
/// ONNX-backed JPEG / DCT / wavelet / ringing / halo artifact remover.
/// Mirrors <see cref="OnnxDenoiser"/>'s degrade-gracefully pattern:
/// <see cref="IsAvailable"/> is false when the model file is missing and
/// <see cref="Remove"/> returns null in that case so callers can no-op
/// without exceptions.
///
/// The canonical model is FBCNN (Flexible Blind CNN — Jiang et al.,
/// https://github.com/jiaxi-jiang/FBCNN) exported to ONNX. Single-pass
/// blind restoration of JPEG/compression artifacts: handles ringing,
/// blocking, and halos in one shot. Drop the file at
/// <see cref="AppDataPaths.ModelFile"/>("fbcnn-color.onnx") to enable the
/// stage; <see cref="Models.ModelRegistry.ArtifactRemoverFbcnnColor"/>
/// handles the download.
///
/// I/O contract: 1×3×H×W float32 RGB in [0..1], output the same shape.
/// FBCNN has 3 levels of 2× down-sampling internally so H and W must be
/// multiples of 8; we pad by edge-replication on the bottom-right when
/// needed and crop the prediction back to the source's dimensions on
/// output.
///
/// Unlike the denoiser this implementation runs the whole image in one
/// inference because FBCNN is fully convolutional and the artifact
/// pattern is mostly local (8-pixel block grid for JPEG) — tiling adds
/// seam complexity for negligible memory savings on the photo sizes the
/// app deals with.
///
/// <c>strength</c> linearly mixes between the source (0.0) and the fully
/// processed result (1.0).
/// </summary>
public sealed class OnnxArtifactRemover : IDisposable {
  public const string DefaultModelFileName = "fbcnn-color.onnx";

  /// <summary>FBCNN's three 2× down-sampling levels require H and W to be multiples of 8.</summary>
  private const int RequiredMultiple = 8;

  private readonly Lazy<InferenceSession?> _session;

  public OnnxArtifactRemover(FileInfo? modelFile = null) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._session = new Lazy<InferenceSession?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Returns a freshly allocated artifact-cleaned copy of
  /// <paramref name="source"/>. Returns null when no model is available so
  /// the caller can fall back to a clone / no-op without try/catch.
  /// </summary>
  /// <param name="source">Input image. Not mutated.</param>
  /// <param name="strength">Blend between source (0.0) and fully processed
  ///   (1.0). Outside that range gets clamped.</param>
  /// <param name="ct">Cooperatively cancels before / after inference.</param>
  public Image<Rgba32>? Remove(Image<Rgba32> source, double strength = 1.0, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    var session = this._session.Value;
    if (session == null)
      return null;

    var blend = (float)Math.Clamp(strength, 0.0, 1.0);
    if (blend < 1e-6)
      return source.Clone();

    ct.ThrowIfCancellationRequested();

    var srcW = source.Width;
    var srcH = source.Height;
    var padW = RoundUp(srcW, RequiredMultiple);
    var padH = RoundUp(srcH, RequiredMultiple);

    // Build the padded NCHW input: source pixels in the top-left, edge-
    // replicate on the right/bottom strips so the model never sees a hard
    // zero step at the padded border (which would produce its own
    // artifacts that survive the crop).
    var pixelCount = padH * padW;
    var input = new float[3 * pixelCount];
    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < padH; y++) {
        var sy = Math.Min(y, srcH - 1);
        var row = accessor.GetRowSpan(sy);
        for (var x = 0; x < padW; x++) {
          var sx = Math.Min(x, srcW - 1);
          var px = row[sx];
          var off = y * padW + x;
          input[0 * pixelCount + off] = px.R / 255f;
          input[1 * pixelCount + off] = px.G / 255f;
          input[2 * pixelCount + off] = px.B / 255f;
        }
      }
    });

    float[] output;
    try {
      var inputName = session.InputMetadata.Keys.First();
      var tensor = new DenseTensor<float>(input, new[] { 1, 3, padH, padW });
      using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
      output = results.First().AsTensor<float>().ToArray();
    } catch {
      return null;
    }

    ct.ThrowIfCancellationRequested();

    // Crop back to source dimensions while alpha-blending against the
    // original by `strength`. Same convention as OnnxDenoiser.Denoise.
    var result = source.Clone();
    result.ProcessPixelRows(accessor => {
      for (var y = 0; y < srcH; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < srcW; x++) {
          var off = y * padW + x;
          var dr = Math.Clamp(output[0 * pixelCount + off], 0f, 1f);
          var dg = Math.Clamp(output[1 * pixelCount + off], 0f, 1f);
          var db = Math.Clamp(output[2 * pixelCount + off], 0f, 1f);

          var px = row[x];
          var sr = px.R / 255f;
          var sg = px.G / 255f;
          var sb = px.B / 255f;

          var rr = sr + (dr - sr) * blend;
          var rg = sg + (dg - sg) * blend;
          var rb = sb + (db - sb) * blend;

          row[x] = new Rgba32(ToByte(rr), ToByte(rg), ToByte(rb), px.A);
        }
      }
    });

    return result;
  }

  private static int RoundUp(int value, int multiple)
    => (value + multiple - 1) / multiple * multiple;

  private static byte ToByte(float v) {
    if (float.IsNaN(v) || v <= 0f)
      return 0;
    if (v >= 1f)
      return 255;
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
    // Sessions are cached by OnnxAcceleration and shared across
    // instances; disposing them here would break the cache.
    // OnnxAcceleration.ResetCache() handles teardown if needed.
  }
}
