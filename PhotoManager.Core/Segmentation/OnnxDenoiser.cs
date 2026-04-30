using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Segmentation;

/// <summary>
/// ONNX-backed AI denoiser. Mirrors <see cref="PhotoManager.Core.Faces.OnnxFaceDetector"/>'s
/// degrade-gracefully pattern: <see cref="IsAvailable"/> is false when the
/// model file is missing and <see cref="Denoise"/> returns null in that case
/// so callers can no-op without exceptions.
///
/// The canonical model is NAFNet (NAFNet-SIDD-width64) exported to ONNX —
/// dynamic 1×3×H×W float32 input/output in the [0..1] range. Drop the file
/// at <see cref="AppDataPaths.ModelFile"/>("denoise.onnx") to enable the
/// stage; <see cref="Models.ModelRegistry.NafnetSidd"/> handles the download.
///
/// Big sensors (50-MP RAWs) blow OnnxRuntime's working set if you feed the
/// whole image in one go, so the implementation tiles to <see cref="TileSize"/>×
/// <see cref="TileSize"/> patches with <see cref="TileOverlap"/>-px overlap and
/// alpha-blends the seams. <c>strength</c> linearly mixes between the source
/// (0.0) and the fully-denoised tile (1.0) so the user can dial intensity
/// without re-running inference.
/// </summary>
public sealed class OnnxDenoiser : IDisposable {
  public const string DefaultModelFileName = "denoise.onnx";

  private const int TileSize = 256;
  private const int TileOverlap = 16;

  private readonly Lazy<InferenceSession?> _session;

  public OnnxDenoiser(FileInfo? modelFile = null) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._session = new Lazy<InferenceSession?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Returns a freshly allocated denoised copy of <paramref name="source"/>.
  /// Returns null when no model is available so the caller can fall back to
  /// a clone / no-op without try/catch.
  /// </summary>
  /// <param name="source">Input image. Not mutated.</param>
  /// <param name="strength">Blend between source (0.0) and fully-denoised
  ///   (1.0). Outside that range gets clamped.</param>
  /// <param name="ct">Cooperatively cancels between tiles so live-preview
  ///   re-renders don't have to wait for a full pass to finish.</param>
  public Image<Rgba32>? Denoise(Image<Rgba32> source, double strength = 1.0, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    var session = this._session.Value;
    if (session == null)
      return null;

    var blend = (float)Math.Clamp(strength, 0.0, 1.0);
    if (blend < 1e-6)
      return source.Clone();

    var output = source.Clone();
    var width = output.Width;
    var height = output.Height;

    // Stride is TileSize - TileOverlap so adjacent tiles share an overlap
    // band of TileOverlap pixels we can ramp-blend across.
    var stride = TileSize - TileOverlap;

    for (var ty = 0; ty < height; ty += stride) {
      for (var tx = 0; tx < width; tx += stride) {
        ct.ThrowIfCancellationRequested();

        var w = Math.Min(TileSize, width - tx);
        var h = Math.Min(TileSize, height - ty);
        if (w <= 0 || h <= 0)
          continue;

        var denoisedTile = RunTile(session, output, tx, ty, w, h);
        if (denoisedTile == null)
          continue;

        BlendTile(output, denoisedTile, tx, ty, w, h, blend);
      }
    }

    return output;
  }

  private static float[]? RunTile(InferenceSession session, Image<Rgba32> source, int tx, int ty, int w, int h) {
    var inputTensor = new float[1 * 3 * h * w];
    var pixelCount = h * w;

    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(ty + y);
        for (var x = 0; x < w; x++) {
          var px = row[tx + x];
          var offset = y * w + x;
          inputTensor[0 * pixelCount + offset] = px.R / 255f;
          inputTensor[1 * pixelCount + offset] = px.G / 255f;
          inputTensor[2 * pixelCount + offset] = px.B / 255f;
        }
      }
    });

    try {
      var inputName = session.InputMetadata.Keys.First();
      var input = new DenseTensor<float>(inputTensor, new[] { 1, 3, h, w });
      using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
      return results.First().AsTensor<float>().ToArray();
    } catch {
      return null;
    }
  }

  private static void BlendTile(Image<Rgba32> target, float[] denoised, int tx, int ty, int w, int h, float strength) {
    var pixelCount = h * w;

    target.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(ty + y);
        for (var x = 0; x < w; x++) {
          var offset = y * w + x;
          var dr = Math.Clamp(denoised[0 * pixelCount + offset], 0f, 1f);
          var dg = Math.Clamp(denoised[1 * pixelCount + offset], 0f, 1f);
          var db = Math.Clamp(denoised[2 * pixelCount + offset], 0f, 1f);

          // Edge feather: pixels in the overlap band on the leading edge
          // of the tile get weighted toward the existing (already-blended)
          // pixel so seams aren't visible.
          var ramp = TileEdgeWeight(x, y, w, h);
          var weight = strength * ramp;

          var px = row[tx + x];
          var sr = px.R / 255f;
          var sg = px.G / 255f;
          var sb = px.B / 255f;

          var rr = sr + (dr - sr) * weight;
          var rg = sg + (dg - sg) * weight;
          var rb = sb + (db - sb) * weight;

          row[tx + x] = new Rgba32(ToByte(rr), ToByte(rg), ToByte(rb), px.A);
        }
      }
    });
  }

  /// <summary>
  /// Linear ramp 0→1 across the leading <see cref="TileOverlap"/> pixels of
  /// each tile so the seams blend instead of cutting hard. Internal pixels
  /// (away from the leading edge) get full weight.
  /// </summary>
  private static float TileEdgeWeight(int x, int y, int w, int h) {
    var weight = 1f;
    if (x < TileOverlap)
      weight = Math.Min(weight, (x + 1f) / (TileOverlap + 1));
    if (y < TileOverlap)
      weight = Math.Min(weight, (y + 1f) / (TileOverlap + 1));
    if (w - x <= TileOverlap)
      weight = Math.Min(weight, (w - x) / (float)(TileOverlap + 1));
    if (h - y <= TileOverlap)
      weight = Math.Min(weight, (h - y) / (float)(TileOverlap + 1));
    return Math.Clamp(weight, 0f, 1f);
  }

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
