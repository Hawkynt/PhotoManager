using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PhotoManager.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Segmentation;

/// <summary>
/// ONNX-backed AI upscaler. Same lazy-session + degrade-gracefully pattern as
/// <see cref="OnnxDenoiser"/>: missing model → <see cref="IsAvailable"/>
/// false and <see cref="Upscale"/> returns null.
///
/// The canonical model is RealESRGAN-x4 ONNX — dynamic 1×3×H×W float32
/// input in [0..1], output 1×3×(H·factor)×(W·factor) at the model's native
/// scale factor (4×). For 2× output we run 4× then bilinear-downsample;
/// running RealESRGAN-x2 directly would be faster but is the same plumbing
/// so a future model can plug in by swapping the file at
/// <see cref="AppDataPaths.ModelFile"/>("upscale.onnx").
///
/// Tiling at <see cref="TileSize"/>×<see cref="TileSize"/> input with
/// <see cref="TileOverlap"/>-px overlap keeps memory in check on 50-MP RAWs.
/// The overlap is wider than the denoiser's because the model spreads each
/// input pixel over factor² output pixels — small input seams turn into
/// visible output seams when blended.
/// </summary>
public sealed class OnnxUpscaler : IDisposable {
  public const string DefaultModelFileName = "upscale.onnx";

  // Default tile size for dynamic-shape models. Fixed-shape models use the
  // declared model dimension instead. 192 chosen as a compromise between
  // fewer tiles (less per-Run() ORT overhead, fewer reads/writes around
  // the seam) and tile peak memory (192-in × 4 = 768-out at 4 bytes/px =
  // ~2.3 MB per tile, fine on any modern machine). The previous 128
  // setting issued ~2.25× as many tile inferences for the same image.
  // size declared by their input metadata (some Real-ESRGAN exports are
  // hard-pinned at 64×64 or 128×128).
  private const int DefaultTileSize = 192;
  private const int DefaultTileOverlap = 48;

  private readonly Lazy<SessionInfo?> _session;

  public OnnxUpscaler(FileInfo? modelFile = null) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._session = new Lazy<SessionInfo?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>The model's native scale factor (output / input ratio). 4 for Real-ESRGAN-x4, 2 for x2 variants. Returns 0 when no model is available.</summary>
  public int NativeFactor => this._session.Value?.NativeFactor ?? 0;

  /// <summary>Maximum dimension we'll allow on either axis of the upscaled output. Most image formats and GPU paths break above 32768 px.</summary>
  public const int MaxOutputDimension = 32768;

  /// <summary>
  /// Returns a freshly allocated upscaled copy of <paramref name="source"/>.
  /// Returns null when no model is available so the caller can fall back to
  /// a plain bilinear resize / no-op without try/catch.
  /// </summary>
  /// <param name="factor">Target scale factor (2 / 4 / 16 / 64 supported).
  ///   16 and 64 are achieved by chaining the model's native 4× pass twice
  ///   or three times. factor &lt;= 1 returns a clone. Values that would
  ///   push either output dimension past <see cref="MaxOutputDimension"/>
  ///   return null so the caller can fall back to no-op.</param>
  /// <param name="ct">Cooperatively cancels between tiles so live-preview
  ///   re-renders don't have to wait for a full pass to finish.</param>
  public Image<Rgba32>? Upscale(Image<Rgba32> source, int factor = 4, CancellationToken ct = default, IProgress<Develop.StageProgress>? progress = null) {
    ArgumentNullException.ThrowIfNull(source);
    if (factor <= 1)
      return source.Clone();

    // Snap to the supported set: 2, 4, 16, 64.
    factor = factor switch {
      <= 2 => 2,
      <= 4 => 4,
      <= 16 => 16,
      _ => 64
    };

    var info = this._session.Value;
    if (info == null)
      return null;

    // Refuse anything that would exceed the 32K-per-side cap so we don't
    // allocate a 6 GB image only to crash later.
    var targetW = (long)source.Width * factor;
    var targetH = (long)source.Height * factor;
    if (targetW > MaxOutputDimension || targetH > MaxOutputDimension)
      return null;

    // Number of native (4×) passes to run, plus an optional final scale.
    var nativeFactor = info.NativeFactor;
    var passes = factor switch {
      2 => 1,
      4 => 1,
      16 => 2,
      _ => 3
    };

    var current = source;
    var disposeCurrent = false;
    for (var pass = 0; pass < passes; pass++) {
      ct.ThrowIfCancellationRequested();
      // Each pass becomes its own stage in the progress feed so the UI
      // can display "Upscale (1/2): patch X/Y" and reset the bar between
      // passes. The ETA recomputes per stage.
      var stageLabel = passes > 1 ? $"upscale ({pass + 1}/{passes})" : "upscale";
      var stepResult = RunSinglePass(info, current, ct, progress, stageLabel);
      if (disposeCurrent)
        current.Dispose();
      if (stepResult == null)
        return null;
      current = stepResult;
      disposeCurrent = true;
    }

    var producedFactor = (int)Math.Pow(nativeFactor, passes);
    if (factor == producedFactor)
      return disposeCurrent ? current : current.Clone();

    // factor=2 path: one 4× pass overshoots; downsample to half.
    // ThumbnailManager picks Box (anti-aliased averaging) for this 50%
    // downscale — fastest AND highest-quality choice for shrinking.
    // Older code used ImageSharp's default bicubic which on a 32MP+
    // intermediate could take seconds; Box does it in tens of ms.
    using (disposeCurrent ? current : null) {
      var w = source.Width * factor;
      var h = source.Height * factor;
      return ThumbnailManager.Resize(current, w, h);
    }
  }


