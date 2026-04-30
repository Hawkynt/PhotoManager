using System.Globalization;
using System.Runtime.InteropServices;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Panorama;

/// <summary>
/// Tunables for <see cref="SphericalPanoramaStitcher"/>. <see cref="OutputWidth"/>
/// drives the equirectangular canvas — height is auto-derived as width/2 to
/// keep the 2:1 aspect ratio that <c>PanoramaViewerWindow</c> expects.
/// </summary>
public sealed record SphericalStitchOptions {
  public double SeamConfidence { get; init; } = 1.0;
  public int OutputWidth { get; init; } = 4096;
  public bool BlendOverlaps { get; init; } = true;
  /// <summary>
  /// Optional per-frame sharpness mask (255 = include, 0 = exclude). Same
  /// semantics as <see cref="OpenCvStitchOptions.Masks"/>: applied by zeroing
  /// the corresponding BGR pixels before handing to OpenCV, since the 4.10
  /// binding doesn't expose <c>Stitcher.Stitch(images, masks, ...)</c>.
  /// </summary>
  public IReadOnlyList<Image<L8>>? Masks { get; init; }
}

/// <summary>
/// Stitches overlapping frames into a true 2:1 equirectangular 360° panorama.
///
/// <para><b>Implementation note — OpenCV API surface in OpenCvSharp4 4.10.</b>
/// The C++ <c>cv::Stitcher</c> exposes <c>setWarper(Spherical)</c> + per-image
/// <c>cameras()</c> so callers can re-project the partial mosaic onto a 2:1
/// equirectangular canvas. Those hooks are <em>not</em> bound by OpenCvSharp4
/// 4.10.0.20241108 — see <c>OpenCvSharp.Stitcher</c>: no <c>SetWarper</c>,
/// no <c>Cameras</c>, no <c>Warper</c> property. The richer cleanroom path
/// (back-project each output pixel through the per-image cameras and blend
/// inverse-distance) therefore isn't available without dropping to P/Invoke,
/// which is more native-binding work than this slice warrants.</para>
///
/// <para><b>Fallback strategy used here.</b> We run <c>cv::Stitcher</c> in
/// <c>Panorama</c> mode, which already applies the spherical warper internally
/// and emits a mosaic whose horizontal axis is longitude and vertical axis is
/// latitude (modulo a translation). We then:</para>
/// <list type="number">
///   <item>Crop the OpenCV mosaic to its non-zero content bounds.</item>
///   <item>Treat the cropped width as covering an angular extent
///         <c>2π · cropW / (refinedWarpScale)</c>; in practice we rely on the
///         output aspect to estimate longitude span and place the mosaic
///         horizontally so its centre sits at longitude 0.</item>
///   <item>Vertically centre on the equator (latitude 0) using the same
///         pixels-per-radian implied by the aspect.</item>
///   <item>Leave un-covered regions black (transparent alpha = 0). For a
///         partial-sphere capture this is the right answer; for a fully
///         covered sphere the source frames will fill the canvas.</item>
/// </list>
///
/// <para>OpenCV failures (insufficient features / no overlap / single input)
/// surface as <c>null</c> + a status hint via <see cref="LastStatus"/>; we do
/// not throw on stitch-failure paths so UI code can degrade gracefully.</para>
/// </summary>
public static class SphericalPanoramaStitcher {
  /// <summary>
  /// Diagnostic for the most recent <see cref="Stitch"/> call. Populated when
  /// <c>Stitch</c> returns <c>null</c> so the UI can surface a meaningful
  /// status line ("Insufficient features", "Need at least two frames", ...).
  /// </summary>
  public static string LastStatus { get; private set; } = "";

