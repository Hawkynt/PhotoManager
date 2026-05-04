using PhotoManager.Core;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Develop;

/// <summary>
/// Applies <see cref="DevelopSettings"/> to an image, producing a new image.
/// Adjustments are done in linear-ish sRGB space (ImageSharp's native Rgba32)
/// — good enough for a casual developer; a true RAW workflow would work in
/// linear scene-referred data, but our "simple developer" ships good-looking
/// JPEGs and not scientifically accurate ones.
/// </summary>
public static class ImageDeveloper {
  /// <summary>
  /// Returns a new image with <paramref name="settings"/> applied. The input
  /// is not mutated. Identity settings return a clone for interface parity.
  /// </summary>
  /// <param name="previewMode">When true, expensive AI stages (denoise, upscale)
  /// are skipped so live-preview slider drags stay responsive on the UI thread.
  /// Final renders (Save As) leave this false so AI stages run.</param>
  /// <param name="ct">Cooperatively cancels between AI tiles. Useful when a
  /// background AI render is superseded by a newer settings change.</param>
  public static Image<Rgba32> Apply(Image<Rgba32> source, DevelopSettings settings, bool previewMode = false, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(settings);

    var output = source.Clone();

    if (settings.RotationDegrees != 0)
      output.Mutate(c => c.Rotate(NormalizeRotation(settings.RotationDegrees)));

    if (Math.Abs(settings.CropAngleDegrees) > 1e-6)
      output.Mutate(c => c.Rotate((float)settings.CropAngleDegrees));

    // Lens corrections (manual radial distortion + per-channel CA) run
    // before the develop sliders so colour adjustments operate on a
    // geometrically-corrected image. Skipped when all three knobs are zero.
    ApplyLensCorrections(output, settings);

    // Perspective / Upright projective warp (keystone, scale, aspect,
    // translation, in-plane rotate). Bilinear-sampled coord-map pass,
    // skipped when every parameter is at its identity value.
    ApplyPerspectiveTransform(output, settings);

    if (!settings.IsIdentity)
      ApplyPixelAdjustments(output, settings, previewMode, ct);
    else if (!previewMode && settings.AiDenoiseStrength > 1e-6)
      // Denoise belongs inside ApplyPixelAdjustments (canonical denoise→sharpen
      // order). When the only active edit is AI denoise, we still need that
      // path so the stage runs. Skipped in preview mode so the UI doesn't
      // freeze on slider drags — the live preview window runs the AI stage
      // off-thread instead and swaps the result in when ready.
      ApplyAiDenoiseStage(output, settings, ct);

    // AI colorize: turns a B&W source into a colour image. Runs AFTER
    // tone/curves (so the model sees the developed luminance) but BEFORE
    // creative LUTs (which operate on the colourised pixels). Skipped in
    // preview mode so slider drags stay responsive — the live-preview
    // window runs the AI stage off-thread instead.
    if (!previewMode && settings.AiColorizeAmount > 1e-6) {
      var colorised = TryAiColorize(output, settings, ct);
      if (colorised != null && !ReferenceEquals(colorised, output)) {
        output.Dispose();
        output = colorised;
      }
    }

    // Creative-look LUT — runs AFTER tone / curves so the user-shaped
    // distribution is what gets remapped, BEFORE local masks so per-mask
    // tweaks operate on the post-look image.
    ApplyCreativeLook(output, settings);

    // AI upscale runs AFTER tone/curves/HSL/sharpening but BEFORE local
    // adjustments + crop so the user has more pixels to work with when
    // placing masks and trimming the frame. No-op when factor <= 1 or
    // the model isn't installed; in preview mode the live-preview window
    // runs this off-thread instead.
    if (!previewMode && settings.AiUpscaleFactor > 1) {
      var upscaled = TryAiUpscale(output, settings, ct);
      if (upscaled != null && !ReferenceEquals(upscaled, output)) {
        output.Dispose();
        output = upscaled;
      }
    }

    // Local adjustments come AFTER the global pixel pass so they sit on
    // top of the developed image. Each adjustment has its own mask + a
    // basic-panel slider set; mask weight scales how much of the local
    // correction blends into the output.
    if (settings.LocalAdjustments is { Count: > 0 } locals)
      foreach (var local in locals)
        if (!local.IsZero)
          ApplyLocalAdjustment(output, local);

    ApplyWatermark(output, settings);

    ApplyCropRectangle(output, settings);

    return output;
  }