  /// <summary>
  /// One full native-factor (4×) pass over <paramref name="source"/>,
  /// tile by tile, dispatched in parallel so OpenVINO MULTI:NPU,GPU,CPU
  /// can keep all configured devices busy concurrently. Tiles are read
  /// from a flat snapshot of the source pixels (ImageSharp's pixel
  /// accessor isn't safe to share across threads), inferences run on
  /// the shared session (ORT InferenceSession.Run is thread-safe), and
  /// writes to the output canvas are serialised under a single lock —
  /// the lock is held for ~0.5 ms per tile vs ~100–300 ms per Run, so
  /// contention is negligible.
  /// </summary>
  private static Image<Rgba32>? RunSinglePass(
      SessionInfo info, Image<Rgba32> source, CancellationToken ct,
      IProgress<Develop.StageProgress>? progress, string stageLabel) {
    var nativeFactor = info.NativeFactor;
    var srcW = source.Width;
    var srcH = source.Height;
    var nativeW = srcW * nativeFactor;
    var nativeH = srcH * nativeFactor;

    var native = new Image<Rgba32>(nativeW, nativeH);
    var stride = info.TileSize - info.TileOverlap;
    if (stride <= 0)
      stride = info.TileSize;

    // Snapshot source pixels once — ImageSharp's per-image accessor
    // isn't safe to share across the parallel inference threads.
    var srcPixels = new Rgba32[srcW * srcH];
    source.CopyPixelDataTo(srcPixels);

    // Build the tile-coordinate list up front so we know the total
    // count for progress reporting.
    var tiles = new List<(int tx, int ty, int w, int h)>();
    for (var ty = 0; ty < srcH; ty += stride) {
      if (ty >= srcH) continue;
      for (var tx = 0; tx < srcW; tx += stride) {
        if (tx >= srcW) continue;
        var w = Math.Min(info.TileSize, srcW - tx);
        var h = Math.Min(info.TileSize, srcH - ty);
        if (w > 0 && h > 0) tiles.Add((tx, ty, w, h));
      }
    }
    var total = tiles.Count;
    var done = 0;
    progress?.Report(new Develop.StageProgress(stageLabel, 0, total));

    var writeLock = new object();

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

    void RunTileOnSession(InferenceSession session, (int tx, int ty, int w, int h) tile) {
      var tilePixels = info.IsFixedInputSize
        ? RunFixedTileFromArray(info, session, srcPixels, srcW, srcH, tile.tx, tile.ty)
        : RunDynamicTileFromArray(info, session, srcPixels, srcW, srcH, tile.tx, tile.ty, tile.w, tile.h);
      if (tilePixels != null) {
        var tileFedW = info.IsFixedInputSize ? info.TileSize : tile.w;
        var tileFedH = info.IsFixedInputSize ? info.TileSize : tile.h;
        var destW = tile.w * nativeFactor;
        var destH = tile.h * nativeFactor;
        lock (writeLock) {
          WriteTile(native, tilePixels, tileFedW * nativeFactor, tileFedH * nativeFactor,
            tile.tx * nativeFactor, tile.ty * nativeFactor, destW, destH);
        }
      }
      var d = Interlocked.Increment(ref done);
      progress?.Report(new Develop.StageProgress(stageLabel, d, total));
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
        // available). Same Parallel.ForEach as before, capped
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
      native.Dispose();
      throw;
    } catch (AggregateException ae) when (ae.InnerExceptions.OfType<OperationCanceledException>().Any()) {
      native.Dispose();
      throw new OperationCanceledException(ct);
    }
    return native;
  }