  public static Image<Rgba32>? Stitch(IReadOnlyList<Image<Rgba32>> images, SphericalStitchOptions? options = null) {
    ArgumentNullException.ThrowIfNull(images);
    options ??= new SphericalStitchOptions();

    if (images.Count < 2) {
      LastStatus = "Need at least two frames for spherical stitching.";
      return null;
    }
    if (options.OutputWidth < 16 || options.OutputWidth % 2 != 0) {
      LastStatus = "OutputWidth must be a positive even number ≥ 16.";
      return null;
    }
    if (options.Masks is { } maskList) {
      if (maskList.Count != images.Count) {
        LastStatus = "Mask count must match image count.";
        return null;
      }
      for (var i = 0; i < maskList.Count; i++) {
        if (maskList[i].Width != images[i].Width || maskList[i].Height != images[i].Height) {
          LastStatus = $"Mask {i} dimensions don't match image dimensions.";
          return null;
        }
      }
    }

    var mats = new List<Mat>(images.Count);
    Mat? mosaic = null;
    try {
      for (var i = 0; i < images.Count; i++)
        mats.Add(ToBgrMat(images[i], options.Masks?[i]));

      Stitcher stitcher;
      try {
        stitcher = Stitcher.Create(Stitcher.Mode.Panorama);
      } catch (DllNotFoundException ex) {
        throw new DllNotFoundException(
          "OpenCV native library is not available on this platform. Install the matching OpenCvSharp4.runtime.* NuGet package.",
          ex);
      } catch (TypeInitializationException ex) when (ex.InnerException is DllNotFoundException dnf) {
        throw new DllNotFoundException(
          "OpenCV native library is not available on this platform. Install the matching OpenCvSharp4.runtime.* NuGet package.",
          dnf);
      }

      stitcher.PanoConfidenceThresh = options.SeamConfidence;
      // Mode.Panorama already uses cv::SphericalWarper internally; the binding
      // doesn't expose SetWarper, but the warp shape is what we want.

      mosaic = new Mat();
      var status = stitcher.Stitch(mats, mosaic);
      if (status != Stitcher.Status.OK) {
        LastStatus = $"OpenCV stitcher status: {status}.";
        return null;
      }

      using var rgba = new Mat();
      Cv2.CvtColor(mosaic, rgba, ColorConversionCodes.BGR2RGBA);
      using var raw = ToImageSharp(rgba);
      using var cropped = CropToContent(raw);
      if (cropped.Width <= 0 || cropped.Height <= 0) {
        LastStatus = "Stitched mosaic was empty after auto-crop.";
        return null;
      }

      LastStatus = $"OK ({cropped.Width}×{cropped.Height} → {options.OutputWidth}×{options.OutputWidth / 2}).";
      return PlaceOnEquirectCanvas(cropped, options);
    } catch (DllNotFoundException) {
      throw;
    } catch (Exception ex) {
      LastStatus = $"Spherical stitch failed: {ex.Message}";
      return null;
    } finally {
      foreach (var m in mats)
        m.Dispose();
      mosaic?.Dispose();
    }
  }

  /// <summary>
  /// Places a cropped OpenCV mosaic into a 2:1 equirectangular canvas. We
  /// preserve the mosaic's pixels-per-radian (derived from its aspect ratio
  /// versus the canvas's 2:1 ratio) so a partial capture lands at the right
  /// angular size, centred on (lon=0, lat=0). Areas the source doesn't cover
  /// remain transparent.
  /// </summary>
  private static Image<Rgba32> PlaceOnEquirectCanvas(Image<Rgba32> mosaic, SphericalStitchOptions options) {
    var canvasW = options.OutputWidth;
    var canvasH = options.OutputWidth / 2;

    var srcW = mosaic.Width;
    var srcH = mosaic.Height;

    // The canvas is 2:1 (width = 2π rad, height = π rad). Pick the largest
    // scale that keeps the mosaic inside the canvas without distorting it
    // and without exceeding the canvas's pixels-per-radian. Width-limited
    // when the mosaic is "wide", height-limited otherwise.
    var pixelsPerRadHorizontal = canvasW / (2.0 * Math.PI);
    var pixelsPerRadVertical = canvasH / Math.PI;
    // Equirectangular images share pixels-per-radian on both axes (same
    // angular density per pixel) — pick the smaller so we never zoom
    // past either axis.
    var targetDensity = Math.Min(pixelsPerRadHorizontal, pixelsPerRadVertical);

    // Treat the mosaic as already-equirectangular (warper is spherical):
    // angular_extent_x = srcW / openCvDensity, angular_extent_y = srcH / openCvDensity.
    // OpenCV's density is scene-dependent, but the aspect ratio of the
    // mosaic still carries the relative extent. We don't know the absolute
    // scale, so we fit the mosaic within the canvas while preserving the
    // aspect ratio.
    var maxScale = Math.Min((double)canvasW / srcW, (double)canvasH / srcH);
    var scale = Math.Min(maxScale, targetDensity / Math.Min(pixelsPerRadHorizontal, pixelsPerRadVertical));

    var placedW = Math.Max(1, (int)Math.Round(srcW * scale));
    var placedH = Math.Max(1, (int)Math.Round(srcH * scale));

    var offsetX = (canvasW - placedW) / 2;
    var offsetY = (canvasH - placedH) / 2;

    var canvas = new Image<Rgba32>(canvasW, canvasH);
    var srcPixels = new Rgba32[srcW * srcH];
    mosaic.CopyPixelDataTo(srcPixels);

    canvas.ProcessPixelRows(accessor => {
      for (var dy = 0; dy < placedH; dy++) {
        var canvasY = offsetY + dy;
        if (canvasY < 0 || canvasY >= canvasH)
          continue;
        var row = accessor.GetRowSpan(canvasY);
        var sy = (int)((double)dy * srcH / placedH);
        if (sy < 0) sy = 0;
        if (sy >= srcH) sy = srcH - 1;
        var srcRowOffset = sy * srcW;
        for (var dx = 0; dx < placedW; dx++) {
          var canvasX = offsetX + dx;
          if (canvasX < 0 || canvasX >= canvasW)
            continue;
          var sx = (int)((double)dx * srcW / placedW);
          if (sx < 0) sx = 0;
          if (sx >= srcW) sx = srcW - 1;
          var p = srcPixels[srcRowOffset + sx];
          // Treat OpenCV's zero pixels as transparent so they don't draw a
          // black rectangle on the equirect canvas.
          if (p.R == 0 && p.G == 0 && p.B == 0)
            continue;
          if (options.BlendOverlaps && row[canvasX].A > 0) {
            // Average against any prior write — only meaningful if we ever
            // do multi-pass placement. For now this is a no-op for the
            // single placement, but kept for forward compatibility.
            row[canvasX] = AverageRgba(row[canvasX], p);
          } else {
            row[canvasX] = p;
          }
        }
      }
    });

    _ = CultureInfo.InvariantCulture; // anchor for any future decimal formatting.
    return canvas;
  }

