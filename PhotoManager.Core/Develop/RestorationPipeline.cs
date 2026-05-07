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
  /// <param name="brushInpaintMask">Optional persistent brush-mask the
  ///   user painted onto the working image (in source pixel space, R≥128
  ///   = inpaint here). Null / empty mask skips the manual-inpaint stage.
  ///   The mask is treated as declarative state — every preview / Save
  ///   re-runs LaMa over the un-modified source so order-independence is
  ///   preserved.</param>
  /// <param name="cache">Optional per-stage output cache (typically held
  ///   for the lifetime of the restore window). Each stage stores its
  ///   output keyed by a CUMULATIVE settings hash — once a settings
  ///   change invalidates one stage's key, every downstream stage's
  ///   cache entry is also invalidated because their cumulative key
  ///   shares the now-changed prefix. So the user changing the
  ///   recolour slider re-runs only recolour + faces + upscale; the
  ///   denoise / artifact / brush-inpaint / auto-scratch / despeckle
  ///   results from the previous render are reused. <see cref="RestorationPipelineCache.BindToSource"/>
  ///   should be called by the caller before Apply so a source change
  ///   discards the now-stale cache.</param>
  public static Image<Rgba32> Apply(
    Image<Rgba32> source,
    IReadOnlyList<NormalizedBoundingBox> faces,
    RestorationSettings settings,
    CancellationToken ct = default,
    IProgress<StageProgress>? progress = null,
    Image<Rgba32>? brushInpaintMask = null,
    RestorationPipelineCache? cache = null
  ) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(faces);
    ArgumentNullException.ThrowIfNull(settings);

    var hasBrushMask = brushInpaintMask != null && MaskHasContent(brushInpaintMask);

    var output = source.Clone();
    if (settings.IsIdentity && !hasBrushMask && settings.DespeckleStrength <= 1e-6)
      return output;

    // Cumulative cache key — built incrementally as we advance through
    // stages. Each stage appends its own settings to this string before
    // checking the cache. A change at stage N changes the keys of
    // stage N AND every downstream stage (since they share the
    // now-changed prefix), causing exactly the stages that need to
    // re-run to miss the cache.
    var cumulativeKey = new System.Text.StringBuilder("v1");

    var pixelCount = output.Width * output.Height;

    // Pre-compute the list of stages that will run + their conservative
    // duration estimates. The pipeline tracks the running stage so each
    // emitted StageProgress can carry the sum of future stages' ETAs —
    // the UI then shows pipeline-total remaining = within-stage remaining
    // + future-stages estimate, instead of just within-stage.
    var plan = new List<(string name, double seconds)>();
    if (settings.AutoTone) plan.Add(("auto-tone", EstimateSeconds("auto-tone", pixelCount, faces.Count)));
    if (settings.DenoiseStrength > 1e-6) plan.Add(("denoise", EstimateSeconds("denoise", pixelCount, faces.Count)));
    if (settings.ArtifactRemoveStrength > 1e-6) plan.Add(("artifact", EstimateSeconds("artifact", pixelCount, faces.Count)));
    if (hasBrushMask) plan.Add(("brush-inpaint", EstimateSeconds("inpaint", pixelCount, faces.Count)));
    if (settings.AutoScratchRemoval) plan.Add(("auto-scratch", EstimateSeconds("auto-scratch", pixelCount, faces.Count) * Math.Min(3, settings.AutoScratchMaxIterations)));
    if (settings.DespeckleStrength > 1e-6) plan.Add(("despeckle", EstimateSeconds("despeckle", pixelCount, faces.Count)));
    if (settings.RecolourStrength > 1e-6) plan.Add(("recolour", EstimateSeconds("recolour", pixelCount, faces.Count)));
    if (settings.FaceRestoreStrength > 1e-6 && faces.Count > 0) plan.Add(("faces", EstimateSeconds("faces", pixelCount, faces.Count)));
    if (settings.UpscaleFactor > 1) {
      var upscalePasses = settings.UpscaleFactor switch { 2 => 1, 4 => 1, 16 => 2, _ => 3 };
      plan.Add(("upscale", EstimateSeconds("upscale", pixelCount, faces.Count) * upscalePasses));
    }
    var planIndex = 0;
    double FutureSeconds() {
      double s = 0;
      for (var i = planIndex + 1; i < plan.Count; i++) s += plan[i].seconds;
      return s;
    }
    void Report(StageProgress p) =>
      progress?.Report(p with { EstimatedRemainingPipelineSeconds = FutureSeconds() });
    var stageProgress = progress is null ? null : new ProgressDecorator(p => Report(p));

    // Helper: extend cumulative key with this stage's settings and try
    // a cache hit. Returns true if the cache supplied the output (in
    // which case `output` is updated in place from the cached image)
    // and the stage body should be skipped. Returns false on cache
    // miss — the stage runs normally and the caller stores the result
    // via StoreCache.
    bool TryCacheHit(string stageName, string stageSettings) {
      cumulativeKey.Append('|').Append(stageName).Append('=').Append(stageSettings);
      if (cache?.GetIfFresh(stageName, cumulativeKey.ToString()) is not { } cached)
        return false;
      if (cached.Width == output.Width && cached.Height == output.Height) {
        ReplaceContents(output, cached);
      } else {
        // Dimension changed (upscale stage). Replace the whole image.
        output.Dispose();
        output = cached.Clone();
      }
      return true;
    }
    void StoreCache(string stageName) =>
      cache?.Set(stageName, cumulativeKey.ToString(), output);

    // 1) Auto-tone.
    if (settings.AutoTone) {
      ct.ThrowIfCancellationRequested();
      if (!TryCacheHit("auto-tone", $"on")) {
        Report(new StageProgress("auto-tone", 0, 1, plan[planIndex].seconds));
        var histogram = HistogramAnalyzer.Compute(output);
        var auto = AutoDeveloper.AutoTone(new DevelopSettings(), histogram);
        ImageDeveloper.ApplyPixelAdjustments(output, auto);
        Report(new StageProgress("auto-tone", 1, 1));
        StoreCache("auto-tone");
      }
      planIndex++;
    } else {
      cumulativeKey.Append("|auto-tone=off");
    }

    // 2) Denoise — tile-based.
    if (settings.DenoiseStrength > 1e-6) {
      ct.ThrowIfCancellationRequested();
      if (!TryCacheHit("denoise", $"{settings.DenoiseStrength:F4}|{settings.DenoiseModel ?? "default"}")) {
        var modelFile = !string.IsNullOrWhiteSpace(settings.DenoiseModel)
          ? AppDataPaths.ModelFile(settings.DenoiseModel!)
          : null;
        using var denoiser = new OnnxDenoiser(modelFile);
        if (denoiser.IsAvailable) {
          using var denoised = denoiser.Denoise(output, settings.DenoiseStrength, ct, stageProgress);
          if (denoised != null)
            ReplaceContents(output, denoised);
        }
        StoreCache("denoise");
      }
      planIndex++;
    } else {
      cumulativeKey.Append("|denoise=off");
    }

    // 2.5) FBCNN artifact removal — single-shot.
    if (settings.ArtifactRemoveStrength > 1e-6) {
      ct.ThrowIfCancellationRequested();
      if (!TryCacheHit("artifact", $"{settings.ArtifactRemoveStrength:F4}|{settings.ArtifactRemoveModel ?? "default"}")) {
        Report(new StageProgress("artifact", 0, 1, plan[planIndex].seconds));
        var modelFile = !string.IsNullOrWhiteSpace(settings.ArtifactRemoveModel)
          ? AppDataPaths.ModelFile(settings.ArtifactRemoveModel!)
          : null;
        using var remover = new OnnxArtifactRemover(modelFile);
        if (remover.IsAvailable) {
          using var cleaned = remover.Remove(output, settings.ArtifactRemoveStrength, ct);
          if (cleaned != null)
            ReplaceContents(output, cleaned);
        }
        Report(new StageProgress("artifact", 1, 1));
        StoreCache("artifact");
      }
      planIndex++;
    } else {
      cumulativeKey.Append("|artifact=off");
    }

    // 2.6) Manual brush-inpaint — runs the user's persistent paint mask
    //       through LaMa. Sits before auto-scratch so the user's
    //       hand-marked damage is dealt with on the un-modified
    //       luminance source, just like auto-detected scratches. The
    //       mask is interpreted in the source's native pixel space —
    //       callers pass either a preview-resolution mask (for live
    //       preview) or an upscaled-to-full-res mask (for Save As).
    if (hasBrushMask) {
      ct.ThrowIfCancellationRequested();
      // Mask hash by content count so painting strokes invalidates
      // the cache. Cheap counting hash — the user's brush is the only
      // thing that mutates the mask within one session.
      var maskHash = QuickMaskHash(brushInpaintMask!);
      if (!TryCacheHit("brush-inpaint", $"{maskHash:X}")) {
        Report(new StageProgress("brush-inpaint", 0, 1, plan[planIndex].seconds));
        var maskForOutput = brushInpaintMask!.Width == output.Width && brushInpaintMask.Height == output.Height
          ? brushInpaintMask
          : brushInpaintMask.Clone(c => c.Resize(output.Width, output.Height));
        try {
          using var inpainter = new OnnxInpainter();
          if (inpainter.IsAvailable) {
            var inpainted = inpainter.Inpaint(output, maskForOutput, ct);
            if (inpainted != null) {
              using (inpainted)
                ReplaceContents(output, inpainted);
            }
          }
        } finally {
          if (!ReferenceEquals(maskForOutput, brushInpaintMask))
            maskForOutput.Dispose();
        }
        Report(new StageProgress("brush-inpaint", 1, 1));
        StoreCache("brush-inpaint");
      }
      planIndex++;
    } else {
      cumulativeKey.Append("|brush-inpaint=off");
    }

    // 2.75) Auto-scratch removal — detect+inpaint loop. Critical that
    //       this runs on the *un-colorised* working source: scratches
    //       are luminance-domain damage that the detector locks onto on
    //       the original much more reliably than on a recoloured /
    //       upscaled image. So this stage sits ahead of recolour /
    //       face restore / upscale in the pipeline order.
    if (settings.AutoScratchRemoval) {
      ct.ThrowIfCancellationRequested();
      if (!TryCacheHit("auto-scratch", $"{settings.AutoScratchMaxIterations}|{settings.AutoScratchThresholdPct:F4}|{settings.AutoScratchDetectorThreshold:F4}")) {
        var cleaned = AutoScratchPipeline.Apply(output, settings.AutoScratchMaxIterations, settings.AutoScratchThresholdPct, ct, stageProgress, settings.AutoScratchDetectorThreshold);
        if (cleaned != null) {
          if (cleaned.Width == output.Width && cleaned.Height == output.Height)
            ReplaceContents(output, cleaned);
          cleaned.Dispose();
        }
        StoreCache("auto-scratch");
      }
      planIndex++;
    } else {
      cumulativeKey.Append("|auto-scratch=off");
    }

    // 2.85) Salt-and-pepper despeckle — pure-C# adaptive median filter.
    //       Runs after auto-scratch (so dust speckles aren't fed into
    //       the detector as residual noise) but before recolour (so
    //       isolated white/black speckles don't get amplified into
    //       coloured artefacts by the colorizer). DespeckleStrength
    //       gates an alpha-blend between source and filtered output:
    //       1.0 = full filter, 0.5 = 50% blend, 0.0 = stage skipped.
    if (settings.DespeckleStrength > 1e-6) {
      ct.ThrowIfCancellationRequested();
      if (!TryCacheHit("despeckle", $"{settings.DespeckleStrength:F4}")) {
        Report(new StageProgress("despeckle", 0, 1, plan[planIndex].seconds));
        var filtered = SaltAndPepperFilter.Filter(output);
        if (settings.DespeckleStrength >= 0.999) {
          using (filtered)
            ReplaceContents(output, filtered);
        } else {
          using (filtered)
            BlendContents(output, filtered, (float)settings.DespeckleStrength);
        }
        Report(new StageProgress("despeckle", 1, 1));
        StoreCache("despeckle");
      }
      planIndex++;
    } else {
      cumulativeKey.Append("|despeckle=off");
    }

    // 3) Recolour — single-shot.
    if (settings.RecolourStrength > 1e-6) {
      ct.ThrowIfCancellationRequested();
      if (!TryCacheHit("recolour", $"{settings.RecolourStrength:F4}|{settings.ColorizeModel ?? "default"}|{settings.ChromaBoost:F4}")) {
        Report(new StageProgress("recolour", 0, 1, plan[planIndex].seconds));
        var colorised = ColorizerRouter.Colorize(output, settings.ColorizeModel, settings.RecolourStrength, settings.ChromaBoost, ct);
        if (colorised != null) {
          using (colorised)
            ReplaceContents(output, colorised);
        }
        Report(new StageProgress("recolour", 1, 1));
        StoreCache("recolour");
      }
      planIndex++;
    } else {
      cumulativeKey.Append("|recolour=off");
    }

    // 4) Face restore — per-face ticks.
    if (settings.FaceRestoreStrength > 1e-6 && faces.Count > 0) {
      ct.ThrowIfCancellationRequested();
      if (!TryCacheHit("faces", $"{settings.FaceRestoreStrength:F4}|{faces.Count}")) {
        Report(new StageProgress("faces", 0, faces.Count, plan[planIndex].seconds));
        ApplyFaceRestoration(output, faces, settings.FaceRestoreStrength, ct, stageProgress);
        StoreCache("faces");
      }
      planIndex++;
    } else {
      cumulativeKey.Append("|faces=off");
    }

    // 5) Upscale — tile-based, multi-pass. Note: this stage CHANGES
    //    image dimensions, so cache hits replace the whole image
    //    rather than blitting in place.
    if (settings.UpscaleFactor > 1) {
      ct.ThrowIfCancellationRequested();
      if (!TryCacheHit("upscale", $"{settings.UpscaleFactor}|{settings.UpscaleModel ?? "default"}")) {
        var modelFile = !string.IsNullOrWhiteSpace(settings.UpscaleModel)
          ? AppDataPaths.ModelFile(settings.UpscaleModel!)
          : null;
        using var upscaler = new OnnxUpscaler(modelFile);
        if (upscaler.IsAvailable) {
          var upscaled = upscaler.Upscale(output, settings.UpscaleFactor, ct, stageProgress);
          if (upscaled != null && !ReferenceEquals(upscaled, output)) {
            output.Dispose();
            output = upscaled;
          }
        }
        StoreCache("upscale");
      }
      planIndex++;
    } else {
      cumulativeKey.Append("|upscale=off");
    }

    return output;
  }

  /// <summary>Quick non-cryptographic hash of a mask's content so the
  /// brush-inpaint cache key changes when the user paints additional
  /// strokes. We sample every 32nd pixel; full-coverage hash isn't
  /// needed because consecutive strokes change at least a few sample
  /// points and the worst false-cache-hit case is "user paints a
  /// stroke that misses every sample" (extremely rare for a 20-px
  /// brush).</summary>
  private static int QuickMaskHash(Image<Rgba32> mask) {
    var hash = 17;
    mask.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y += 32) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x += 32) {
          hash = unchecked(hash * 31 + row[x].R);
        }
      }
    });
    return hash;
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
    CancellationToken ct,
    IProgress<StageProgress>? progress = null
  ) {
    using var restorer = new OnnxFaceRestorer();
    if (!restorer.IsAvailable)
      return;

    var blend = (float)Math.Clamp(strength, 0.0, 1.0);
    var imageW = image.Width;
    var imageH = image.Height;

    var done = 0;
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
      done++;
      progress?.Report(new StageProgress("faces", done, faces.Count));
    }
  }

  /// <summary>Lightweight IProgress&lt;StageProgress&gt; wrapper that hands
  /// every event off to a caller-supplied lambda. Used by Apply() to
  /// decorate inner-stage events (denoise tile updates, upscale tile
  /// updates, per-face restoration ticks) with the future-pipeline ETA
  /// before forwarding to the UI.</summary>
  private sealed class ProgressDecorator(Action<StageProgress> handler) : IProgress<StageProgress> {
    public void Report(StageProgress value) => handler(value);
  }

  /// <summary>
  /// Conservative time estimate (in seconds) for a stage that doesn't
  /// emit per-tile progress, so the UI can show a starting ETA before
  /// the first measurement comes in. Numbers are intentionally on the
  /// pessimistic side — better to overestimate and have the actual
  /// time come in shorter than the reverse. Throughputs assume modest
  /// hardware (Intel iGPU / mid-range NPU); on CPU EP times are
  /// roughly 4–8× higher; on a discrete GPU 2–4× lower.
  /// </summary>
  private static double EstimateSeconds(string stageName, long pixelCount, int faceCount) {
    var megapixels = Math.Max(0.1, pixelCount / 1_000_000.0);
    return stageName switch {
      "auto-tone" => 0.05 + megapixels * 0.02,    // ~50 MPix/s histogram + adjust
      "denoise"   => 0.5  + megapixels * 0.6,     // ~1.5 MPix/s NAFNet on NPU/GPU
      "artifact"  => 0.5  + megapixels * 0.7,     // FBCNN, similar to denoise
      "auto-scratch" => 1.5 + megapixels * 1.2,   // BOPB + LaMa per iteration
      "despeckle" => 0.2 + megapixels * 0.4,      // pure-C# adaptive median, all cores
      "recolour"  => 1.0,                          // fixed 256/512 input, near-constant
      "faces"     => 0.5  * Math.Max(1, faceCount), // ~500 ms / face on accelerator
      "inpaint"   => 1.5,                          // LaMa, fixed 512×512
      "upscale"   => 2.0  + megapixels * 1.5,     // ~0.7 MPix/s ESRGAN at 4×
      _ => 1.0
    };
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

  /// <summary>Returns true when <paramref name="mask"/> has at least one
  /// pixel with R≥128 (the convention every brush / detector uses for
  /// "inpaint here"). An empty mask should skip the manual-inpaint stage
  /// entirely so identity settings still return a fast clone.</summary>
  private static bool MaskHasContent(Image<Rgba32> mask) {
    var found = false;
    mask.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height && !found; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          if (row[x].R >= 128) { found = true; break; }
      }
    });
    return found;
  }

  /// <summary>Linear-blend <paramref name="src"/> into <paramref name="dst"/>
  /// in place: dst = lerp(dst, src, weight). Both images must share
  /// dimensions. Used by the despeckle stage when DespeckleStrength is
  /// fractional so the user gets a partial filter instead of all-or-nothing.</summary>
  private static void BlendContents(Image<Rgba32> dst, Image<Rgba32> src, float weight) {
    if (dst.Width != src.Width || dst.Height != src.Height)
      return;
    weight = Math.Clamp(weight, 0f, 1f);
    if (weight <= 0)
      return;
    var pixels = new Rgba32[src.Width * src.Height];
    src.CopyPixelDataTo(pixels);
    var inv = 1f - weight;
    dst.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var srcOff = y * dst.Width;
        for (var x = 0; x < row.Length; x++) {
          var d = row[x];
          var s = pixels[srcOff + x];
          row[x] = new Rgba32(
            (byte)Math.Round(d.R * inv + s.R * weight),
            (byte)Math.Round(d.G * inv + s.G * weight),
            (byte)Math.Round(d.B * inv + s.B * weight),
            d.A);
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