  /// <summary>Array-fed counterpart to <see cref="RunDynamicTile"/>; safe
  /// to call concurrently from multiple threads because it reads from a
  /// flat snapshot rather than the live ImageSharp accessor.</summary>
  private static float[]? RunDynamicTileFromArray(SessionInfo info, Rgba32[] src, int srcW, int srcH, int tx, int ty, int w, int h)
    => RunDynamicTileFromArray(info, info.Session, src, srcW, srcH, tx, ty, w, h);

  /// <summary>Same as the single-session overload but runs against an
  /// EXPLICIT session — caller threads each hold their own EP-pinned
  /// session and pass it here.</summary>
  private static float[]? RunDynamicTileFromArray(SessionInfo info, InferenceSession session, Rgba32[] src, int srcW, int srcH, int tx, int ty, int w, int h) {
    var pixelCount = h * w;
    var inputTensor = new float[1 * 3 * pixelCount];
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
    return RunSession(info, session, inputTensor, h, w);
  }

  /// <summary>Array-fed counterpart to <see cref="RunFixedTile"/>;
  /// thread-safe (no ImageSharp accessor calls).</summary>
  private static float[]? RunFixedTileFromArray(SessionInfo info, Rgba32[] src, int srcW, int srcH, int tx, int ty)
    => RunFixedTileFromArray(info, info.Session, src, srcW, srcH, tx, ty);

  private static float[]? RunFixedTileFromArray(SessionInfo info, InferenceSession session, Rgba32[] src, int srcW, int srcH, int tx, int ty) {
    var size = info.TileSize;
    var pixelCount = size * size;
    var inputTensor = new float[1 * 3 * pixelCount];
    for (var y = 0; y < size; y++) {
      var sourceY = Math.Min(ty + y, srcH - 1);
      var rowOffset = sourceY * srcW;
      for (var x = 0; x < size; x++) {
        var sourceX = Math.Min(tx + x, srcW - 1);
        var px = src[rowOffset + sourceX];
        var offset = y * size + x;
        inputTensor[0 * pixelCount + offset] = px.R / 255f;
        inputTensor[1 * pixelCount + offset] = px.G / 255f;
        inputTensor[2 * pixelCount + offset] = px.B / 255f;
      }
    }
    return RunSession(info, session, inputTensor, size, size);
  }

  private static float[]? RunDynamicTile(SessionInfo info, Image<Rgba32> source, int tx, int ty, int w, int h) {
    var pixelCount = h * w;
    var inputTensor = new float[1 * 3 * pixelCount];
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
    return RunSession(info, inputTensor, h, w);
  }