  /// AI denoise stage. Lazily opens the ONNX session via OnnxDenoiser; when
  /// no model is installed the call is a graceful no-op. Otherwise the
  /// source image is replaced with the denoised result, blended by
  /// DevelopSettings.AiDenoiseStrength.
  private static void ApplyAiDenoiseStage(Image<Rgba32> image, DevelopSettings settings, CancellationToken ct = default) {
    if (settings.AiDenoiseStrength <= 1e-6)
      return;

    var modelFile = !string.IsNullOrWhiteSpace(settings.AiDenoiseModel)
      ? AppDataPaths.ModelFile(settings.AiDenoiseModel!)
      : null;

    using var denoiser = new Segmentation.OnnxDenoiser(modelFile);
    if (!denoiser.IsAvailable)
      return;

    using var denoised = denoiser.Denoise(image, settings.AiDenoiseStrength, ct);
    if (denoised is null)
      return;

    // Copy denoised pixels back so the caller's reference stays valid
    // (Apply tracks `output` by reference). Round-trip via a flat buffer
    // because ImageSharp pixel accessors are ref structs.
    var pixels = new Rgba32[denoised.Width * denoised.Height];
    denoised.CopyPixelDataTo(pixels);
    var width = image.Width;
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var dst = accessor.GetRowSpan(y);
        var srcOffset = y * width;
        for (var x = 0; x < width; x++)
          dst[x] = pixels[srcOffset + x];
      }
    });
  }

  /// AI upscale stage. Returns the upscaled image (a fresh allocation) or
  /// the same reference when the model isn't installed / the factor was
  /// degenerate, so the caller can decide whether to swap the buffer.
  /// The user-picked model (settings.AiUpscaleModel) maps to a file under
  /// the app's models directory; null falls back to the default
  /// "upscale.onnx" which is what every prior version of PhotoManager wrote.
  private static Image<Rgba32>? TryAiUpscale(Image<Rgba32> image, DevelopSettings settings, CancellationToken ct = default) {
    if (settings.AiUpscaleFactor <= 1)
      return image;
    var modelFile = !string.IsNullOrWhiteSpace(settings.AiUpscaleModel)
      ? AppDataPaths.ModelFile(settings.AiUpscaleModel!)
      : null;
    using var upscaler = new Segmentation.OnnxUpscaler(modelFile);
    if (!upscaler.IsAvailable)
      return image;
    return upscaler.Upscale(image, settings.AiUpscaleFactor, ct);
  }

  /// AI colorize stage. Lazily opens the OnnxColorizer; when no model is
  /// installed the call is a graceful no-op. Otherwise the source image
  /// is replaced with the colorised result, blended by
  /// <see cref="DevelopSettings.AiColorizeAmount"/>. Returns null on
  /// failure so the caller falls back to the un-colourised image.
  private static Image<Rgba32>? TryAiColorize(Image<Rgba32> image, DevelopSettings settings, CancellationToken ct = default) {
    if (settings.AiColorizeAmount <= 1e-6)
      return null;
    // ColorizerRouter dispatches to OnnxColorizer (DeOldify) or
    // OnnxColorizerDDColor based on the model filename — they have
    // incompatible I/O contracts (RGB-out vs Lab-ab-out).
    return Segmentation.ColorizerRouter.Colorize(image, settings.AiColorizeModel, settings.AiColorizeAmount, ct);
  }

  /// <summary>
  /// Apply a single masked local adjustment. Pixel weight comes from
  /// <see cref="ComputeMaskWeight"/>; weighted exposure / contrast /
  /// highlights / shadows / saturation / WB / clarity are added on top of
  /// the unmodified pixel and blended back via the mask weight.
  /// </summary>
  private static void ApplyLocalAdjustment(Image<Rgba32> image, LocalAdjustment adj) {
    var w = image.Width;
    var h = image.Height;
    var amount = Math.Clamp(adj.Amount, 0, 1);
    if (amount < 1e-6)
      return;

    var exposureMult = Math.Pow(2, adj.Exposure);
    var contrastFactor = 1 + adj.Contrast / 100.0;
    var saturationFactor = 1 + adj.Saturation / 100.0;
    var clarityFactor = adj.Clarity / 200.0;
    var highlightsAmount = adj.Highlights / 100.0;
    var shadowsAmount = adj.Shadows / 100.0;
    var redMult   = 1 + adj.Temperature / 333.0;
    var blueMult  = 1 - adj.Temperature / 333.0;
    var greenMult = 1 - adj.Tint / 333.0;
    var magentaMult = 1 + adj.Tint / 666.0;

    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var px = row[x];
          var origR = px.R / 255.0;
          var origG = px.G / 255.0;
          var origB = px.B / 255.0;

          var weight = WeightedMask(adj.Mask, x, y, w, h, origR, origG, origB);
          if (adj.SubMasks is { Count: > 0 } subs) {
            foreach (var sub in subs) {
              var sw = WeightedMask(sub, x, y, w, h, origR, origG, origB);
              weight = sub.Combine switch {
                MaskCombineOp.Subtract  => Math.Clamp(weight - sw, 0, 1),
                MaskCombineOp.Intersect => weight * sw,
                _                       => Math.Clamp(weight + sw, 0, 1)
              };
            }
          }
          weight *= amount;
          if (weight < 1e-6)
            continue;

          var r = origR; var g = origG; var b = origB;

          if (adj.Exposure != 0) {
            r *= exposureMult; g *= exposureMult; b *= exposureMult;
          }
          if (adj.Temperature != 0 || adj.Tint != 0) {
            r *= redMult * magentaMult;
            g *= greenMult;
            b *= blueMult * magentaMult;
          }
          if (adj.Contrast != 0) {
            r = (r - 0.5) * contrastFactor + 0.5;
            g = (g - 0.5) * contrastFactor + 0.5;
            b = (b - 0.5) * contrastFactor + 0.5;
          }
          if (adj.Highlights != 0 || adj.Shadows != 0) {
            var lum = 0.299 * r + 0.587 * g + 0.114 * b;
            var lumAdj = ApplyToneShifts(lum, highlightsAmount, shadowsAmount, 0, 0);
            var lumDelta = lumAdj - lum;
            r += lumDelta; g += lumDelta; b += lumDelta;
          }
          if (clarityFactor != 0) {
            var lum = 0.299 * r + 0.587 * g + 0.114 * b;
            var midWeight = 1 - 4 * (lum - 0.5) * (lum - 0.5);
            var clarityShift = (lum - 0.5) * clarityFactor * midWeight;
            r += clarityShift; g += clarityShift; b += clarityShift;
          }
          if (adj.Saturation != 0) {
            var lum = 0.299 * r + 0.587 * g + 0.114 * b;
            r = lum + (r - lum) * saturationFactor;
            g = lum + (g - lum) * saturationFactor;
            b = lum + (b - lum) * saturationFactor;
          }

          // Per-mask Luminance: lift toward white / crush toward black,
          // hue-preserving (same idea as the global highlights/shadows).
          if (adj.Luminance != 0) {
            var k = adj.Luminance / 100.0;
            var delta = k >= 0
              ? (1 - (0.299 * r + 0.587 * g + 0.114 * b)) * k * 0.5
              : (0.299 * r + 0.587 * g + 0.114 * b) * k * 0.5;
            r += delta; g += delta; b += delta;
          }

          // Per-mask toning: tint the masked region with a single hue.
          if (adj.ToningSaturation > 1e-6) {
            var (tr, tg, tb) = HueSatToTint(adj.ToningHue, adj.ToningSaturation / 100.0);
            r += tr * 0.4; g += tg * 0.4; b += tb * 0.4;
          }

          // Per-mask defringe: desaturate purple / green halos within the mask.
          if (adj.Defringe > 1e-6) {
            RgbToHsl(r, g, b, out var hueDef, out var satDef, out var lumDef);
            var hueDeg = hueDef * 360;
            var amount = Math.Clamp(adj.Defringe / 20.0, 0, 1);
            if (satDef > 0.4 && ((hueDeg >= 265 && hueDeg <= 320) || (hueDeg >= 60 && hueDeg <= 160))) {
              satDef *= 1 - amount;
              HslToRgb(hueDef, satDef, lumDef, out r, out g, out b);
            }
          }

          // Blend by the mask weight.
          r = origR + (r - origR) * weight;
          g = origG + (g - origG) * weight;
          b = origB + (b - origB) * weight;
          row[x] = new Rgba32(ToByte(r), ToByte(g), ToByte(b), px.A);
        }
      }
    });
  }

  /// <summary>
  /// Range-mask multiplier: smoothstep-rises from 0 at <paramref name="min"/> - feather/2
  /// to 1 at <paramref name="min"/> + feather/2, then symmetrically falls
  /// at the upper edge. Used to narrow a geometric mask to a luminance band.
  /// </summary>
  private static double LuminanceRangeWeight(double lum, double min, double max, double feather) {
    feather = Math.Max(1e-3, feather);
    var halfFeather = feather * 0.5;
    var lo = Math.Clamp((lum - (min - halfFeather)) / feather, 0, 1);
    var hi = Math.Clamp(((max + halfFeather) - lum) / feather, 0, 1);
    var loSmooth = lo * lo * (3 - 2 * lo);
    var hiSmooth = hi * hi * (3 - 2 * hi);
    return Math.Min(loSmooth, hiSmooth);
  }

  /// <summary>
  /// Hue range-mask multiplier. Hue is normalised 0..1 around the wheel.
  /// When max &lt; min the range wraps (e.g. 0.95..0.05 selects reds across
  /// the seam). Pixels with very low saturation get a soft pass — pure
  /// greys would otherwise be excluded by every hue range.
  /// </summary>
  private static double HueRangeWeight(double hue, double sat, double min, double max, double feather) {
    feather = Math.Max(1e-3, feather);
    // Pure-grey safety: with sat near zero, don't gate by hue.
    if (sat < 0.05)
      return 1;
    bool wraps = max < min;
    double inside;
    if (!wraps) {
      var halfFeather = feather * 0.5;
      var lo = Math.Clamp((hue - (min - halfFeather)) / feather, 0, 1);
      var hi = Math.Clamp(((max + halfFeather) - hue) / feather, 0, 1);
      inside = Math.Min(lo * lo * (3 - 2 * lo), hi * hi * (3 - 2 * hi));
    } else {
      // Treat as union of [min..1] and [0..max].
      inside = Math.Max(
        HueRangeWeight(hue, sat, min,    1.0,  feather),
        HueRangeWeight(hue, sat, 0.0,    max,  feather));
    }
    return inside;
  }

  /// <summary>
  /// Geometric mask weight × range filters (luminance and hue). Used by
  /// the local-adjustment pass to combine multiple masks fairly.
  /// </summary>
  private static double WeightedMask(LocalMask mask, int x, int y, int width, int height, double r, double g, double b) {
    var w = ComputeMaskWeight(mask, x, y, width, height);
    if (w < 1e-6) return 0;

    if (mask.LuminanceRangeMin > 1e-6 || mask.LuminanceRangeMax < 1 - 1e-6) {
      var lum = 0.299 * r + 0.587 * g + 0.114 * b;
      w *= LuminanceRangeWeight(lum, mask.LuminanceRangeMin, mask.LuminanceRangeMax, mask.LuminanceRangeFeather);
      if (w < 1e-6) return 0;
    }
    if (mask.HueRangeMin > 1e-6 || mask.HueRangeMax < 1 - 1e-6) {
      RgbToHsl(r, g, b, out var hue, out var sat, out _);
      w *= HueRangeWeight(hue, sat, mask.HueRangeMin, mask.HueRangeMax, mask.HueRangeFeather);
    }
    return w;
  }

  /// <summary>
  /// Mask weight at pixel <paramref name="x"/>, <paramref name="y"/>.
  /// Linear gradient: clamped projection 0..1 along the line. Radial:
  /// SmoothStep falloff from inside the (rotated) ellipse to outside,
  /// modulated by the Feather control.
  /// </summary>
  private static double ComputeMaskWeight(LocalMask mask, int x, int y, int width, int height) {
    var px = (double)x / width;
    var py = (double)y / height;
    switch (mask.Type) {
      case LocalMaskType.Linear: {
        var dx = mask.X1 - mask.X0;
        var dy = mask.Y1 - mask.Y0;
        var lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-9) return 1.0;
        var t = ((px - mask.X0) * dx + (py - mask.Y0) * dy) / lenSq;
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);  // smooth so the gradient line doesn't band
      }
      case LocalMaskType.Radial: {
        var ax = px - mask.CenterX;
        var ay = py - mask.CenterY;
        if (Math.Abs(mask.Angle) > 1e-6) {
          var rad = mask.Angle * Math.PI / 180.0;
          var c = Math.Cos(rad); var s = Math.Sin(rad);
          var rx = ax * c + ay * s;
          var ry = -ax * s + ay * c;
          ax = rx; ay = ry;
        }
        var nx = ax / Math.Max(1e-6, mask.RadiusX);
        var ny = ay / Math.Max(1e-6, mask.RadiusY);
        var dist = Math.Sqrt(nx * nx + ny * ny);
        var feather = Math.Clamp(mask.Feather / 100.0, 0, 1);
        // Inside the ellipse the weight is 1.0 fading to 0.0 across the
        // feather band; outside it's 0. SmoothStep keeps the falloff
        // visually pleasing.
        var inner = 1 - feather;
        double t;
        if (dist <= inner) t = 1;
        else if (dist >= 1) t = 0;
        else {
          var u = (1 - dist) / Math.Max(1e-6, feather);
          t = u * u * (3 - 2 * u);
        }
        return mask.Invert ? 1 - t : t;
      }
      case LocalMaskType.Brush: {
        if (mask.BrushDabs is not { Count: > 0 } dabs)
          return 0;
        // Sum each dab's contribution. Positive flow is paint, negative is
        // erase; the running weight is clamped 0..1 between dabs so a
        // long paint stroke saturates at "fully covered".
        var weight = 0.0;
        foreach (var dab in dabs) {
          if (Math.Abs(dab.Radius) < 1e-6) continue;
          var ddx = px - dab.X;
          var ddy = py - dab.Y;
          var dist = Math.Sqrt(ddx * ddx + ddy * ddy) / dab.Radius;
          if (dist >= 1) continue;
          // Smooth radial falloff (Wendland-like quadratic).
          var falloff = 1 - dist * dist;
          weight = Math.Clamp(weight + falloff * dab.Flow, 0, 1);
        }
        return mask.Invert ? 1 - weight : weight;
      }
    }
    return 0;
  }

  /// <summary>
  /// Projective transform around the image centre. The slider values build
  /// a sequence (translate → scale+aspect → rotate → horizontal keystone
  /// → vertical keystone); the inverse of that sequence maps each output
  /// pixel back to its source coordinate, where bilinear interpolation
  /// gives the colour.
  /// </summary>
  private static void ApplyPerspectiveTransform(Image<Rgba32> image, DevelopSettings s) {
    if (Math.Abs(s.PerspectiveVertical)   < 1e-6
     && Math.Abs(s.PerspectiveHorizontal) < 1e-6
     && Math.Abs(s.PerspectiveRotate)     < 1e-6
     && Math.Abs(s.PerspectiveScale - 100) < 1e-6
     && Math.Abs(s.PerspectiveAspect)     < 1e-6
     && Math.Abs(s.PerspectiveX)          < 1e-6
     && Math.Abs(s.PerspectiveY)          < 1e-6)
      return;

    var w = image.Width;
    var h = image.Height;
    var cx = w * 0.5;
    var cy = h * 0.5;

    var srcPixels = new Rgba32[w * h];
    image.CopyPixelDataTo(srcPixels);

    // Slider → matrix-coefficient mapping. ±100 keystone bends the far
    // edge by ~50% of image width; ±100 X/Y shifts a full image; aspect
    // ±100 stretches the long axis by ±30%.
    var kV = s.PerspectiveVertical   / 100.0 * 0.5;
    var kH = s.PerspectiveHorizontal / 100.0 * 0.5;
    var scale = Math.Max(0.05, s.PerspectiveScale / 100.0);
    var aspect = 1 + s.PerspectiveAspect / 100.0 * 0.3;
    var tx = s.PerspectiveX / 100.0;
    var ty = s.PerspectiveY / 100.0;
    var rotateRad = s.PerspectiveRotate * Math.PI / 180.0;
    var cosR = Math.Cos(rotateRad);
    var sinR = Math.Sin(rotateRad);

    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          // Normalise to (-1, +1) so slider amounts are resolution-agnostic.
          var nx = (x - cx) / cx;
          var ny = (y - cy) / cy;

          // Inverse translate.
          nx -= tx; ny -= ty;
          // Inverse scale + aspect.
          nx /= scale;
          ny /= scale * aspect;
          // Inverse rotate.
          var rx = nx * cosR + ny * sinR;
          var ry = -nx * sinR + ny * cosR;
          nx = rx; ny = ry;
          // Inverse horizontal keystone — projective[1 0 0; 0 1 0; kH 0 1]^-1
          // resolves to (nx, ny) /= 1 - kH*nx.
          var divH = 1 - kH * nx;
          if (Math.Abs(divH) > 1e-6) { nx /= divH; ny /= divH; }
          // Inverse vertical keystone.
          var divV = 1 - kV * ny;
          if (Math.Abs(divV) > 1e-6) { nx /= divV; ny /= divV; }

          var sx = cx + nx * cx;
          var sy = cy + ny * cy;
          if (sx < 0 || sx > w - 1.001 || sy < 0 || sy > h - 1.001) {
            row[x] = new Rgba32(0, 0, 0, srcPixels[y * w + x].A);
            continue;
          }
          var rChannel = SampleBilinearChannel(srcPixels, w, h, sx, sy, 0);
          var gChannel = SampleBilinearChannel(srcPixels, w, h, sx, sy, 1);
          var bChannel = SampleBilinearChannel(srcPixels, w, h, sx, sy, 2);
          row[x] = new Rgba32((byte)rChannel, (byte)gChannel, (byte)bChannel, srcPixels[y * w + x].A);
        }
      }
    });
  }

  /// <summary>
  /// Single coordinate-map pass that applies manual radial distortion
  /// correction + per-channel chromatic-aberration scaling. Bilinear
  /// sampling keeps the result smooth without re-quantising. No-op when
  /// all three settings are zero.
  /// </summary>
  private static void ApplyLensCorrections(Image<Rgba32> image, DevelopSettings settings) {
    if (Math.Abs(settings.LensManualDistortion) < 1e-6
     && Math.Abs(settings.ChromaticAberrationR) < 1e-6
     && Math.Abs(settings.ChromaticAberrationB) < 1e-6)
      return;

    var w = image.Width;
    var h = image.Height;
    var cx = w * 0.5;
    var cy = h * 0.5;
    var distort = settings.LensManualDistortion / 100.0;        // -1..+1
    var caR = settings.ChromaticAberrationR / 100.0 * 0.005;    // ±0.5% scale
    var caB = settings.ChromaticAberrationB / 100.0 * 0.005;

    // Snapshot the source so the per-pixel sampler reads from a stable
    // copy while we overwrite the original.
    var srcPixels = new Rgba32[w * h];
    image.CopyPixelDataTo(srcPixels);

    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        var dy = (y - cy) / cy;
        for (var x = 0; x < row.Length; x++) {
          var dx = (x - cx) / cx;
          var r2 = dx * dx + dy * dy;
          var distFactor = 1 + distort * 0.3 * r2;

          // R / G / B sampled at slightly different radii to correct CA.
          var fr = distFactor * (1 + caR * r2);
          var fb = distFactor * (1 + caB * r2);
          var rChannel = SampleBilinearChannel(srcPixels, w, h, cx + (x - cx) * fr,        cy + (y - cy) * fr,        0);
          var gChannel = SampleBilinearChannel(srcPixels, w, h, cx + (x - cx) * distFactor, cy + (y - cy) * distFactor, 1);
          var bChannel = SampleBilinearChannel(srcPixels, w, h, cx + (x - cx) * fb,        cy + (y - cy) * fb,        2);
          row[x] = new Rgba32((byte)rChannel, (byte)gChannel, (byte)bChannel, srcPixels[y * w + x].A);
        }
      }
    });
  }

  /// <summary>
  /// Bilinear sample of a single channel from a flat Rgba32 source.
  /// Channel index: 0 = R, 1 = G, 2 = B. Out-of-bounds samples clamp to
  /// the edge so distortion / CA at the rim doesn't punch holes.
  /// </summary>
  private static double SampleBilinearChannel(Rgba32[] src, int width, int height, double sx, double sy, int channel) {
    sx = Math.Clamp(sx, 0, width - 1.001);
    sy = Math.Clamp(sy, 0, height - 1.001);
    var x0 = (int)sx;
    var y0 = (int)sy;
    var fx = sx - x0;
    var fy = sy - y0;
    var idx00 = y0 * width + x0;
    var p00 = ChannelOf(src[idx00], channel);
    var p10 = ChannelOf(src[idx00 + 1], channel);
    var p01 = ChannelOf(src[idx00 + width], channel);
    var p11 = ChannelOf(src[idx00 + width + 1], channel);
    return p00 * (1 - fx) * (1 - fy) + p10 * fx * (1 - fy)
         + p01 * (1 - fx) * fy        + p11 * fx * fy;
  }

  private static byte ChannelOf(Rgba32 px, int channel) => channel switch {
    0 => px.R,
    1 => px.G,
    _ => px.B
  };

  /// Resolves the named LUT (lazy-loaded from %AppData%/PhotoManager/luts/)
  /// and applies it via trilinear interpolation. Skipped when no look is
  /// selected, opacity ≤ 0, or the file is missing / unparseable.
  private static void ApplyCreativeLook(Image<Rgba32> image, DevelopSettings settings) {
    if (string.IsNullOrWhiteSpace(settings.LookName) || settings.LookOpacity <= 1e-6)
      return;
    var lut = LookCache.Resolve(settings.LookName!);
    if (lut is null)
      return;
    Lut3D.Apply(image, lut, settings.LookOpacity);
  }

  /// Caches parsed LUTs by filename so a slider drag doesn't re-read disk
  /// on every preview tick. Invalidates entries whose backing file's
  /// last-write timestamp changes.
  private static class LookCache {
    private static readonly Dictionary<string, (DateTime Stamp, Lut3D? Lut)> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static Lut3D? Resolve(string lookName) {
      var dir = AppDataPaths.SubDirectory("luts");
      var file = new FileInfo(Path.Combine(dir.FullName, lookName));
      if (!file.Exists) {
        var withCube = new FileInfo(file.FullName + ".cube");
        var with3dl  = new FileInfo(file.FullName + ".3dl");
        if (withCube.Exists)      file = withCube;
        else if (with3dl.Exists)  file = with3dl;
        else                      return null;
      }
      var stamp = file.LastWriteTimeUtc;
      lock (Gate) {
        if (Entries.TryGetValue(file.FullName, out var cached) && cached.Stamp == stamp)
          return cached.Lut;
        Lut3D? lut;
        try { lut = LutLoader.Load(file); }
        catch { lut = null; }
        Entries[file.FullName] = (stamp, lut);
        return lut;
      }
    }
  }

  /// <summary>
  /// Applies the user's normalised crop rectangle. Runs AFTER pixel
  /// adjustments + arbitrary rotation so the crop coordinates land on
  /// the rotated image (which is what the user sees in the preview).
  /// </summary>
  private static void ApplyCropRectangle(Image<Rgba32> image, DevelopSettings settings) {
    var l = Math.Clamp(settings.CropLeft, 0, 1);
    var t = Math.Clamp(settings.CropTop, 0, 1);
    var r = Math.Clamp(settings.CropRight, 0, 1);
    var b = Math.Clamp(settings.CropBottom, 0, 1);
    if (l < 1e-6 && t < 1e-6 && r > 1 - 1e-6 && b > 1 - 1e-6)
      return;
    if (r <= l || b <= t)
      return;

    var x = (int)Math.Round(l * image.Width);
    var y = (int)Math.Round(t * image.Height);
    var w = Math.Max(1, (int)Math.Round((r - l) * image.Width));
    var h = Math.Max(1, (int)Math.Round((b - t) * image.Height));
    if (x + w > image.Width)  w = image.Width  - x;
    if (y + h > image.Height) h = image.Height - y;
    image.Mutate(c => c.Crop(new Rectangle(x, y, w, h)));
  }

  /// <summary>Convenience: load (RAW-aware via PNGCrushCS), develop, save as JPEG.</summary>
  public static async Task RenderToJpegAsync(
    FileInfo sourceFile,
    FileInfo destinationFile,
    DevelopSettings settings,
    int jpegQuality = 92,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(sourceFile);
    ArgumentNullException.ThrowIfNull(destinationFile);
    if (!sourceFile.Exists)
      throw new FileNotFoundException("Source image not found.", sourceFile.FullName);

    using var source = await RawImageLoader.LoadAsync(sourceFile, cancellationToken);
    using var developed = Apply(source, settings);

    destinationFile.Directory?.Create();
    var encoder = new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = jpegQuality };
    await developed.SaveAsJpegAsync(destinationFile.FullName, encoder, cancellationToken);
  }

  internal static void ApplyPixelAdjustments(Image<Rgba32> image, DevelopSettings settings, bool previewMode = false, CancellationToken ct = default) {
    var exposureMultiplier = Math.Pow(2, settings.ExposureStops);
    var contrastFactor = 1 + settings.ContrastPercent / 100.0;
    var saturationFactor = 1 + settings.SaturationPercent / 100.0;
    var redGainMult   = 1 + settings.RedGain   / 100.0;
    var greenGainMult = 1 + settings.GreenGain / 100.0;
    var blueGainMult  = 1 + settings.BlueGain  / 100.0;
    // Pre-compute the tone-curve LUT once so the per-pixel loop is a
    // single array lookup. Identity curve → null and we skip the LUT pass.
    var lut = BuildCurveLut(settings.ToneCurvePoints, settings.ToneCurveInterpolation);
    var redLut   = BuildCurveLut(settings.RedCurvePoints,   settings.ToneCurveInterpolation);
    var greenLut = BuildCurveLut(settings.GreenCurvePoints, settings.ToneCurveInterpolation);
    var blueLut  = BuildCurveLut(settings.BlueCurvePoints,  settings.ToneCurveInterpolation);
    var vibranceFactor = settings.VibrancePercent / 100.0;
    var clarityFactor = settings.ClarityPercent / 200.0; // gentler than contrast
    // Dehaze: positive boosts midtone contrast + saturation; negative softens.
    // Half the strength of the per-pixel weighting so it sits between Clarity and full contrast.
    var dehazeFactor = settings.DehazePercent / 100.0;

    // Pre-build HSL mixer arrays (8 bands each) so the pixel loop just
    // does interpolation. Identity-list -> null so the pixel loop can
    // skip the conversion entirely on photos with no HSL edits.
    var hueShifts = NormaliseBandList(settings.HslHueShifts);
    var satShifts = NormaliseBandList(settings.HslSaturationShifts);
    var lumShifts = NormaliseBandList(settings.HslLuminanceShifts);
    var hasHslWork = hueShifts is not null || satShifts is not null || lumShifts is not null;

    // Color Grading: pre-compute the 4 wheel tints (deltaR/G/B at full strength)
    // and per-section gating amounts. Skipped entirely when nothing's set.
    var (shTintR, shTintG, shTintB) = HueSatToTint(settings.GradeShadowHue,    settings.GradeShadowSat / 100.0);
    var (mtTintR, mtTintG, mtTintB) = HueSatToTint(settings.GradeMidtoneHue,   settings.GradeMidtoneSat / 100.0);
    var (hlTintR, hlTintG, hlTintB) = HueSatToTint(settings.GradeHighlightHue, settings.GradeHighlightSat / 100.0);
    var (gbTintR, gbTintG, gbTintB) = HueSatToTint(settings.GradeGlobalHue,    settings.GradeGlobalSat / 100.0);
    var shLum = settings.GradeShadowLum    / 100.0;
    var mtLum = settings.GradeMidtoneLum   / 100.0;
    var hlLum = settings.GradeHighlightLum / 100.0;
    var gbLum = settings.GradeGlobalLum    / 100.0;
    var hasGrade = Math.Abs(settings.GradeShadowSat) > 1e-6 || Math.Abs(shLum) > 1e-6
                || Math.Abs(settings.GradeMidtoneSat) > 1e-6 || Math.Abs(mtLum) > 1e-6
                || Math.Abs(settings.GradeHighlightSat) > 1e-6 || Math.Abs(hlLum) > 1e-6
                || Math.Abs(settings.GradeGlobalSat) > 1e-6 || Math.Abs(gbLum) > 1e-6;

    // Color Enhancement — skin-tone-protective vibrance variant. Same
    // engine as Vibrance, but extra weight goes to "non-skin" hues
    // (away from orange/yellow) so people don't go plastic-y.
    var colorEnhanceFactor = settings.ColorEnhancement / 100.0;
    var hasColorEnhance = Math.Abs(colorEnhanceFactor) > 1e-6;

    // Vignette: pre-compute centre + max-radius so the pixel loop only does
    // a sqrt + falloff curve. Roundness shapes the rectangle vs ellipse;
    // midpoint sets where falloff begins; feather softens the edge.
    var vignetteAmount = settings.VignetteAmount / 100.0;
    var hasVignette = Math.Abs(vignetteAmount) > 1e-6;
    var vignetteMid = Math.Clamp(settings.VignetteMidpoint / 100.0, 0, 1);
    var vignetteFeather = 1 + Math.Clamp(settings.VignetteFeather / 100.0, 0, 1) * 3;
    var vignetteRound = settings.VignetteRoundness / 100.0;  // -1..+1
    var imgW = image.Width;
    var imgH = image.Height;
    var cx = imgW * 0.5;
    var cy = imgH * 0.5;
    var maxDist = Math.Sqrt(cx * cx + cy * cy);

    // Grain: per-pixel Gaussian noise driven by a deterministic hash so
    // re-renders look identical. Size scales the spatial block (so big
    // grain looks chunky), Frequency gates which pixels get noise at all.
    var grainAmount = settings.GrainAmount / 100.0;
    var hasGrain = grainAmount > 1e-6;
    var grainBlock = Math.Max(1, (int)Math.Round(1 + settings.GrainSize / 25.0));      // 1..5 px
    var grainFreq  = Math.Clamp(settings.GrainFrequency / 100.0, 0.05, 1.0);
    // Vignette highlight protection: when amount<0 (darkening), spare
    // bright pixels by this factor so highlights don't get crushed.
    var vignetteHighlightProtection = Math.Clamp(settings.VignetteHighlightContrast / 100.0, 0, 1);

    // Black & White: per-band gray-mixer weights. When ConvertToGrayscale
    // is on, the pixel loop converts to a weighted gray instead of the
    // BT.601 luminance, so each hue band contributes the user's amount.
    var grayMixers = new[] {
      settings.GrayMixerRed, settings.GrayMixerOrange, settings.GrayMixerYellow, settings.GrayMixerGreen,
      settings.GrayMixerAqua, settings.GrayMixerBlue, settings.GrayMixerPurple, settings.GrayMixerMagenta
    };

    // Split Toning: pre-compute tints + a split point in [0..1] from Balance.
    var (stShTintR, stShTintG, stShTintB) = HueSatToTint(settings.SplitToningShadowHue, settings.SplitToningShadowSaturation / 100.0);
    var (stHlTintR, stHlTintG, stHlTintB) = HueSatToTint(settings.SplitToningHighlightHue, settings.SplitToningHighlightSaturation / 100.0);
    var hasSplitTone = Math.Abs(settings.SplitToningShadowSaturation) > 1e-6
                    || Math.Abs(settings.SplitToningHighlightSaturation) > 1e-6;
    var splitBalance = 0.5 + Math.Clamp(settings.SplitToningBalance, -100, 100) / 200.0;  // 0..1

    // Parametric tone curve LUT — built once per render so the per-pixel
    // loop just does an indexed lookup. Splits are 0..100 (Adobe defaults
    // 25/50/75); the four shifts amplify each band's response.
    var paramLut = BuildParametricLut(settings);

    // Defringe — narrow-range desaturation for purple / green halos. Adobe
    // range 0..20; we treat saturation > 0.4 as a proxy for "edge pixel"
    // since real fringes are highly saturated. Works well for the obvious
    // halos around backlit branches; less well for purple subjects.
    var defringePurple = settings.DefringePurpleAmount / 20.0;  // 0..1
    var defringeGreen  = settings.DefringeGreenAmount  / 20.0;
    var hasDefringe = defringePurple > 1e-6 || defringeGreen > 1e-6;

    // Camera calibration — small per-primary tweaks. Each value rotates
    // hue and scales saturation around its primary's anchor in HSL space.
    var calibR = (settings.CalibrationRedHue, settings.CalibrationRedSaturation);
    var calibG = (settings.CalibrationGreenHue, settings.CalibrationGreenSaturation);
    var calibB = (settings.CalibrationBlueHue, settings.CalibrationBlueSaturation);
    var hasCalibration = Math.Abs(calibR.Item1) > 1e-6 || Math.Abs(calibR.Item2) > 1e-6
                      || Math.Abs(calibG.Item1) > 1e-6 || Math.Abs(calibG.Item2) > 1e-6
                      || Math.Abs(calibB.Item1) > 1e-6 || Math.Abs(calibB.Item2) > 1e-6;
    var highlightsAmount = settings.HighlightsPercent / 100.0;
    var shadowsAmount = settings.ShadowsPercent / 100.0;
    var whitesAmount = settings.WhitesPercent / 200.0;   // half-strength near 1.0
    var blacksAmount = settings.BlacksPercent / 200.0;   // half-strength near 0.0
    // Map -100..+100 to a per-channel multiplier. At +100 red ×1.3 / blue ×0.7
    // which is a roughly warm-white cast; at -100 the reverse.
    var redMult = 1 + settings.TemperatureShift / 333.0;
    var blueMult = 1 - settings.TemperatureShift / 333.0;
    // Tint pulls green vs. magenta. Negative = greener; positive = pinker.
    var greenMult = 1 - settings.TintShift / 333.0;
    var magentaMult = 1 + settings.TintShift / 666.0;

    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        // Pre-compute the per-row vignette y component so the inner loop
        // only handles the x term.
        var dyNorm = (y - cy) / cy;
        for (var x = 0; x < row.Length; x++) {
          var px = row[x];
          var r = px.R / 255.0;
          var g = px.G / 255.0;
          var b = px.B / 255.0;

          // Exposure
          r *= exposureMultiplier;
          g *= exposureMultiplier;
          b *= exposureMultiplier;

          // White balance: temperature (R/B) + tint (G vs R+B).
          r *= redMult * magentaMult;
          g *= greenMult;
          b *= blueMult * magentaMult;

          // Per-channel gain (after WB so the user sees the same colour
          // they'd see in a Curves dialog with R/G/B sliders).
          r *= redGainMult;
          g *= greenGainMult;
          b *= blueGainMult;

          // Tone-curve adjustments, all luminance-driven so colors don't
          // shift independently. Each is a soft S/cube curve about its
          // anchor (0 / 0.25 / 0.75 / 1).
          var lum = 0.299 * r + 0.587 * g + 0.114 * b;
          var lumAdj = ApplyToneShifts(lum, highlightsAmount, shadowsAmount, whitesAmount, blacksAmount);
          var lumDelta = lumAdj - lum;
          r += lumDelta;
          g += lumDelta;
          b += lumDelta;

          // Contrast about 0.5 midgray.
          r = (r - 0.5) * contrastFactor + 0.5;
          g = (g - 0.5) * contrastFactor + 0.5;
          b = (b - 0.5) * contrastFactor + 0.5;

          // Clarity: local-contrast in the midtones via a softer S-curve
          // weighted by a bell at midgray. Cheap approximation, no blur.
          if (clarityFactor != 0) {
            lum = 0.299 * r + 0.587 * g + 0.114 * b;
            var midWeight = 1 - 4 * (lum - 0.5) * (lum - 0.5);
            var clarityShift = (lum - 0.5) * clarityFactor * midWeight;
            r += clarityShift;
            g += clarityShift;
            b += clarityShift;
          }

          // Dehaze: cheap approximation — push midtones harder than Clarity
          // and lift saturation slightly so flat / hazy areas regain
          // contrast and color. Stops short of a true dark-channel-prior
          // dehaze, which would need a per-image atmospheric estimate.
          if (dehazeFactor != 0) {
            lum = 0.299 * r + 0.587 * g + 0.114 * b;
            var midWeight = 1 - 4 * (lum - 0.5) * (lum - 0.5);
            var dehazeShift = (lum - 0.5) * dehazeFactor * 0.5 * midWeight;
            r += dehazeShift;
            g += dehazeShift;
            b += dehazeShift;
            if (dehazeFactor > 0) {
              var dehazeSatFactor = 1 + dehazeFactor * 0.25 * midWeight;
              r = lum + (r - lum) * dehazeSatFactor;
              g = lum + (g - lum) * dehazeSatFactor;
              b = lum + (b - lum) * dehazeSatFactor;
            }
          }

          // Saturation via luminance-preserving interpolation.
          lum = 0.299 * r + 0.587 * g + 0.114 * b;
          r = lum + (r - lum) * saturationFactor;
          g = lum + (g - lum) * saturationFactor;
          b = lum + (b - lum) * saturationFactor;

          // Color Enhancement — vibrance with skin-tone protection.
          // Compute a hue-distance weight: hues near 30° (skin) get
          // less boost, hues elsewhere get full vibrance behaviour.
          if (hasColorEnhance) {
            var maxC = Math.Max(r, Math.Max(g, b));
            var minC = Math.Min(r, Math.Min(g, b));
            var sat = maxC > 0 ? (maxC - minC) / Math.Max(maxC, 1e-6) : 0;
            if (sat > 0.05) {
              RgbToHsl(r, g, b, out var ceHue, out _, out _);
              var hueDeg = ceHue * 360;
              // Skin-tone window centred on ~30° (orange).
              var skinWeight = Math.Max(0, 1 - Math.Abs(hueDeg - 30) / 30.0);
              var protect = 1 - skinWeight * 0.7;
              var ceWeight = (1 - sat) * protect;
              var factor = 1 + colorEnhanceFactor * ceWeight;
              var ceLum = 0.299 * r + 0.587 * g + 0.114 * b;
              r = ceLum + (r - ceLum) * factor;
              g = ceLum + (g - ceLum) * factor;
              b = ceLum + (b - ceLum) * factor;
            }
          }

          // Vibrance: scale interpolation amount by (1 - current saturation)
          // so already-saturated colors barely move.
          if (vibranceFactor != 0) {
            var maxC = Math.Max(r, Math.Max(g, b));
            var minC = Math.Min(r, Math.Min(g, b));
            var sat = maxC > 0 ? (maxC - minC) / Math.Max(maxC, 1e-6) : 0;
            var weight = 1 - Math.Min(1, Math.Max(0, sat));
            var factor = 1 + vibranceFactor * weight;
            lum = 0.299 * r + 0.587 * g + 0.114 * b;
            r = lum + (r - lum) * factor;
            g = lum + (g - lum) * factor;
            b = lum + (b - lum) * factor;
          }

          // Camera calibration — three per-primary nudges (hue + sat).
          // Applied early so subsequent colour edits operate on the
          // calibrated palette.
          if (hasCalibration) {
            RgbToHsl(r, g, b, out var hCal, out var sCal, out var lCal);
            ApplyCalibrationShifts(ref hCal, ref sCal, calibR, calibG, calibB);
            HslToRgb(hCal, sCal, lCal, out r, out g, out b);
          }

          // HSL color mixer — pixels get weighted contributions from the
          // two nearest of the eight bands. Hue rotates the colour, Sat
          // scales saturation about the pixel's lightness, Lum lifts /
          // crushes pixels in that band without changing colour.
          if (hasHslWork) {
            RgbToHsl(r, g, b, out var h_, out var s_, out var l_);
            ApplyHslBandShifts(ref h_, ref s_, ref l_, hueShifts, satShifts, lumShifts);
            HslToRgb(h_, s_, l_, out r, out g, out b);
          }

          // Defringe — desaturate purple / green halos near edges.
          if (hasDefringe) {
            RgbToHsl(r, g, b, out var hDef, out var sDef, out var lDef);
            var hueDeg = hDef * 360;
            var changed = false;
            if (defringePurple > 0 && sDef > 0.4 && hueDeg >= 265 && hueDeg <= 320) {
              sDef *= 1 - defringePurple;
              changed = true;
            }
            if (defringeGreen > 0 && sDef > 0.4 && hueDeg >= 60 && hueDeg <= 160) {
              sDef *= 1 - defringeGreen;
              changed = true;
            }
            if (changed)
              HslToRgb(hDef, sDef, lDef, out r, out g, out b);
          }

          // Color Grading: weight the 3 wheels by where the pixel sits in
          // the luminance range, then apply each wheel's tint + lum shift.
          // Global wheel applies uniformly. Strength factor 0.3 keeps the
          // tint subtle so users can stack with HSL without blowing out.
          if (hasGrade) {
            lum = 0.299 * r + 0.587 * g + 0.114 * b;
            var wShadow    = Math.Max(0, 1 - 2 * lum);
            var wHighlight = Math.Max(0, 2 * lum - 1);
            var wMidtone   = Math.Max(0, 1 - wShadow - wHighlight);
            const double tintStrength = 0.3;
            r += (shTintR * wShadow + mtTintR * wMidtone + hlTintR * wHighlight + gbTintR) * tintStrength;
            g += (shTintG * wShadow + mtTintG * wMidtone + hlTintG * wHighlight + gbTintG) * tintStrength;
            b += (shTintB * wShadow + mtTintB * wMidtone + hlTintB * wHighlight + gbTintB) * tintStrength;
            // Per-section luminance lift / crush.
            var gradeLumDelta = (shLum * wShadow + mtLum * wMidtone + hlLum * wHighlight) * 0.3 + gbLum * 0.3;
            r += gradeLumDelta; g += gradeLumDelta; b += gradeLumDelta;
          }

          // Vignette: radial brightness modulation. Roundness <0 squeezes
          // the falloff into an ellipse along the long axis; >0 pushes it
          // toward a rounded square. Feather reshapes the falloff curve.
          // HighlightContrast spares bright pixels when amount<0 so the
          // sky/sun in a corner doesn't get muddied by a darkening vignette.
          if (hasVignette) {
            var dxNorm = (x - cx) / cx;
            // Roundness blends between L2 (circle) and L_inf (square) norm.
            var dist = vignetteRound > 0
              ? Math.Max(Math.Abs(dxNorm), Math.Abs(dyNorm)) * (1 - vignetteRound) + Math.Sqrt(dxNorm*dxNorm + dyNorm*dyNorm) * vignetteRound
              : Math.Sqrt(dxNorm*dxNorm + dyNorm*dyNorm) + Math.Abs(vignetteRound) * Math.Min(Math.Abs(dxNorm), Math.Abs(dyNorm)) * 0.3;
            var dnorm = Math.Min(1.0, dist / Math.Sqrt(2));
            var falloff = Math.Max(0, dnorm - vignetteMid) / Math.Max(1e-3, 1 - vignetteMid);
            falloff = Math.Pow(falloff, vignetteFeather);
            var effectiveFalloff = falloff;
            if (vignetteAmount < 0 && vignetteHighlightProtection > 0) {
              var lumLocal = 0.299 * r + 0.587 * g + 0.114 * b;
              effectiveFalloff *= 1 - vignetteHighlightProtection * Math.Max(0, lumLocal - 0.5) * 2;
            }
            var multiplier = 1 + vignetteAmount * effectiveFalloff;
            r *= multiplier; g *= multiplier; b *= multiplier;
          }

          // Grain: monochrome additive Gaussian-ish noise. Hash from
          // blocked coordinates so larger Size = chunkier grain;
          // Frequency gates which pixels get noise at all.
          if (hasGrain) {
            var bx = x / grainBlock;
            var by = y / grainBlock;
            var hash = (uint)(bx * 374761393 + by * 668265263);
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            var freqGate = (hash & 0xFFu) / 255.0;
            if (freqGate <= grainFreq) {
              var n = (((hash >> 8) & 0xFFFFu) / 32768.0) - 1.0;     // -1..1
              var noise = n * grainAmount * 0.12;
              r += noise; g += noise; b += noise;
            }
          }

          // Split Toning — legacy 2-wheel shadow / highlight tinting.
          // Adobe replaced this with Color Grading but the slider state
          // still ships in older catalogs and must round-trip cleanly.
          if (hasSplitTone) {
            lum = 0.299 * r + 0.587 * g + 0.114 * b;
            var hl = SmoothStep(lum, splitBalance - 0.15, splitBalance + 0.15);
            var sh = 1 - hl;
            const double splitStrength = 0.4;
            r += (stShTintR * sh + stHlTintR * hl) * splitStrength;
            g += (stShTintG * sh + stHlTintG * hl) * splitStrength;
            b += (stShTintB * sh + stHlTintB * hl) * splitStrength;
          }

          // Parametric tone curve — single-channel LUT applied to the
          // pixel's luminance, with the delta replicated across R/G/B so
          // the curve doesn't shift hue (matches the master curve's behavior).
          if (paramLut is not null) {
            lum = 0.299 * r + 0.587 * g + 0.114 * b;
            var paramOut = paramLut[ClampToByte(lum)] / 255.0;
            var paramDelta = paramOut - lum;
            r += paramDelta; g += paramDelta; b += paramDelta;
          }

          // Tone curve master pass — applied uniformly to all channels so
          // it doesn't shift hue. Comes after every other pixel-stage
          // adjustment so the user shapes the FINAL response, not an
          // intermediate one.
          if (lut is not null) {
            r = lut[ClampToByte(r)] / 255.0;
            g = lut[ClampToByte(g)] / 255.0;
            b = lut[ClampToByte(b)] / 255.0;
          }

          // Per-channel tone curves — applied AFTER the master curve so
          // users can shape individual channels without disturbing the
          // overall luminance response. Adobe / Lightroom apply them in
          // the same order; matching that keeps round-tripped looks
          // visually similar.
          if (redLut   is not null) r = redLut  [ClampToByte(r)] / 255.0;
          if (greenLut is not null) g = greenLut[ClampToByte(g)] / 255.0;
          if (blueLut  is not null) b = blueLut [ClampToByte(b)] / 255.0;

          // Black & White conversion — must run last so it captures all
          // upstream colour adjustments (HSL, color grading, split toning)
          // before flattening to gray. Per-band gray-mixer weights bias
          // how each hue contributes to the gray output.
          if (settings.ConvertToGrayscale) {
            var gray = ComputeGrayscale(r, g, b, grayMixers);
            r = g = b = gray;
          }

          row[x] = new Rgba32(ToByte(r), ToByte(g), ToByte(b), px.A);
        }
      }
    });

    // Color noise reduction: blur the chroma channels (Cb, Cr) only,
    // leaving luminance intact. ImageSharp doesn't expose a chroma-only
    // blur, so we approximate by Gaussian-blurring the whole image and
    // then restoring the original Y from the source — chroma stays
    // smoothed, luminance detail isn't sacrificed.
    // Detail (preserve high-freq) shrinks the chroma sigma; Smoothness
    // grows it. Both at default (50) leaves the base sigma unchanged.
    if (settings.ColorNoiseReduction > 1e-6) {
      var detailFactor = 1 - Math.Clamp(settings.ColorNrDetail / 200.0, 0, 0.5);
      var smoothFactor = 1 + Math.Clamp(settings.ColorNrSmoothness / 100.0, 0, 1);
      ApplyChromaBlur(image, settings.ColorNoiseReduction * detailFactor * smoothFactor);
    }

    // AI denoise: sits between chroma smoothing (above) and the spatial
    // luminance smoothness blur (below), so the canonical denoise→sharpen
    // order is preserved. No-op when the model isn't installed or we're
    // rendering a live preview (skipping keeps slider drags responsive —
    // the final Save As pass runs the AI stage).
    if (!previewMode)
      ApplyAiDenoiseStage(image, settings, ct);

    // Smoothness (luminance NR): Gaussian blur, applied BEFORE the
    // sharpening / texture pass so the user can take the edge off noise
    // and then bring back true edges with sharpening. Sigma scales with
    // strength; 50% maps to ~1.0 sigma — gentle by default.
    // LumNrDetail (>0) shrinks the blur sigma so high-frequency texture
    // is preserved; LumNrContrast (>0) does the same so high-contrast
    // edges aren't blurred away. Both default to 0 (no protection).
    if (settings.SmoothnessPercent > 0) {
      var detailProtection   = 1 - Math.Clamp(settings.LuminanceNrDetail   / 200.0, 0, 0.5);
      var contrastProtection = 1 - Math.Clamp(settings.LuminanceNrContrast / 200.0, 0, 0.5);
      var sigma = (float)Math.Clamp(
        settings.SmoothnessPercent / 50.0 * detailProtection * contrastProtection,
        0.1, 2.5);
      image.Mutate(c => c.GaussianBlur(sigma));
    }

    // Texture: high-frequency detail. Smaller-radius unsharp than
    // sharpening; works well for skin / fabric / fine grain. Negative
    // values do a soft micro-blur for skin smoothing.
    if (Math.Abs(settings.TexturePercent) > 0.001) {
      if (settings.TexturePercent > 0) {
        var amount = (float)Math.Clamp(settings.TexturePercent / 100.0, 0, 1.5);
        image.Mutate(c => c.GaussianSharpen(0.7f * amount));
      } else {
        var sigma = (float)Math.Clamp(-settings.TexturePercent / 100.0, 0.1, 1.5);
        image.Mutate(c => c.GaussianBlur(sigma * 0.6f));
      }
    }

    // Sharpening — main pass uses an explicit radius if the user set one,
    // otherwise derives it from the Amount slider for backwards compat.
    // Detail adds a smaller-radius pass on top to bring out high-frequency
    // texture (skin, foliage). When SharpenMasking > 0 we keep the
    // pre-sharpen image around and blend the sharpened result through a
    // Sobel-style edge mask so flat areas stay clean.
    if (settings.SharpeningAmount > 0) {
      var amount = (float)Math.Clamp(settings.SharpeningAmount / 100.0, 0, 1.5);
      var radius = settings.SharpenRadius > 0
        ? (float)Math.Clamp(settings.SharpenRadius, 0.3, 3.0)
        : (float)Math.Clamp(settings.SharpeningAmount / 50.0, 0.5, 3.0);

      Image<Rgba32>? preSharpen = settings.SharpenMasking > 1e-6 ? image.Clone() : null;
      try {
        image.Mutate(c => c.GaussianSharpen(amount * radius));
        if (settings.SharpenDetail > 0) {
          var detail = (float)Math.Clamp(settings.SharpenDetail / 100.0, 0, 1) * amount * 0.5f;
          image.Mutate(c => c.GaussianSharpen(detail * 0.6f));
        }
        if (preSharpen is not null)
          BlendByEdgeMask(image, preSharpen, settings.SharpenMasking / 100.0);
      } finally {
        preSharpen?.Dispose();
      }
    }
  }

  /// <summary>
  /// Sobel-style edge mask blend: where the pre-sharpen image has high
  /// gradient magnitude (edges), keep the sharpened pixel; where it's
  /// flat, fall back to the original. <paramref name="masking"/> 0 means
  /// "use sharpened everywhere", 1 means "only on the strongest edges".
  /// </summary>
  private static void BlendByEdgeMask(Image<Rgba32> sharpened, Image<Rgba32> original, double masking) {
    var w = sharpened.Width;
    var h = sharpened.Height;
    if (w < 3 || h < 3) return;

    var origPixels = new Rgba32[w * h];
    original.CopyPixelDataTo(origPixels);

    // The mask threshold rises with the Masking slider — at 1.0 only
    // pixels with a near-saturated gradient pass through.
    var threshold = 0.05 + masking * 0.45;

    sharpened.ProcessPixelRows(accessor => {
      for (var y = 1; y < accessor.Height - 1; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 1; x < row.Length - 1; x++) {
          // Sobel on luminance of the original.
          double lAbove = LuminanceOf(origPixels[(y - 1) * w + x]);
          double lBelow = LuminanceOf(origPixels[(y + 1) * w + x]);
          double lLeft  = LuminanceOf(origPixels[y * w + (x - 1)]);
          double lRight = LuminanceOf(origPixels[y * w + (x + 1)]);
          var gx = lRight - lLeft;
          var gy = lBelow - lAbove;
          var mag = Math.Sqrt(gx * gx + gy * gy);
          // SmoothStep around threshold so the mask edge doesn't pop.
          var t = Math.Clamp((mag - threshold) / 0.2, 0, 1);
          var maskWeight = t * t * (3 - 2 * t);
          if (maskWeight >= 0.999)
            continue;  // sharpened pixel survives unchanged
          var orig = origPixels[y * w + x];
          row[x] = new Rgba32(
            (byte)(orig.R + (row[x].R - orig.R) * maskWeight),
            (byte)(orig.G + (row[x].G - orig.G) * maskWeight),
            (byte)(orig.B + (row[x].B - orig.B) * maskWeight),
            row[x].A);
        }
      }
    });
  }

  private static double LuminanceOf(Rgba32 px)
    => (0.299 * px.R + 0.587 * px.G + 0.114 * px.B) / 255.0;

  /// <summary>
  /// Reweights a luminance value by four anchor amounts so highlights /
  /// shadows / whites / blacks each respond to their own slider without
  /// stomping on the others.
  /// Anchors: shadows≈0.25, highlights≈0.75, blacks=0, whites=1.
  /// </summary>
  private static double ApplyToneShifts(double lum, double highlights, double shadows, double whites, double blacks) {
    // Bell-shaped weight peaked at the anchor.
    double weight(double anchor) {
      var d = lum - anchor;
      return Math.Max(0, 1 - 16 * d * d);
    }
    var shift = 0.0;
    shift += highlights * weight(0.75) * 0.25;
    shift += shadows    * weight(0.25) * 0.25;
    shift += whites     * (lum > 0.85 ? (lum - 0.85) / 0.15 : 0) * 0.5;
    shift += blacks     * (lum < 0.15 ? (0.15 - lum) / 0.15 * -1 : 0) * 0.5;
    return lum + shift;
  }

  /// <summary>
  /// Camera-calibration nudges: small per-primary hue + saturation tweaks.
  /// Each primary's effect on a pixel is weighted by how close the pixel's
  /// hue is to that primary's anchor (R=0°, G=120°, B=240° on the wheel).
  /// </summary>
  private static void ApplyCalibrationShifts(
      ref double hue, ref double sat,
      (double Hue, double Sat) red, (double Hue, double Sat) green, (double Hue, double Sat) blue) {
    // Wheel-distance weights (1.0 at the primary's anchor, 0.0 ¹⁄₃ wheel away).
    var hueLocal = hue;
    double Weight(double anchor) {
      var d = Math.Abs(hueLocal - anchor);
      if (d > 0.5) d = 1 - d;
      return Math.Max(0, 1 - 3 * d);
    }
    var wR = Weight(0.0);       // 0°
    var wG = Weight(120.0/360); // 120°
    var wB = Weight(240.0/360); // 240°
    var hueShift = (red.Hue * wR + green.Hue * wG + blue.Hue * wB) / 360.0 * 0.15;  // ±100 -> ±5.4°
    var satScale = 1 + (red.Sat * wR + green.Sat * wG + blue.Sat * wB) / 100.0 * 0.5;
    hue = (hue + hueShift + 1) % 1;
    sat = Math.Clamp(sat * satScale, 0, 1);
  }

  /// <summary>
  /// Build the 256-entry parametric tone-curve LUT from the four lift
  /// values (Shadows / Darks / Lights / Highlights) and three split points
  /// (Shadow / Midtone / Highlight). Returns null when nothing's set.
  /// Each band's response is a SmoothStep weight peaked between its
  /// neighbouring split points; the four amounts add together at midgray
  /// for fine luminance control without hand-placing curve points.
  /// </summary>
  private static byte[]? BuildParametricLut(DevelopSettings s) {
    if (Math.Abs(s.ParametricShadows)    < 1e-6
     && Math.Abs(s.ParametricDarks)      < 1e-6
     && Math.Abs(s.ParametricLights)     < 1e-6
     && Math.Abs(s.ParametricHighlights) < 1e-6)
      return null;

    var sh = Math.Clamp(s.ParametricShadowSplit,    0, 100) / 100.0;
    var mid = Math.Clamp(s.ParametricMidtoneSplit,  0, 100) / 100.0;
    var hi = Math.Clamp(s.ParametricHighlightSplit, 0, 100) / 100.0;
    if (sh >= mid) sh = mid - 0.01;
    if (hi <= mid) hi = mid + 0.01;

    var lut = new byte[256];
    for (var i = 0; i < 256; i++) {
      var x = i / 255.0;
      // Four overlapping SmoothStep windows across the [0..1] range.
      var wShadows    = 1 - SmoothStep(x, sh - 0.05,  sh + 0.05);
      var wHighlights = SmoothStep(x, hi - 0.05, hi + 0.05);
      var wDarks      = SmoothStep(x, sh - 0.05,  sh + 0.05) - SmoothStep(x, mid - 0.05, mid + 0.05);
      var wLights     = SmoothStep(x, mid - 0.05, mid + 0.05) - SmoothStep(x, hi - 0.05,  hi + 0.05);
      var shift =
          s.ParametricShadows    * wShadows
        + s.ParametricDarks      * Math.Max(0, wDarks)
        + s.ParametricLights     * Math.Max(0, wLights)
        + s.ParametricHighlights * wHighlights;
      var y = Math.Clamp(x + shift / 100.0 * 0.4, 0, 1);
      lut[i] = (byte)Math.Round(y * 255);
    }
    return lut;
  }

  private static double SmoothStep(double x, double edge0, double edge1) {
    var t = Math.Clamp((x - edge0) / Math.Max(1e-6, edge1 - edge0), 0, 1);
    return t * t * (3 - 2 * t);
  }

  /// <summary>
  /// Adobe-style B&W conversion: start with BT.601 luminance, then bias
  /// it by each pixel's hue band weights × the matching gray-mixer
  /// slider. The eight bands match the HSL wheel anchors.
  /// </summary>
  private static double ComputeGrayscale(double r, double g, double b, double[] mixers) {
    var gray = 0.299 * r + 0.587 * g + 0.114 * b;
    RgbToHsl(r, g, b, out var hue, out var sat, out _);
    if (sat < 1e-6)
      return gray;  // already neutral, mixers don't apply
    // Two-nearest band weighting (same anchors as the HSL mixer).
    var bandIndex = 0;
    var nextIndex = 0;
    var weight = 0.0;
    for (var i = 0; i < HslBandHues.Length; i++) {
      var thisHue = HslBandHues[i];
      var nextHue = i + 1 < HslBandHues.Length ? HslBandHues[i + 1] : HslBandHues[0] + 1;
      if (hue >= thisHue && hue < nextHue) {
        bandIndex = i;
        nextIndex = (i + 1) % HslBandHues.Length;
        weight = (hue - thisHue) / (nextHue - thisHue);
        break;
      }
    }
    var mix = mixers[bandIndex] * (1 - weight) + mixers[nextIndex] * weight;
    return Math.Clamp(gray + mix / 100.0 * sat * 0.5, 0, 1);
  }

  /// <summary>
  /// Converts a Color-Grading wheel position (Hue 0..360°, Sat 0..1) to a
  /// signed RGB tint relative to neutral grey. Used inside the develop
  /// pixel loop to add a wheel's contribution weighted by the section's
  /// luminance band.
  /// </summary>
  private static (double R, double G, double B) HueSatToTint(double hueDegrees, double saturation) {
    if (saturation < 1e-6)
      return (0, 0, 0);
    var hueFrac = ((hueDegrees % 360) + 360) % 360 / 360.0;
    HslToRgb(hueFrac, 1, 0.5, out var r, out var g, out var b);
    // Centre on grey (0.5) so a 50% sat tint pushes ±0.25 from grey.
    return ((r - 0.5) * saturation, (g - 0.5) * saturation, (b - 0.5) * saturation);
  }

  /// <summary>
  /// Approximate chroma-only blur by Gaussian-blurring the whole image
  /// then restoring the original Y (BT.601) channel from the pre-blurred
  /// source. The result keeps luminance detail intact while smoothing
  /// blotchy chroma noise.
  /// </summary>
  private static void ApplyChromaBlur(Image<Rgba32> image, double amountPercent) {
    var sigma = (float)Math.Clamp(amountPercent / 30.0, 0.3, 4.0);
    using var preBlur = image.Clone();
    image.Mutate(c => c.GaussianBlur(sigma));
    // Restore Y from preBlur into the blurred image.
    image.ProcessPixelRows(preBlur, (post, pre) => {
      for (var y = 0; y < post.Height; y++) {
        var postRow = post.GetRowSpan(y);
        var preRow = pre.GetRowSpan(y);
        for (var x = 0; x < postRow.Length; x++) {
          var pr = preRow[x].R / 255.0;
          var pg = preRow[x].G / 255.0;
          var pb = preRow[x].B / 255.0;
          var qr = postRow[x].R / 255.0;
          var qg = postRow[x].G / 255.0;
          var qb = postRow[x].B / 255.0;
          var preY  = 0.299 * pr + 0.587 * pg + 0.114 * pb;
          var postY = 0.299 * qr + 0.587 * qg + 0.114 * qb;
          var diff  = preY - postY;
          postRow[x] = new Rgba32(
            ToByte(qr + diff), ToByte(qg + diff), ToByte(qb + diff), preRow[x].A);
        }
      }
    });
  }

  /// <summary>
  /// Centre hues (0..1, fraction of the colour wheel) for the 8 HSL bands —
  /// Red / Orange / Yellow / Green / Aqua / Blue / Purple / Magenta. Adobe
  /// uses the same anchor positions; matching them keeps Lightroom-shared
  /// edits visually consistent.
  /// </summary>
  private static readonly double[] HslBandHues = {
    0.0,        // Red       (0°)
    30.0/360,   // Orange    (30°)
    60.0/360,   // Yellow    (60°)
    120.0/360,  // Green     (120°)
    180.0/360,  // Aqua      (180°)
    240.0/360,  // Blue      (240°)
    270.0/360,  // Purple    (270°)
    300.0/360   // Magenta   (300°)
  };

  /// <summary>Returns null when every band is zero — saves the pixel loop a redundant pass.</summary>
  private static IReadOnlyList<double>? NormaliseBandList(IReadOnlyList<double>? source) {
    if (source is null || source.Count == 0)
      return null;
    var any = false;
    for (var i = 0; i < source.Count && i < 8; i++)
      if (Math.Abs(source[i]) > 1e-6) { any = true; break; }
    return any ? source : null;
  }

  private static void ApplyHslBandShifts(
      ref double hue, ref double sat, ref double lum,
      IReadOnlyList<double>? hueShifts, IReadOnlyList<double>? satShifts, IReadOnlyList<double>? lumShifts) {
    // Find the two nearest bands and a 0..1 weight between them.
    var bandIndex = 0;
    var nextIndex = 0;
    var weight = 0.0;
    for (var i = 0; i < HslBandHues.Length; i++) {
      var thisHue = HslBandHues[i];
      var nextHue = i + 1 < HslBandHues.Length ? HslBandHues[i + 1] : HslBandHues[0] + 1;
      if (hue >= thisHue && hue < nextHue) {
        bandIndex = i;
        nextIndex = (i + 1) % HslBandHues.Length;
        weight = (hue - thisHue) / (nextHue - thisHue);
        break;
      }
    }
    // Hue values smaller than band 0 (rare wrap-around) — fall through to the magenta→red wedge.
    if (hue < HslBandHues[0]) {
      bandIndex = 7;
      nextIndex = 0;
      var startHue = HslBandHues[7] - 1;
      weight = (hue - startHue) / (HslBandHues[0] - startHue);
    }

    var hShift = Lerp(hueShifts, bandIndex, nextIndex, weight) * 30.0 / 360.0; // ±100 -> ±30°
    var sShift = Lerp(satShifts, bandIndex, nextIndex, weight) / 100.0;       // ±100 -> ±100% relative
    var lShift = Lerp(lumShifts, bandIndex, nextIndex, weight) / 100.0;       // ±100 -> ±50% lightness shift

    hue = (hue + hShift + 1) % 1;            // wrap into 0..1
    sat = Math.Clamp(sat * (1 + sShift), 0, 1);
    // Luminance moves toward black (lShift < 0) or white (lShift > 0) by half the user weight.
    lum = lShift >= 0
      ? lum + (1 - lum) * lShift * 0.5
      : lum + lum * lShift * 0.5;
    lum = Math.Clamp(lum, 0, 1);
  }

  private static double Lerp(IReadOnlyList<double>? values, int a, int b, double t) {
    if (values is null)
      return 0;
    var va = a < values.Count ? values[a] : 0;
    var vb = b < values.Count ? values[b] : 0;
    return va + (vb - va) * t;
  }

  private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l) {
    var max = Math.Max(r, Math.Max(g, b));
    var min = Math.Min(r, Math.Min(g, b));
    l = (max + min) * 0.5;
    if (Math.Abs(max - min) < 1e-9) { h = 0; s = 0; return; }
    var d = max - min;
    s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    if (max == r)      h = ((g - b) / d + (g < b ? 6 : 0)) / 6.0;
    else if (max == g) h = ((b - r) / d + 2) / 6.0;
    else               h = ((r - g) / d + 4) / 6.0;
  }

  private static void HslToRgb(double h, double s, double l, out double r, out double g, out double b) {
    if (s < 1e-9) { r = g = b = l; return; }
    var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
    var p = 2 * l - q;
    r = HueToRgbComponent(p, q, h + 1.0/3);
    g = HueToRgbComponent(p, q, h);
    b = HueToRgbComponent(p, q, h - 1.0/3);
  }

  private static double HueToRgbComponent(double p, double q, double t) {
    if (t < 0) t += 1;
    if (t > 1) t -= 1;
    if (t < 1.0/6) return p + (q - p) * 6 * t;
    if (t < 1.0/2) return q;
    if (t < 2.0/3) return p + (q - p) * (2.0/3 - t) * 6;
    return p;
  }

  private static byte ToByte(double v) {
    if (v <= 0) return 0;
    if (v >= 1) return 255;
    return (byte)Math.Round(v * 255);
  }

  private static byte ClampToByte(double v) {
    if (v <= 0) return 0;
    if (v >= 1) return 255;
    return (byte)Math.Round(v * 255);
  }

  /// <summary>
  /// Build a 256-entry lookup table from the tone curve's control points
  /// using the requested interpolation mode. Returns null when the curve
  /// is identity so the per-pixel loop can short-circuit.
  /// </summary>
  public static byte[]? BuildCurveLut(IReadOnlyList<CurvePoint>? points, CurveInterpolation mode = CurveInterpolation.Linear) {
    if (points is null || points.Count < 2)
      return null;

    var sorted = points.OrderBy(p => p.X).ToArray();
    // Anchor the curve at 0 and 1 so a user-placed midpoint doesn't cause
    // weird clipping near the ends.
    if (sorted[0].X > 0)
      sorted = new[] { new CurvePoint(0, sorted[0].Y) }.Concat(sorted).ToArray();
    if (sorted[^1].X < 1)
      sorted = sorted.Concat(new[] { new CurvePoint(1, sorted[^1].Y) }).ToArray();

    if (sorted.All(p => Math.Abs(p.X - p.Y) < 1e-6))
      return null;

    Func<double, double> sampler = mode switch {
      CurveInterpolation.CatmullRom => x => SampleCatmullRom(sorted, x),
      CurveInterpolation.Bezier     => x => SampleBezier    (sorted, x),
      _                             => x => SampleLinear    (sorted, x)
    };

    var lut = new byte[256];
    for (var i = 0; i < 256; i++)
      lut[i] = (byte)Math.Round(Math.Clamp(sampler(i / 255.0), 0, 1) * 255);
    return lut;
  }

  private static double SampleLinear(CurvePoint[] sorted, double x) {
    var k = 0;
    while (k < sorted.Length - 1 && sorted[k + 1].X < x) k++;
    var lo = sorted[k];
    var hi = k + 1 < sorted.Length ? sorted[k + 1] : sorted[^1];
    if (Math.Abs(hi.X - lo.X) < 1e-9) return lo.Y;
    var t = (x - lo.X) / (hi.X - lo.X);
    return lo.Y + (hi.Y - lo.Y) * t;
  }

  /// <summary>
  /// Centripetal Catmull-Rom spline through every control point. Tangent
  /// at each interior anchor is the average of the slopes to neighbours;
  /// at the ends we fall back to the slope of the adjacent segment so
  /// the curve still meets the (0,0)/(1,1) anchors smoothly.
  /// </summary>
  private static double SampleCatmullRom(CurvePoint[] sorted, double x) {
    var k = 0;
    while (k < sorted.Length - 1 && sorted[k + 1].X < x) k++;
    if (k >= sorted.Length - 1)
      return sorted[^1].Y;

    var p1 = sorted[k];
    var p2 = sorted[k + 1];
    var p0 = k > 0 ? sorted[k - 1] : new CurvePoint(p1.X - (p2.X - p1.X), p1.Y - (p2.Y - p1.Y));
    var p3 = k + 2 < sorted.Length ? sorted[k + 2] : new CurvePoint(p2.X + (p2.X - p1.X), p2.Y + (p2.Y - p1.Y));

    if (Math.Abs(p2.X - p1.X) < 1e-9)
      return p1.Y;
    var t = Math.Clamp((x - p1.X) / (p2.X - p1.X), 0, 1);
    var t2 = t * t;
    var t3 = t2 * t;
    return 0.5 * (
      (2 * p1.Y) +
      (-p0.Y + p2.Y) * t +
      (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 +
      (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3
    );
  }

  /// <summary>
  /// Cubic Bezier with control points derived from Catmull-Rom tangents:
  /// the segment between p1 and p2 uses handles at p1 + (p2 - p0) / 6 and
  /// p2 - (p3 - p1) / 6. Slightly looser than the pure spline; users
  /// typically pick this for steeper "punchy" tone curves.
  /// </summary>
  private static double SampleBezier(CurvePoint[] sorted, double x) {
    var k = 0;
    while (k < sorted.Length - 1 && sorted[k + 1].X < x) k++;
    if (k >= sorted.Length - 1)
      return sorted[^1].Y;

    var p1 = sorted[k];
    var p2 = sorted[k + 1];
    var p0 = k > 0 ? sorted[k - 1] : p1;
    var p3 = k + 2 < sorted.Length ? sorted[k + 2] : p2;

    var dx = p2.X - p1.X;
    if (Math.Abs(dx) < 1e-9)
      return p1.Y;

    var c1 = new CurvePoint(p1.X + dx / 3.0, p1.Y + (p2.Y - p0.Y) / 6.0);
    var c2 = new CurvePoint(p2.X - dx / 3.0, p2.Y - (p3.Y - p1.Y) / 6.0);

    // Solve t for which Bezier_x(t) == x via 6 Newton iterations — the
    // x mapping is monotonic for a sane curve so this converges fast.
    var t = (x - p1.X) / dx;
    for (var iter = 0; iter < 6; iter++) {
      var u = 1 - t;
      var bx = u * u * u * p1.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * p2.X;
      var dbx = 3 * u * u * (c1.X - p1.X) + 6 * u * t * (c2.X - c1.X) + 3 * t * t * (p2.X - c2.X);
      if (Math.Abs(dbx) < 1e-9) break;
      t -= (bx - x) / dbx;
      t = Math.Clamp(t, 0, 1);
    }
    var u2 = 1 - t;
    return u2 * u2 * u2 * p1.Y + 3 * u2 * u2 * t * c1.Y + 3 * u2 * t * t * c2.Y + t * t * t * p2.Y;
  }

  private static RotateMode NormalizeRotation(int degrees) {
    var norm = ((degrees % 360) + 360) % 360;
    return norm switch {
      90 => RotateMode.Rotate90,
      180 => RotateMode.Rotate180,
      270 => RotateMode.Rotate270,
      _ => RotateMode.None
    };
  }

  /// Watermark layer: white text + 1 px black outline at the chosen corner /
  /// centre, blended with WatermarkOpacity. Tries Arial first, then any
  /// installed sans-serif, then the first system family. Failure is silent —
  /// a missing font system shouldn't punch a hole in the develop pipeline.
  private static void ApplyWatermark(Image<Rgba32> image, DevelopSettings settings) {
    if (string.IsNullOrEmpty(settings.WatermarkText))
      return;
    var opacity = Math.Clamp(settings.WatermarkOpacity, 0, 1);
    if (opacity < 1e-6)
      return;
    if (settings.WatermarkFontSize <= 0)
      return;

    Font? font;
    try {
      font = ResolveWatermarkFont(settings.WatermarkFontSize);
    } catch {
      return;
    }
    if (font is null)
      return;

    FontRectangle bounds;
    try {
      bounds = TextMeasurer.MeasureBounds(settings.WatermarkText!, new TextOptions(font));
    } catch {
      return;
    }

    var margin = Math.Max(8, settings.WatermarkFontSize / 3);
    var (px, py) = ComputeWatermarkOrigin(settings.WatermarkPosition, image.Width, image.Height, bounds, margin);

    var fillAlpha = (byte)Math.Round(255 * opacity);
    var outlineAlpha = (byte)Math.Round(200 * opacity);
    var fill = Color.FromRgba(255, 255, 255, fillAlpha);
    var outline = Color.FromRgba(0, 0, 0, outlineAlpha);

    try {
      image.Mutate(c => c.DrawText(settings.WatermarkText!, font!, fill, new PointF(px, py)));
      image.Mutate(c => c.DrawText(settings.WatermarkText!, font!, Pens.Solid(outline, 1f), new PointF(px, py)));
    } catch {
      // Glyph / shaping failure — keep going with the un-watermarked image.
    }
  }

  private static (float X, float Y) ComputeWatermarkOrigin(
    string position, int width, int height, FontRectangle bounds, int margin) {
    var w = bounds.Width;
    var h = bounds.Height;
    return position switch {
      "TopLeft"     => (margin,                 margin),
      "TopRight"    => (width  - w - margin,    margin),
      "BottomLeft"  => (margin,                 height - h - margin),
      "Center"      => ((width - w) * 0.5f,     (height - h) * 0.5f),
      _             => (width  - w - margin,    height - h - margin)
    };
  }

  private static Font? ResolveWatermarkFont(int size) {
    if (size <= 0) return null;
    if (SystemFonts.TryGet("Arial", out var arial))
      return arial.CreateFont(size);
    foreach (var family in SystemFonts.Families) {
      var name = (family.Name ?? string.Empty).ToLowerInvariant();
      if (name.Contains("sans") || name.Contains("dejavu") || name.Contains("liberation") || name.Contains("noto"))
        return family.CreateFont(size);
    }
    foreach (var family in SystemFonts.Families)
      return family.CreateFont(size);
    return null;
  }
}
