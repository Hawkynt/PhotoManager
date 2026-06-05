using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Panorama;

/// <summary>
/// Naive panorama assembler for cylindrically-warped frames captured on a
/// tripod. Each frame is aligned to the running canvas by minimising the
/// sum-of-squared-differences across a horizontal-plus-small-vertical search
/// window in the overlap region; matched frames are then linearly feathered
/// across that overlap. Good enough for hand-held panoramic sweeps; falls
/// over for big parallax or tilt — those go through OpenCv.
/// </summary>
public static class TripodPanoramaStitcher {
  private const int VerticalSearchHalfRange = 24;

  /// <summary>
  /// Stitch <paramref name="cylindricallyWarped"/> left-to-right.
  /// <paramref name="overlapHint"/> is the fraction of frame width that is
  /// expected to overlap with the previous frame (clamped to 0.05–0.9).
  /// Returns the same image when only one frame is supplied.
  /// <para><paramref name="masks"/>, when non-null, must contain one
  /// <see cref="Image{L8}"/> per frame with matching dimensions. Mask alpha
  /// (0..1 from byte/255) multiplies the linear-feathering blend weight, so
  /// blurry regions in frame N don't override sharp regions in the canvas.</para>
  /// </summary>
  public static Image<Rgba32> Stitch(
      IReadOnlyList<Image<Rgba32>> cylindricallyWarped,
      double overlapHint = 0.3,
      IReadOnlyList<Image<L8>>? masks = null) {
    ArgumentNullException.ThrowIfNull(cylindricallyWarped);
    if (cylindricallyWarped.Count == 0)
      throw new ArgumentException("At least one frame required.", nameof(cylindricallyWarped));
    overlapHint = Math.Clamp(overlapHint, 0.05, 0.9);
    if (masks is not null) {
      if (masks.Count != cylindricallyWarped.Count)
        throw new ArgumentException("Mask count must match frame count.", nameof(masks));
      for (var i = 0; i < masks.Count; i++) {
        if (masks[i].Width != cylindricallyWarped[i].Width || masks[i].Height != cylindricallyWarped[i].Height)
          throw new ArgumentException($"Mask {i} dimensions don't match frame dimensions.", nameof(masks));
      }
    }

    if (cylindricallyWarped.Count == 1)
      return cylindricallyWarped[0].Clone();

    var canvas = cylindricallyWarped[0].Clone();
    var canvasMask = masks is null ? null : ExtractMaskBytes(masks[0]);
    var canvasW = cylindricallyWarped[0].Width;
    for (var i = 1; i < cylindricallyWarped.Count; i++) {
      var frameMask = masks is null ? null : ExtractMaskBytes(masks[i]);
      (canvas, canvasMask, canvasW) = AppendFrame(canvas, canvasMask, canvasW, cylindricallyWarped[i], frameMask, overlapHint);
    }

    return canvas;
  }