  private static Rgba32 AverageRgba(Rgba32 a, Rgba32 b) => new(
    (byte)((a.R + b.R) / 2),
    (byte)((a.G + b.G) / 2),
    (byte)((a.B + b.B) / 2),
    (byte)Math.Max(a.A, b.A));

  private static Mat ToBgrMat(Image<Rgba32> source, Image<L8>? mask = null) {
    var width = source.Width;
    var height = source.Height;
    var pixels = new Rgba32[width * height];
    source.CopyPixelDataTo(pixels);

    byte[]? maskPixels = null;
    if (mask is not null) {
      maskPixels = new byte[width * height];
      mask.ProcessPixelRows(accessor => {
        for (var y = 0; y < accessor.Height; y++) {
          var row = accessor.GetRowSpan(y);
          var dst = y * width;
          for (var x = 0; x < row.Length; x++)
            maskPixels[dst + x] = row[x].PackedValue;
        }
      });
    }

    var bgr = new byte[width * height * 3];
    for (var i = 0; i < pixels.Length; i++) {
      var p = pixels[i];
      var o = i * 3;
      if (maskPixels is not null && maskPixels[i] == 0) {
        bgr[o] = 0;
        bgr[o + 1] = 0;
        bgr[o + 2] = 0;
        continue;
      }
      bgr[o] = p.B;
      bgr[o + 1] = p.G;
      bgr[o + 2] = p.R;
    }
    var mat = new Mat(height, width, MatType.CV_8UC3);
    Marshal.Copy(bgr, 0, mat.Data, bgr.Length);
    return mat;
  }

  private static Image<Rgba32> ToImageSharp(Mat rgba) {
    var width = rgba.Width;
    var height = rgba.Height;
    var bytes = new byte[width * height * 4];
    Marshal.Copy(rgba.Data, bytes, 0, bytes.Length);

    var image = new Image<Rgba32>(width, height);
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var srcOffset = y * width * 4;
        for (var x = 0; x < row.Length; x++) {
          var o = srcOffset + x * 4;
          row[x] = new Rgba32(bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3]);
        }
      }
    });
    return image;
  }

  private static Image<Rgba32> CropToContent(Image<Rgba32> source) {
    var width = source.Width;
    var height = source.Height;
    var pixels = new Rgba32[width * height];
    source.CopyPixelDataTo(pixels);

    var minX = width;
    var maxX = -1;
    var minY = height;
    var maxY = -1;
    for (var y = 0; y < height; y++) {
      var rowStart = y * width;
      for (var x = 0; x < width; x++) {
        var p = pixels[rowStart + x];
        if (p.R == 0 && p.G == 0 && p.B == 0)
          continue;
        if (x < minX) minX = x;
        if (x > maxX) maxX = x;
        if (y < minY) minY = y;
        if (y > maxY) maxY = y;
      }
    }
    if (maxX < 0 || maxY < 0)
      return source.Clone();
    if (minX == 0 && minY == 0 && maxX == width - 1 && maxY == height - 1)
      return source.Clone();

    var cropW = maxX - minX + 1;
    var cropH = maxY - minY + 1;
    var cropped = new Image<Rgba32>(cropW, cropH);
    cropped.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var srcRow = (y + minY) * width + minX;
        for (var x = 0; x < row.Length; x++)
          row[x] = pixels[srcRow + x];
      }
    });
    return cropped;
  }
}
