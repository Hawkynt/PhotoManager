using System.Runtime.InteropServices;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Panorama;

/// <summary>
/// Tunables for <see cref="OpenCvPanoramaStitcher"/>. <see cref="AutoCropToContent"/>
/// trims the transparent border that OpenCV leaves on the warped output.
/// <para><see cref="Masks"/>, when non-null, drives smart-patch sharpness gating:
/// 255 = include, 0 = exclude. Mask count must equal image count; per-mask
/// dimensions must match the corresponding image. OpenCvSharp4 4.10 does not
/// expose <c>Stitcher.Stitch(images, masks, ...)</c> — only the unmasked
/// overloads are bound — so we apply masks by zeroing out excluded pixels in
/// the BGR Mat handed to the stitcher. That deprives the feature detector of
/// signal in blurry/obstructed regions, which is the same effect a true
/// inclusion mask would produce for matching and seam selection.</para>
/// </summary>
public sealed record OpenCvStitchOptions {
  public Stitcher.Mode Mode { get; init; } = Stitcher.Mode.Panorama;
  public double Confidence { get; init; } = 1.0;
  public bool AutoCropToContent { get; init; } = true;
  public IReadOnlyList<Image<L8>>? Masks { get; init; }
}

/// <summary>
/// Wraps OpenCvSharp4's high-level <see cref="Stitcher"/> for hand-held
/// panoramas (full feature detection, homography, multi-band blending).
/// We avoid the <c>OpenCvSharp4.Extensions</c> Bitmap dep entirely so the
/// library stays portable to non-Windows runners — pixel buffers are copied
/// straight from <see cref="Image{TPixel}"/> to <see cref="Mat"/> and back.
/// </summary>
public static class OpenCvPanoramaStitcher {
  /// <summary>
  /// Returns a stitched mosaic, or <c>null</c> if the underlying OpenCV
  /// stitcher couldn't find enough matches to assemble the inputs (e.g.
  /// insufficient overlap, featureless scene). Throws
  /// <see cref="DllNotFoundException"/> when the OpenCV native binary isn't
  /// present on the current platform — callers should surface that as a
  /// "OpenCV runtime not installed" message rather than crashing.
  /// </summary>
  public static Image<Rgba32>? Stitch(IReadOnlyList<Image<Rgba32>> images, OpenCvStitchOptions? options = null) {
    ArgumentNullException.ThrowIfNull(images);
    if (images.Count < 2)
      throw new ArgumentException("Need at least two frames for OpenCV stitching.", nameof(images));

    options ??= new OpenCvStitchOptions();
    if (options.Masks is { } masks) {
      if (masks.Count != images.Count)
        throw new ArgumentException("Mask count must match image count.", nameof(options));
      for (var i = 0; i < masks.Count; i++) {
        if (masks[i].Width != images[i].Width || masks[i].Height != images[i].Height)
          throw new ArgumentException($"Mask {i} dimensions {masks[i].Width}x{masks[i].Height} don't match image {images[i].Width}x{images[i].Height}.", nameof(options));
      }
    }

    var mats = new List<Mat>(images.Count);
    Mat? result = null;
    try {
      for (var i = 0; i < images.Count; i++)
        mats.Add(ToBgrMat(images[i], options.Masks?[i]));

      Stitcher stitcher;
      try {
        stitcher = Stitcher.Create(options.Mode);
      } catch (DllNotFoundException ex) {
        throw new DllNotFoundException(
          "OpenCV native library is not available on this platform. Install the matching OpenCvSharp4.runtime.* NuGet package.",
          ex);
      } catch (TypeInitializationException ex) when (ex.InnerException is DllNotFoundException dnf) {
        throw new DllNotFoundException(
          "OpenCV native library is not available on this platform. Install the matching OpenCvSharp4.runtime.* NuGet package.",
          dnf);
      }

      stitcher.PanoConfidenceThresh = options.Confidence;

      result = new Mat();
      var status = stitcher.Stitch(mats, result);
      if (status != Stitcher.Status.OK)
        return null;

      using var rgba = new Mat();
      Cv2.CvtColor(result, rgba, ColorConversionCodes.BGR2RGBA);

      var image = ToImageSharp(rgba);
      return options.AutoCropToContent ? CropToContent(image) : image;
    } finally {
      foreach (var m in mats)
        m.Dispose();
      result?.Dispose();
    }
  }

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

  /// <summary>
  /// Trim the transparent border around a stitched panorama. OpenCV's warp
  /// leaves zero-pixel margins where projection maps out of the image; the
  /// crop finds the tightest axis-aligned rect where alpha &gt; 0 anywhere.
  /// </summary>
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
        if (p.A == 0 && p.R == 0 && p.G == 0 && p.B == 0)
          continue;
        if (x < minX) minX = x;
        if (x > maxX) maxX = x;
        if (y < minY) minY = y;
        if (y > maxY) maxY = y;
      }
    }
    if (maxX < 0 || maxY < 0)
      return source;
    if (minX == 0 && minY == 0 && maxX == width - 1 && maxY == height - 1)
      return source;

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
    source.Dispose();
    return cropped;
  }
}