  private static (Image<Rgba32> result, byte[]? mask, int width) AppendFrame(
      Image<Rgba32> canvas, byte[]? canvasMask, int trackedCanvasWidth,
      Image<Rgba32> frame, byte[]? frameMask,
      double overlapHint) {
    var canvasW = canvas.Width;
    var canvasH = canvas.Height;
    var frameW = frame.Width;
    var frameH = frame.Height;

    var canvasPixels = new Rgba32[canvasW * canvasH];
    canvas.CopyPixelDataTo(canvasPixels);
    var framePixels = new Rgba32[frameW * frameH];
    frame.CopyPixelDataTo(framePixels);

    var nominalOverlap = (int)Math.Round(frameW * overlapHint);
    nominalOverlap = Math.Clamp(nominalOverlap, 8, Math.Min(canvasW, frameW) - 1);

    // Search the horizontal placement of the new frame's left edge: the
    // expected position puts overlap pixels of the new frame on top of the
    // canvas's right edge. We try a ±25% window around that around the
    // hint, and a small vertical wiggle to absorb hand-held tilt.
    var expectedLeftOnCanvas = canvasW - nominalOverlap;
    var hSearch = Math.Max(8, nominalOverlap / 2);
    var (bestDx, bestDy) = FindBestTranslation(
      canvasPixels, canvasW, canvasH,
      framePixels, frameW, frameH,
      expectedLeftOnCanvas, hSearch, VerticalSearchHalfRange);

    var leftOnCanvas = bestDx;
    var topOnCanvas = bestDy;

    var minX = Math.Min(0, leftOnCanvas);
    var maxX = Math.Max(canvasW, leftOnCanvas + frameW);
    var minY = Math.Min(0, topOnCanvas);
    var maxY = Math.Max(canvasH, topOnCanvas + frameH);

    var outW = maxX - minX;
    var outH = maxY - minY;
    var outPixels = new Rgba32[outW * outH];
    byte[]? outMask = (canvasMask is not null || frameMask is not null) ? new byte[outW * outH] : null;

    var canvasOffsetX = -minX;
    var canvasOffsetY = -minY;
    var frameOffsetX = leftOnCanvas - minX;
    var frameOffsetY = topOnCanvas - minY;

    BlitOpaque(outPixels, outW, canvasPixels, canvasW, canvasH, canvasOffsetX, canvasOffsetY,
               outMask, canvasMask);
    BlendFrame(outPixels, outW, outH, framePixels, frameW, frameH, frameOffsetX, frameOffsetY,
               canvasOffsetX, canvasW, outMask, canvasMask, frameMask, canvasOffsetY);

    var result = new Image<Rgba32>(outW, outH);
    result.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var src = y * outW;
        for (var x = 0; x < row.Length; x++)
          row[x] = outPixels[src + x];
      }
    });

    canvas.Dispose();
    return (result, outMask, outW);
  }

  private static (int dx, int dy) FindBestTranslation(
      Rgba32[] canvas, int canvasW, int canvasH,
      Rgba32[] frame, int frameW, int frameH,
      int expectedLeftOnCanvas, int hHalfRange, int vHalfRange) {
    var bestScore = double.MaxValue;
    var bestDx = expectedLeftOnCanvas;
    var bestDy = 0;

    var dxLo = expectedLeftOnCanvas - hHalfRange;
    var dxHi = expectedLeftOnCanvas + hHalfRange;
    var stride = Math.Max(1, hHalfRange / 12);

    for (var dx = dxLo; dx <= dxHi; dx += stride) {
      for (var dy = -vHalfRange; dy <= vHalfRange; dy += 2) {
        var score = ScoreOverlap(canvas, canvasW, canvasH, frame, frameW, frameH, dx, dy);
        if (score < bestScore) {
          bestScore = score;
          bestDx = dx;
          bestDy = dy;
        }
      }
    }

    // Refine around best with a finer search.
    var refinedLo = bestDx - stride;
    var refinedHi = bestDx + stride;
    for (var dx = refinedLo; dx <= refinedHi; dx++) {
      for (var dy = bestDy - 2; dy <= bestDy + 2; dy++) {
        var score = ScoreOverlap(canvas, canvasW, canvasH, frame, frameW, frameH, dx, dy);
        if (score < bestScore) {
          bestScore = score;
          bestDx = dx;
          bestDy = dy;
        }
      }
    }

    return (bestDx, bestDy);
  }

  private static double ScoreOverlap(
      Rgba32[] canvas, int canvasW, int canvasH,
      Rgba32[] frame, int frameW, int frameH,
      int dx, int dy) {
    var x0 = Math.Max(0, dx);
    var x1 = Math.Min(canvasW, dx + frameW);
    var y0 = Math.Max(0, dy);
    var y1 = Math.Min(canvasH, dy + frameH);

    var overlapW = x1 - x0;
    var overlapH = y1 - y0;
    if (overlapW <= 0 || overlapH <= 0)
      return double.MaxValue;
    if (overlapW * overlapH < 64)
      return double.MaxValue;

    long sum = 0;
    long count = 0;
    var step = Math.Max(1, overlapW / 64) + Math.Max(1, overlapH / 64);
    for (var y = y0; y < y1; y += step) {
      for (var x = x0; x < x1; x += step) {
        var cIdx = y * canvasW + x;
        var fIdx = (y - dy) * frameW + (x - dx);
        var c = canvas[cIdx];
        var f = frame[fIdx];
        if (c.A == 0 || f.A == 0)
          continue;
        var dr = c.R - f.R;
        var dg = c.G - f.G;
        var db = c.B - f.B;
        sum += dr * dr + dg * dg + db * db;
        count++;
      }
    }
    if (count == 0)
      return double.MaxValue;
    return sum / (double)count;
  }

  private static void BlitOpaque(
      Rgba32[] dest, int destW, Rgba32[] src, int srcW, int srcH, int dx, int dy,
      byte[]? destMask, byte[]? srcMask) {
    for (var y = 0; y < srcH; y++) {
      var dstRow = (y + dy) * destW + dx;
      var srcRow = y * srcW;
      for (var x = 0; x < srcW; x++) {
        var pixel = src[srcRow + x];
        if (pixel.A == 0)
          continue;
        var di = dstRow + x;
        dest[di] = pixel;
        if (destMask is not null)
          destMask[di] = srcMask is not null ? srcMask[srcRow + x] : (byte)255;
      }
    }
  }

  /// <summary>
  /// Extract the L8 mask channel into a flat byte buffer for fast indexed
  /// access during blending. Mirror of the inline copy used by ToBgrMat in
  /// the OpenCV stitchers.
  /// </summary>
  private static byte[] ExtractMaskBytes(Image<L8> mask) {
    var width = mask.Width;
    var height = mask.Height;
    var bytes = new byte[width * height];
    mask.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var dst = y * width;
        for (var x = 0; x < row.Length; x++)
          bytes[dst + x] = row[x].PackedValue;
      }
    });
    return bytes;
  }

  /// <summary>
  /// Blend the new frame onto the canvas with linear feathering across the
  /// horizontal overlap zone (right edge of canvas / left edge of frame).
  /// Outside the overlap the frame is copied verbatim; inside, the weight
  /// ramps from 0 at the left of the overlap (canvas dominates) to 1 at the
  /// right edge (frame dominates). When sharpness masks are supplied, the
  /// per-pixel blend weight is multiplied by mask[x,y]/255 — a blurry pixel
  /// in the new frame can't override a sharp pixel already on the canvas.
  /// </summary>
  private static void BlendFrame(
      Rgba32[] dest, int destW, int destH,
      Rgba32[] frame, int frameW, int frameH,
      int frameOffsetX, int frameOffsetY,
      int canvasOffsetX, int canvasW,
      byte[]? destMask, byte[]? canvasMask, byte[]? frameMask, int canvasOffsetY) {
    // Overlap zone in dest coords: where canvas (x in [canvasOffsetX, canvasOffsetX + canvasW))
    // intersects the frame footprint.
    var canvasRightEdge = canvasOffsetX + canvasW;
    var frameLeftEdge = frameOffsetX;
    var overlapStart = Math.Max(frameLeftEdge, canvasOffsetX);
    var overlapEnd = Math.Min(canvasRightEdge, frameLeftEdge + frameW);
    var overlapWidth = Math.Max(0, overlapEnd - overlapStart);

    for (var y = 0; y < frameH; y++) {
      var destY = y + frameOffsetY;
      if (destY < 0 || destY >= destH)
        continue;
      for (var x = 0; x < frameW; x++) {
        var destX = x + frameOffsetX;
        if (destX < 0 || destX >= destW)
          continue;
        var fIdx = y * frameW + x;
        var fp = frame[fIdx];
        if (fp.A == 0)
          continue;

        var di = destY * destW + destX;
        var existing = dest[di];
        var fMask = frameMask is null ? (byte)255 : frameMask[fIdx];
        var fAlpha = fMask / 255.0;

        if (existing.A == 0 || destX < overlapStart || destX >= overlapEnd || overlapWidth <= 0) {
          // No canvas pixel here — write frame outright (any mask reduction
          // would just create holes the smart-patch step already decided
          // weren't worth keeping).
          dest[di] = fp;
          if (destMask is not null)
            destMask[di] = fMask;
          continue;
        }

        var t = (destX - overlapStart) / (double)overlapWidth;
        var baseW = Math.Clamp(t, 0.0, 1.0);
        // Multiply the linear-feather weight by the mask alpha so blurry
        // pixels from the incoming frame can't override sharp pixels
        // already on the canvas. Renormalise against the canvas's own
        // mask alpha so a blurry canvas pixel still loses to a sharper
        // incoming one.
        var w = baseW * fAlpha;
        if (destMask is not null) {
          var cAlpha = destMask[di] / 255.0;
          var denom = baseW * fAlpha + (1.0 - baseW) * cAlpha;
          if (denom > 0)
            w = (baseW * fAlpha) / denom;
        }
        // canvasMask param is intentionally unused: the dest mask written
        // by BlitOpaque already carries the per-pixel canvas confidence.
        _ = canvasMask;
        var inv = 1.0 - w;
        dest[di] = new Rgba32(
          (byte)(existing.R * inv + fp.R * w),
          (byte)(existing.G * inv + fp.G * w),
          (byte)(existing.B * inv + fp.B * w),
          (byte)Math.Max(existing.A, fp.A));
        if (destMask is not null)
          destMask[di] = (byte)Math.Max(destMask[di], fMask);
      }
    }
  }
}
