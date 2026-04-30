using PhotoManager.Core.Detection;
using PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Develop;

/// <summary>
/// Orchestrates the multi-stage restoration of an old / damaged photo.
/// Stages, in order: auto-tone → denoise → colorize → face-restore →
/// upscale. Each stage is gated by a strength in
/// <see cref="RestorationSettings"/>; zero-strength stages are no-ops.
///
/// The pipeline is preview-friendly: every stage threads the supplied
/// <see cref="CancellationToken"/> down to the underlying ONNX engines
/// so live re-renders cancel within ~1 tile of latency. A missing model
/// makes the corresponding stage a graceful no-op rather than an error
/// — the restoration window's status bar is responsible for surfacing
/// "AI denoise model not installed" prompts before invoking the pipeline.
/// </summary>
public static class RestorationPipeline {
  /// <summary>
  /// Run all configured restoration stages on <paramref name="source"/>
  /// and return a freshly allocated restored image. The input is not
  /// mutated. Identity settings return a clone for interface parity.
  /// </summary>
  /// <param name="source">Full-resolution source image. Not mutated.</param>
  /// <param name="faces">Face bounding boxes detected on the source image
  ///   (normalised 0..1, top-left origin). Empty list = skip face
  ///   restoration even if the strength slider is non-zero.</param>
  /// <param name="settings">Strengths + presets from the restoration window.</param>
  /// <param name="ct">Cancels between stages and inside ONNX tile loops.</param>
  public static Image<Rgba32> Apply(
    Image<Rgba32> source,
    IReadOnlyList<NormalizedBoundingBox> faces,
    RestorationSettings settings,
    CancellationToken ct = default
  ) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(faces);
    ArgumentNullException.ThrowIfNull(settings);

    var output = source.Clone();
    if (settings.IsIdentity)
      return output;

    // 1) Auto-tone: stretches the source's tone curve before any AI sees
    //    it, so models trained on well-exposed inputs produce cleaner
    //    output. Cheap; histogram pass plus the standard pixel-adjustment
    //    routine the develop window uses.
    if (settings.AutoTone) {
      ct.ThrowIfCancellationRequested();
      var histogram = HistogramAnalyzer.Compute(output);
      var auto = AutoDeveloper.AutoTone(new DevelopSettings(), histogram);
      ImageDeveloper.ApplyPixelAdjustments(output, auto);
    }

    // 2) Denoise / despeckle: NAFNet (or whatever model is set in
    //    settings.DenoiseModel) blends source ↔ denoised by strength.
    if (settings.DenoiseStrength > 1e-6) {
      ct.ThrowIfCancellationRequested();
      var modelFile = !string.IsNullOrWhiteSpace(settings.DenoiseModel)
        ? AppDataPaths.ModelFile(settings.DenoiseModel!)
        : null;
      using var denoiser = new OnnxDenoiser(modelFile);
      if (denoiser.IsAvailable) {
        using var denoised = denoiser.Denoise(output, settings.DenoiseStrength, ct);
        if (denoised != null)
          ReplaceContents(output, denoised);
      }
    }

    // 3) Recolourize: DeOldify maps grayscale luminance to an RGB chroma
    //    field which is blended onto the source's full-resolution
    //    luminance. Only a meaningful operation on B&W or near-B&W
    //    sources; on a colour source it'll add a subtle cast.
    if (settings.RecolourStrength > 1e-6) {
      ct.ThrowIfCancellationRequested();
      var modelFile = !string.IsNullOrWhiteSpace(settings.ColorizeModel)
        ? AppDataPaths.ModelFile(settings.ColorizeModel!)
        : null;
      using var colorizer = new OnnxColorizer(modelFile);
      if (colorizer.IsAvailable) {
        var colorised = colorizer.Colorize(output, settings.RecolourStrength, ct);
        if (colorised != null) {
          using (colorised)
            ReplaceContents(output, colorised);
        }
      }
    }

    // 4) Face restoration: GFPGAN runs on each detected face crop and
    //    feathers the restored result back into the image. Face boxes
    //    are normalised to the original source's dimensions, so the box
    //    coordinates still apply to the (possibly tone-mapped /
    //    colourised) intermediate `output` because we haven't resized.
    if (settings.FaceRestoreStrength > 1e-6 && faces.Count > 0) {
      ct.ThrowIfCancellationRequested();
      ApplyFaceRestoration(output, faces, settings.FaceRestoreStrength, ct);
    }

    // 5) Upscale: chained 4× passes via the existing OnnxUpscaler.
    if (settings.UpscaleFactor > 1) {
      ct.ThrowIfCancellationRequested();
      var modelFile = !string.IsNullOrWhiteSpace(settings.UpscaleModel)
        ? AppDataPaths.ModelFile(settings.UpscaleModel!)
        : null;
      using var upscaler = new OnnxUpscaler(modelFile);
      if (upscaler.IsAvailable) {
        var upscaled = upscaler.Upscale(output, settings.UpscaleFactor, ct);
        if (upscaled != null && !ReferenceEquals(upscaled, output)) {
          output.Dispose();
          output = upscaled;
        }
      }
    }

