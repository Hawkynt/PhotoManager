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
///
/// Tile inference is dispatched in parallel across all available accelerators
/// (NPU, GPU, CPU) using the same multi-EP work-stealing pattern as
/// <see cref="OnnxUpscaler"/>: one consumer task per device session pulls
/// tiles from a shared queue, and the first finisher wins. Blend write-back
/// is serialised under a lock because BlendTile reads-and-writes the output
/// image's overlap band in-place.
/// </summary>
public sealed class OnnxDenoiser : IDisposable {
  public const string DefaultModelFileName = "denoise.onnx";

  private const int TileSize = 256;
  private const int TileOverlap = 16;

  private readonly Lazy<SessionInfo?> _session;

  public OnnxDenoiser(FileInfo? modelFile = null) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._session = new Lazy<SessionInfo?>(() => TryOpenSession(path));
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
  public Image<Rgba32>? Denoise(Image<Rgba32> source, double strength = 1.0, CancellationToken ct = default, IProgress<Develop.StageProgress>? progress = null) {
    ArgumentNullException.ThrowIfNull(source);
    var info = this._session.Value;
    if (info == null)
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

    // Snapshot source pixels once — ImageSharp's per-image accessor
    // isn't safe to share across the parallel inference threads.
    var srcPixels = new Rgba32[width * height];
    source.CopyPixelDataTo(srcPixels);

    // Build tile coordinates up front for progress + queue sizing.
    var tiles = new List<(int tx, int ty, int w, int h)>();
    for (var ty = 0; ty < height; ty += stride) {
      for (var tx = 0; tx < width; tx += stride) {
        var w = Math.Min(TileSize, width - tx);
        var h = Math.Min(TileSize, height - ty);
        if (w > 0 && h > 0)
          tiles.Add((tx, ty, w, h));
      }
    }
    var totalTiles = tiles.Count;
    var done = 0;
    progress?.Report(new Develop.StageProgress("denoise", 0, totalTiles));

    var writeLock = new object();

    void RunTileOnSession(InferenceSession session, (int tx, int ty, int w, int h) tile) {
      var denoisedTile = RunTileFromArray(info, session, srcPixels, width, height, tile.tx, tile.ty, tile.w, tile.h);
      if (denoisedTile != null) {
        lock (writeLock) {
          BlendTile(output, denoisedTile, tile.tx, tile.ty, tile.w, tile.h, blend);
        }
      }
      var d = Interlocked.Increment(ref done);
      progress?.Report(new Develop.StageProgress("denoise", d, totalTiles));
    }

    // Try the multi-EP dispatch path first: one InferenceSession per
    // physical accelerator (NPU, GPU, CPU). Each consumer holds its
    // own session and pulls tiles off a shared BlockingCollection —
    // a fast device naturally takes more tiles than a slow one (work
    // stealing). When OpenVINO can't bind a separate NPU/GPU session
    // (single-device hardware, OV not installed), we drop to the
    // single-session Parallel.ForEach path below.
    IReadOnlyList<(string Device, InferenceSession Session)>? sessions = null;
    try {
      sessions = OnnxAcceleration.CreateMultiDeviceSessions(info.ModelPath);
    } catch {
      sessions = null;
    }

    try {
      if (sessions != null && sessions.Count >= 2) {
        // Producer/consumer with one consumer per session. Work
        // stealing falls out for free: a fast accelerator drains the
        // queue more aggressively than a slow one.
        using var queue = new System.Collections.Concurrent.BlockingCollection<(int tx, int ty, int w, int h)>(boundedCapacity: tiles.Count + 1);
        foreach (var tile in tiles)
          queue.Add(tile);
        queue.CompleteAdding();

        var consumerTasks = new Task[sessions.Count];
        for (var i = 0; i < sessions.Count; i++) {
          var session = sessions[i].Session;
          consumerTasks[i] = Task.Run(() => {
            foreach (var tile in queue.GetConsumingEnumerable(ct)) {
              RunTileOnSession(session, tile);
            }
          }, ct);
        }
        Task.WaitAll(consumerTasks, ct);
      } else {
        // Single-session fallback (no separate NPU/GPU sessions
        // available). Same Parallel.ForEach as upscaler, capped
        // conservatively because all parallel calls hit one session.
        Parallel.ForEach(
          tiles,
          new ParallelOptions {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Min(4, Math.Max(2, Environment.ProcessorCount / 2))
          },
          tile => RunTileOnSession(info.Session, tile));
      }
    } catch (OperationCanceledException) {
      output.Dispose();
      throw;
    } catch (AggregateException ae) when (ae.InnerExceptions.OfType<OperationCanceledException>().Any()) {
      output.Dispose();
      throw new OperationCanceledException(ct);
    }

    return output;
  }

  /// <summary>Array-fed tile inference; safe to call concurrently from
  /// multiple threads because it reads from a flat snapshot rather than
  /// the live ImageSharp accessor.</summary>
  private static float[]? RunTileFromArray(SessionInfo info, InferenceSession session, Rgba32[] src, int srcW, int srcH, int tx, int ty, int w, int h) {
    var inputTensor = new float[1 * 3 * h * w];
    var pixelCount = h * w;

    for (var y = 0; y < h; y++) {
      var srcRowOffset = (ty + y) * srcW;
      for (var x = 0; x < w; x++) {
        var px = src[srcRowOffset + tx + x];
        var offset = y * w + x;
        inputTensor[0 * pixelCount + offset] = px.R / 255f;
        inputTensor[1 * pixelCount + offset] = px.G / 255f;
        inputTensor[2 * pixelCount + offset] = px.B / 255f;
      }
    }

    try {
      var input = new DenseTensor<float>(inputTensor, new[] { 1, 3, h, w });
      using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(info.InputName, input) });
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

  private static SessionInfo? TryOpenSession(FileInfo modelFile) {
    try {
      if (!modelFile.Exists)
        return null;
      var session = OnnxAcceleration.CreateSession(modelFile.FullName);
      var inputName = session.InputMetadata.Keys.First();
      return new SessionInfo(session, inputName, modelFile.FullName);
    } catch {
      return null;
    }
  }

  public void Dispose() {
    // Sessions are cached by OnnxAcceleration and shared across
    // instances; disposing them here would break the cache.
    // OnnxAcceleration.ResetCache() handles teardown if needed.
  }

  private sealed record SessionInfo(
    InferenceSession Session,
    string InputName,
    string ModelPath);
}
