using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Segmentation;

/// <summary>
/// ONNX-backed image inpainter (LaMa). Same degrade-gracefully pattern as
/// <see cref="OnnxDenoiser"/>: <see cref="IsAvailable"/> false when the
/// model is missing and <see cref="Inpaint"/> returns null in that case.
///
/// LaMa expects fixed 1×3×512×512 image input + 1×1×512×512 mask input
/// (1 = inpaint here, 0 = keep). We tile arbitrary-sized images with
/// 64-pixel overlap so seams are barely visible, and we only run tiles
/// whose mask region is non-empty — clean tiles are passed through
/// unchanged. Output is the inpainted RGB image at the source's
/// resolution, with the mask region replaced by the model's prediction
/// (feathered into the unmasked surroundings).
/// </summary>
public sealed class OnnxInpainter : IDisposable {
  public const string DefaultModelFileName = "inpaint-lama.onnx";

  /// <summary>The dimension LaMa's ONNX export is fixed to.</summary>
  public const int TileSize = 512;

  /// <summary>Tile overlap so that adjacent tiles' results blend across a feathered band instead of cutting at a hard edge.</summary>
  private const int TileOverlap = 64;

  private readonly Lazy<SessionInfo?> _session;

  public OnnxInpainter(FileInfo? modelFile = null) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._session = new Lazy<SessionInfo?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Run LaMa over <paramref name="source"/> wherever
  /// <paramref name="mask"/> is non-zero. The mask is single-channel
  /// (read from the mask image's R channel — we treat any non-zero R as
  /// "inpaint here", consistent with how the brush canvas writes a red
  /// alpha-blended overlay). Returns a freshly allocated image at the
  /// source's resolution; null when no model is available.
  /// </summary>
  /// <param name="source">Image to inpaint. Not mutated.</param>
  /// <param name="mask">Same dimensions as <paramref name="source"/>.
  ///   Non-zero R channel = mask region to inpaint.</param>
  /// <param name="ct">Cancels between tiles.</param>
  public Image<Rgba32>? Inpaint(Image<Rgba32> source, Image<Rgba32> mask, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(mask);
    if (mask.Width != source.Width || mask.Height != source.Height)
      throw new ArgumentException("Mask must match source dimensions.", nameof(mask));

    var info = this._session.Value;
    if (info == null)
      return null;

    var output = source.Clone();

    // Pre-compute a flat float[] of mask values to make per-tile lookup
    // cheap. Pixels with R >= 128 are considered inside the mask.
    var maskFlat = new float[mask.Width * mask.Height];
    mask.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var off = y * mask.Width;
        for (var x = 0; x < row.Length; x++)
          maskFlat[off + x] = row[x].R >= 128 ? 1f : 0f;
      }
    });

    var stride = TileSize - TileOverlap;
    for (var ty = 0; ty < source.Height; ty += stride) {
      for (var tx = 0; tx < source.Width; tx += stride) {
        ct.ThrowIfCancellationRequested();

        // Each tile is exactly TileSize×TileSize centred at (tx, ty),
        // clamped so the right/bottom edge tiles stay within the source.
        var x0 = Math.Min(tx, Math.Max(0, source.Width  - TileSize));
        var y0 = Math.Min(ty, Math.Max(0, source.Height - TileSize));
        var w  = Math.Min(TileSize, source.Width);
        var h  = Math.Min(TileSize, source.Height);

        // Skip tiles that don't intersect any masked pixel — saves a
        // session run, and the unmasked pixels stay untouched.
        if (!TileHasMask(maskFlat, mask.Width, x0, y0, w, h))
          continue;

        var tileOutput = RunTile(info, source, mask, maskFlat, x0, y0, w, h);
        if (tileOutput == null)
          continue;

        BlendTile(output, tileOutput, maskFlat, mask.Width, x0, y0, w, h);
      }
    }

    return output;
  }

  /// <summary>Quick scan over the tile's mask values to decide whether it's worth running.</summary>
  private static bool TileHasMask(float[] mask, int maskW, int x0, int y0, int w, int h) {
    for (var y = 0; y < h; y++) {
      var off = (y0 + y) * maskW + x0;
      for (var x = 0; x < w; x++)
        if (mask[off + x] > 0)
          return true;
    }
    return false;
  }

  /// <summary>
  /// Build the (image, mask) tensors for one tile, run LaMa, return the
  /// 512×512 output as a flat float[3*512*512] in [0..255] BGR.
  /// LaMa's reference C++/Python pipeline (opencv/inpainting_lama)
  /// feeds OpenCV-loaded images straight to <c>cv.dnn.blobFromImage</c>
  /// with <c>scalefactor=1/255</c> and <c>swapRB=False</c> — so the
  /// model expects BGR in [0..1] and emits BGR in [0..255]. Treating
  /// the output as [0..1] RGB clamps everything to 1.0 → white blobs;
  /// reading channel 0 as R inpaints with the wrong colour.
  /// </summary>
  private static float[]? RunTile(SessionInfo info, Image<Rgba32> source, Image<Rgba32> mask, float[] maskFlat,
                                   int x0, int y0, int w, int h) {
    var pc = TileSize * TileSize;
    var imgTensor = new float[3 * pc];
    var mskTensor = new float[1 * pc];

    // Pad with the source's edge pixels for tiles smaller than TileSize.
    // Channel 0 = B, 2 = R per OpenCV's BGR convention.
    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < TileSize; y++) {
        var sy = Math.Min(y0 + y, accessor.Height - 1);
        var row = accessor.GetRowSpan(sy);
        for (var x = 0; x < TileSize; x++) {
          var sx = Math.Min(x0 + x, row.Length - 1);
          var px = row[sx];
          var off = y * TileSize + x;
          imgTensor[0 * pc + off] = px.B / 255f;
          imgTensor[1 * pc + off] = px.G / 255f;
          imgTensor[2 * pc + off] = px.R / 255f;
        }
      }
    });

    // Mask: only the actual source-area; out-of-bounds counts as "keep" (0).
    var srcW = source.Width;
    var srcH = source.Height;
    for (var y = 0; y < TileSize; y++) {
      var sy = y0 + y;
      var off = y * TileSize;
      for (var x = 0; x < TileSize; x++) {
        var sx = x0 + x;
        if (sx >= 0 && sx < srcW && sy >= 0 && sy < srcH)
          mskTensor[off + x] = maskFlat[sy * srcW + sx];
        else
          mskTensor[off + x] = 0f;
      }
    }

    try {
      var img = new DenseTensor<float>(imgTensor, new[] { 1, 3, TileSize, TileSize });
      var msk = new DenseTensor<float>(mskTensor, new[] { 1, 1, TileSize, TileSize });
      using var results = info.Session.Run(new[] {
        NamedOnnxValue.CreateFromTensor(info.ImageInputName, img),
        NamedOnnxValue.CreateFromTensor(info.MaskInputName, msk)
      });
      return results.First().AsTensor<float>().ToArray();
    } catch {
      return null;
    }
  }

  /// <summary>
  /// Write the 512×512 inpainted tile back into the corresponding
  /// region of <paramref name="output"/>, but only at masked pixels —
  /// outside the mask we keep the source untouched. A small linear
  /// feather across the mask boundary hides the seam.
  /// </summary>
  private static void BlendTile(Image<Rgba32> output, float[] tile, float[] maskFlat, int maskW,
                                 int x0, int y0, int w, int h) {
    const int featherPx = 6;
    var pc = TileSize * TileSize;

    output.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var ty = y0 + y;
        if (ty < 0 || ty >= accessor.Height)
          continue;
        var row = accessor.GetRowSpan(ty);
        var maskRow = ty * maskW;
        for (var x = 0; x < w; x++) {
          var tx = x0 + x;
          if (tx < 0 || tx >= row.Length)
            continue;

          var maskAtPixel = maskFlat[maskRow + tx];
          if (maskAtPixel <= 0)
            continue;  // Only touch masked pixels.

          // Compute per-pixel feather weight: distance to the nearest
          // unmasked pixel, clamped to featherPx, normalised to [0..1].
          var weight = ComputeFeatherWeight(maskFlat, maskW, tx, ty, featherPx);

          var off = y * TileSize + x;
          // LaMa's output is BGR in [0..255] (not [0..1]). Channel 0 = B,
          // channel 2 = R — same convention as the input we fed it.
          var b = Math.Clamp(tile[0 * pc + off], 0f, 255f);
          var g = Math.Clamp(tile[1 * pc + off], 0f, 255f);
          var r = Math.Clamp(tile[2 * pc + off], 0f, 255f);

          var px = row[tx];
          row[tx] = new Rgba32(
            (byte)Math.Round(px.R * (1 - weight) + r * weight),
            (byte)Math.Round(px.G * (1 - weight) + g * weight),
            (byte)Math.Round(px.B * (1 - weight) + b * weight),
            px.A
          );
        }
      }
    });
  }

  /// <summary>
  /// Cheap chebyshev-distance feather: 1.0 when the pixel sits at least
  /// <paramref name="featherPx"/> inside the masked region, ramps to 0
  /// at the mask edge. Avoids visible seams without a full Gaussian blur.
  /// </summary>
  private static float ComputeFeatherWeight(float[] maskFlat, int maskW, int x, int y, int featherPx) {
    var maskH = maskFlat.Length / maskW;
    var minDist = featherPx;
    for (var dy = -featherPx; dy <= featherPx; dy++) {
      var ny = y + dy;
      if (ny < 0 || ny >= maskH) continue;
      for (var dx = -featherPx; dx <= featherPx; dx++) {
        var nx = x + dx;
        if (nx < 0 || nx >= maskW) continue;
        if (maskFlat[ny * maskW + nx] <= 0) {
          var d = Math.Max(Math.Abs(dx), Math.Abs(dy));
          if (d < minDist)
            minDist = d;
        }
      }
    }
    return Math.Min(1f, (float)minDist / featherPx);
  }

  private static SessionInfo? TryOpenSession(FileInfo modelFile) {
    try {
      if (!modelFile.Exists)
        return null;
      // LaMa-Fourier uses Fast Fourier Convolution (FFC) blocks that
      // include Pad and DequantizeLinear ops OpenVINO 1.20 can't
      // import correctly — the session creates without exceptions but
      // Run() then silently produces a copy of the input image
      // (verified empirically: a 10×384 px synthetic mask bar over the
      // middle of a photo produced output identical to input). Force
      // CPU EP here until the LaMa export is rebuilt without those ops
      // or OpenVINO catches up. CPU is ~10× slower than NPU for LaMa
      // but at least the output is correct — silent no-op was hiding
      // every auto-loop run.
      var session = OnnxAcceleration.CreateSession(modelFile.FullName, preferCpu: true);
      // The opencv mirror's input names are "image" and "mask". Pick
      // them by metadata so any future re-export with different names
      // still works (we look at element-count to disambiguate).
      string? imageName = null, maskName = null;
      foreach (var kv in session.InputMetadata) {
        var dims = kv.Value.Dimensions;
        if (dims.Length == 4 && dims[1] == 3)      imageName = kv.Key;
        else if (dims.Length == 4 && dims[1] == 1) maskName  = kv.Key;
      }
      if (imageName == null || maskName == null)
        return null;
      return new SessionInfo(session, imageName, maskName);
    } catch {
      return null;
    }
  }

  public void Dispose() {
    // Sessions are cached by OnnxAcceleration and shared across
    // instances; disposing them here would break the cache.
    // OnnxAcceleration.ResetCache() handles teardown if needed.
  }

  private sealed record SessionInfo(InferenceSession Session, string ImageInputName, string MaskInputName);
}