    return output;
  }

  /// <summary>
  /// Crop each detected face out of <paramref name="image"/>, run GFPGAN,
  /// resize the restored 512×512 back to the box's pixel size, and
  /// alpha-blend it in with a soft elliptical mask so the rectangle's
  /// edge doesn't show. <paramref name="strength"/> linearly mixes the
  /// restored crop with the original.
  /// </summary>
  private static void ApplyFaceRestoration(
    Image<Rgba32> image,
    IReadOnlyList<NormalizedBoundingBox> faces,
    double strength,
    CancellationToken ct
  ) {
    using var restorer = new OnnxFaceRestorer();
    if (!restorer.IsAvailable)
      return;

    var blend = (float)Math.Clamp(strength, 0.0, 1.0);
    var imageW = image.Width;
    var imageH = image.Height;

    foreach (var face in faces) {
      ct.ThrowIfCancellationRequested();

      // Convert normalised 0..1 box → pixel rect, expanded slightly so
      // GFPGAN sees a bit of context around the face (forehead, jawline)
      // and the feathered seam falls outside the actual features.
      var pad = 0.10f;
      var x = (int)Math.Round(Math.Max(0, (face.X - face.Width  * pad)) * imageW);
      var y = (int)Math.Round(Math.Max(0, (face.Y - face.Height * pad)) * imageH);
      var w = (int)Math.Round(Math.Min(1.0, face.Width  * (1 + 2 * pad)) * imageW);
      var h = (int)Math.Round(Math.Min(1.0, face.Height * (1 + 2 * pad)) * imageH);
      x = Math.Clamp(x, 0, imageW - 1);
      y = Math.Clamp(y, 0, imageH - 1);
      w = Math.Clamp(w, 1, imageW - x);
      h = Math.Clamp(h, 1, imageH - y);
      if (w < 16 || h < 16)
        continue;  // too small for GFPGAN to be useful

      using var crop = image.Clone(c => c.Crop(new Rectangle(x, y, w, h)));
      using var restored512 = restorer.Restore(crop, ct);
      if (restored512 is null)
        continue;
      using var restoredCrop = restored512.Clone(c => c.Resize(w, h));

      BlendFace(image, restoredCrop, x, y, w, h, blend);
    }
  }

  /// <summary>
  /// Alpha-blend <paramref name="restored"/> into <paramref name="target"/>
  /// at (<paramref name="dstX"/>, <paramref name="dstY"/>) using a smooth
  /// elliptical falloff: full strength in the centre, fading to zero at
  /// the corners of the bounding box. Hides the rectangular seam.
  /// </summary>
  private static void BlendFace(Image<Rgba32> target, Image<Rgba32> restored,
                                int dstX, int dstY, int w, int h, float strength) {
    var halfW = w * 0.5f;
    var halfH = h * 0.5f;
    var restoredPixels = new Rgba32[w * h];
    restored.CopyPixelDataTo(restoredPixels);

    target.ProcessPixelRows(accessor => {
      for (var ly = 0; ly < h; ly++) {
        var ty = dstY + ly;
        if (ty < 0 || ty >= accessor.Height)
          continue;
        var row = accessor.GetRowSpan(ty);
        for (var lx = 0; lx < w; lx++) {
          var tx = dstX + lx;
          if (tx < 0 || tx >= row.Length)
            continue;

          // Elliptical Gaussian-ish falloff: 1 at centre, ~0 at the box
          // corners. Squared distance from centre, normalised to the
          // box's half-extents so the mask stays smooth on non-square
          // boxes.
          var dx = (lx - halfW) / halfW;
          var dy = (ly - halfH) / halfH;
          var r2 = dx * dx + dy * dy;
          var maskWeight = (float)Math.Max(0.0, 1.0 - r2);
          maskWeight *= maskWeight;  // smoother shoulder
          var w_ = strength * maskWeight;
          if (w_ <= 0)
            continue;

          var px = row[tx];
          var rest = restoredPixels[ly * w + lx];
          row[tx] = new Rgba32(
            (byte)Math.Round(px.R * (1 - w_) + rest.R * w_),
            (byte)Math.Round(px.G * (1 - w_) + rest.G * w_),
            (byte)Math.Round(px.B * (1 - w_) + rest.B * w_),
            px.A
          );
        }
      }
    });
  }

  /// <summary>
  /// Copy pixels from <paramref name="src"/> over <paramref name="dst"/>
  /// in place. Both images must share dimensions. Used to swap stage
  /// outputs back into the long-lived <c>output</c> reference without
  /// losing the caller's pointer.
  /// </summary>
  private static void ReplaceContents(Image<Rgba32> dst, Image<Rgba32> src) {
    if (dst.Width != src.Width || dst.Height != src.Height)
      return;
    var pixels = new Rgba32[src.Width * src.Height];
    src.CopyPixelDataTo(pixels);
    dst.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var srcOff = y * dst.Width;
        for (var x = 0; x < row.Length; x++)
          row[x] = pixels[srcOff + x];
      }
    });
  }
}