  /// <summary>
  /// Fixed-input-shape models (e.g. Real-ESRGAN exports pinned at 64×64)
  /// only accept the exact declared size, so we always feed a full tile
  /// and replicate the last row / column when the source's tail tile is
  /// smaller than the model's input.
  /// </summary>
  private static float[]? RunFixedTile(SessionInfo info, Image<Rgba32> source, int tx, int ty) {
    var size = info.TileSize;
    var pixelCount = size * size;
    var inputTensor = new float[1 * 3 * pixelCount];
    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < size; y++) {
        var sourceY = Math.Min(ty + y, source.Height - 1);
        var row = accessor.GetRowSpan(sourceY);
        for (var x = 0; x < size; x++) {
          var sourceX = Math.Min(tx + x, source.Width - 1);
          var px = row[sourceX];
          var offset = y * size + x;
          inputTensor[0 * pixelCount + offset] = px.R / 255f;
          inputTensor[1 * pixelCount + offset] = px.G / 255f;
          inputTensor[2 * pixelCount + offset] = px.B / 255f;
        }
      }
    });
    return RunSession(info, inputTensor, size, size);
  }

  private static float[]? RunSession(SessionInfo info, float[] inputTensor, int h, int w)
    => RunSession(info, info.Session, inputTensor, h, w);

  /// <summary>Inference with an EXPLICIT session — used by the multi-EP
  /// dispatch path where each consumer thread holds its own session
  /// pinned to a specific accelerator (NPU vs GPU vs CPU). Same input
  /// shape and output handling as the single-session overload.</summary>
  private static float[]? RunSession(SessionInfo info, InferenceSession session, float[] inputTensor, int h, int w) {
    try {
      var input = new DenseTensor<float>(inputTensor, new[] { 1, 3, h, w });
      using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(info.InputName, input) });
      return results.First().AsTensor<float>().ToArray();
    } catch {
      return null;
    }
  }

  private static void WriteTile(Image<Rgba32> target, float[] tilePixels, int tileW, int tileH,
                                int destX, int destY, int destW, int destH) {
    // tilePixels is laid out [3, tileH, tileW]. The destination region we
    // actually want to keep is destW × destH starting at (destX, destY) —
    // for fixed-shape models the model output may be larger than the
    // source-region we asked about, so we crop to destW/destH on copy.
    var copyW = Math.Min(destW, tileW);
    var copyH = Math.Min(destH, tileH);
    copyW = Math.Min(copyW, target.Width - destX);
    copyH = Math.Min(copyH, target.Height - destY);
    if (copyW <= 0 || copyH <= 0)
      return;

    var pixelCount = tileH * tileW;
    target.ProcessPixelRows(accessor => {
      for (var y = 0; y < copyH; y++) {
        var dstRow = accessor.GetRowSpan(destY + y);
        for (var x = 0; x < copyW; x++) {
          var offset = y * tileW + x;
          var dr = Math.Clamp(tilePixels[0 * pixelCount + offset], 0f, 1f);
          var dg = Math.Clamp(tilePixels[1 * pixelCount + offset], 0f, 1f);
          var db = Math.Clamp(tilePixels[2 * pixelCount + offset], 0f, 1f);
          dstRow[destX + x] = new Rgba32(ToByte(dr), ToByte(dg), ToByte(db), (byte)255);
        }
      }
    });
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
      var inputDims = session.InputMetadata[inputName].Dimensions;
      // NCHW shape: [batch, channels, H, W]. A dimension > 0 is fixed; <= 0 is dynamic.
      var fixedH = inputDims.Length >= 4 && inputDims[2] > 0 ? inputDims[2] : -1;
      var fixedW = inputDims.Length >= 4 && inputDims[3] > 0 ? inputDims[3] : -1;
      var isFixed = fixedH > 0 && fixedW > 0;
      var tileSize = isFixed ? Math.Min(fixedH, fixedW) : DefaultTileSize;
      var tileOverlap = isFixed ? Math.Max(0, tileSize / 8) : DefaultTileOverlap;

      // Native factor = output H / input H. Dynamic-output models can't be
      // queried statically, so we fall back to 4× (the canonical Real-ESRGAN).
      var nativeFactor = 4;
      var outputName = session.OutputMetadata.Keys.First();
      var outputDims = session.OutputMetadata[outputName].Dimensions;
      if (isFixed && outputDims.Length >= 4 && outputDims[2] > 0)
        nativeFactor = Math.Max(1, outputDims[2] / fixedH);

      return new SessionInfo(session, inputName, tileSize, tileOverlap, isFixed, nativeFactor, modelFile.FullName);
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
    int TileSize,
    int TileOverlap,
    bool IsFixedInputSize,
    int NativeFactor,
    string ModelPath);
}
