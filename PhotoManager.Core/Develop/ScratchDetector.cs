using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Develop;

/// <summary>
/// Auto-detect scratches and tears in scanned old photos via Frangi
/// vesselness — a Hessian-based ridge filter that's the gold standard
/// for thin curvilinear feature detection (originally invented for
/// blood vessels in medical imaging, but the math applies one-to-one
/// to scratches in photos). Output is a mask in the same shape the
/// brush-paint flow produces, so the inpaint pipeline (LaMa) can run
/// against it without further conversion. Pure C# — no OpenCV
/// dependency, so the detector runs anywhere the rest of the app runs.
///
/// Why Frangi instead of morphological top-hat:
///   The top-hat approach (which earlier versions used) produces strong
///   responses for "bright on dark" features but collapses on
///   "bright on bright" — e.g. a paper-crease scratch crossing a face.
///   Frangi looks at the local 2nd-derivative structure: a true scratch
///   has a strong second derivative perpendicular to its direction and
///   a near-zero one along its length, regardless of absolute brightness.
///
/// How it works:
///   1. Convert source to luminance.
///   2. Gaussian-smooth at the configured scale σ.
///   3. Compute the Hessian (∂²/∂x², ∂²/∂y², ∂²/∂x∂y) per pixel.
///   4. Per pixel, compute eigenvalues λ₁, λ₂ (|λ₁| ≤ |λ₂|).
///   5. Frangi vesselness:
///        V = exp(-(λ₁/λ₂)² / (2β²)) · (1 - exp(-S² / (2c²)))
///      where S = sqrt(λ₁² + λ₂²) is the "structureness". Bright
///      scratches need λ₂ &lt; 0; dark scratches λ₂ &gt; 0.
///   6. Threshold response (relative to its max).
///   7. Morphological closing to bridge tiny gaps.
///   8. Connected-component pruning by area + bounding-box aspect
///      ratio (true scratches are long-and-thin; eye / nostril blobs
///      that survive the ridge filter sit closer to a square box).
///
/// The user can still refine the auto-mask with the brush before
/// clicking Inpaint — auto-detect rarely catches everything but is a
/// reliable starting point that saves most of the painting work.
/// </summary>
public static class ScratchDetector {
  /// <summary>
  /// Run the detector against <paramref name="source"/> and return an
  /// Rgba32 mask the same dimensions as the input. Mask convention
  /// matches the brush-paint flow: <c>R = 255</c> at "inpaint here",
  /// alpha 200 for the semi-transparent overlay rendering, fully
  /// transparent (R = 0, A = 0) elsewhere.
  /// </summary>
  public static Image<Rgba32> Detect(Image<Rgba32> source, ScratchDetectorOptions? options = null) {
    ArgumentNullException.ThrowIfNull(source);
    options ??= new ScratchDetectorOptions();
    var w = source.Width;
    var h = source.Height;

    // Step 1 — luminance.
    var gray = ToLuminance(source);

    // Step 2 — preprocessing to boost local contrast for sub-10-luma
    // hairline scratches that would otherwise be near-noise-floor.
    //   * UseHighPass: subtract a heavy-Gaussian-blurred copy. Boosts
    //     contrast for features at the Frangi scale without amplifying
    //     fine texture (curtains, fabric, paper grain) the way CLAHE
    //     does. Best default.
    //   * UseClahe: contrast-limited adaptive histogram equalisation.
    //     Stronger global lift but amplifies textured areas heavily,
    //     producing many false positives on portraits with patterned
    //     backgrounds. Off by default.
    if (options.UseHighPass)
      gray = HighPassBoost(gray, w, h, options.HighPassSigma, options.HighPassStrength);
    if (options.UseClahe)
      gray = ApplyClahe(gray, w, h, options.ClaheTileSize, options.ClaheClipLimit);

    // Step 3 — multi-scale Frangi. Single-scale Frangi only catches
    // scratches near a specific width; running at several sigmas
    // (catches 1-px hairlines AND chunky paper-crease tears) and
    // taking the pixel-wise max produces a much richer response map.
    float[] response;
    if (options.MultiScale) {
      response = new float[w * h];
      foreach (var sigma in new[] { 1.0, 1.5, 2.5 }) {
        var smoothed = GaussianSmooth(gray, w, h, sigma);
        var partial = FrangiResponse(smoothed, w, h, options.Beta, options.C, options.DetectDarkScratches);
        for (var p = 0; p < response.Length; p++)
          if (partial[p] > response[p])
            response[p] = partial[p];
      }
    } else {
      var smoothed = GaussianSmooth(gray, w, h, options.Sigma);
      response = FrangiResponse(smoothed, w, h, options.Beta, options.C, options.DetectDarkScratches);
    }

    // Step 4 — threshold. Use the 99th-percentile response as the
    // "strong response" anchor (the max gets dominated by a single
    // very-strong feature and pushes the cutoff too high). Lower
    // cutoff = more thorough at the cost of false positives.
    var sorted = (float[])response.Clone();
    Array.Sort(sorted);
    var p99Index = Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99));
    var p99 = sorted[p99Index];
    var cutoff = p99 * options.ResponseCutoff;
    var binary = new byte[w * h];
    for (var p = 0; p < response.Length; p++)
      binary[p] = response[p] >= cutoff ? (byte)255 : (byte)0;

    // Step 7 — close + dilate. Closing bridges 1-2 pixel gaps along a
    // scratch broken by ridge-filter noise; the extra dilation widens
    // each detected scratch into a brush stroke wide enough that LaMa
    // can sample meaningful surrounding context.
    binary = Dilate3x3(binary, w, h);
    binary = Dilate3x3(binary, w, h);

    // Step 8 — drop blobs that are short OR not elongated. True
    // scratches are long-and-thin (high aspect ratio). Eye sockets and
    // nostrils that survive the ridge filter sit closer to a square
    // bounding box. Dropping low-aspect-ratio components removes
    // most portrait false positives.
    if (options.MinComponentArea > 0 || options.MinAspectRatio > 1)
      binary = PruneNonScratchComponents(binary, w, h, options.MinComponentArea, options.MinAspectRatio);

    // Step 9 (optional) — suppress detections in skin-toned regions.
    if (options.SkinToneSuppress)
      SuppressSkinTone(binary, source, w, h);

    return BinaryToRgbaMask(binary, w, h);
  }

  // ---------- High-pass preprocessing ----------

  /// <summary>
  /// Subtract a heavily-blurred copy of the image from the original
  /// (with strength factor) to flatten large-scale luminance changes
  /// while preserving Frangi-scale detail. Result: sub-10-luma scratches
  /// across bright skin become as visible as bright-on-dark scratches —
  /// exactly the regime Frangi handles best.
  /// </summary>
  private static byte[] HighPassBoost(byte[] src, int w, int h, double sigma, double strength) {
    if (sigma <= 0) return (byte[])src.Clone();
    var blur = GaussianSmooth(src, w, h, sigma);
    var dst = new byte[src.Length];
    for (var p = 0; p < src.Length; p++) {
      // High-pass = src - blur. Add back the blurred mean (128 here for
      // simplicity) so the result is centred around mid-gray. Strength
      // amplifies the high-pass before the recombine.
      var hp = (src[p] - blur[p]) * strength;
      dst[p] = (byte)Math.Clamp(Math.Round(128 + hp), 0, 255);
    }
    return dst;
  }

  // ---------- CLAHE ----------

  /// <summary>
  /// Contrast-Limited Adaptive Histogram Equalization. Divides the
  /// image into <paramref name="tileSize"/> × <paramref name="tileSize"/>
  /// non-overlapping tiles, equalizes each tile's histogram (clipping
  /// per-bin to <paramref name="clipLimit"/> · pixelsPerTile to prevent
  /// noise amplification), then bilinearly interpolates between the
  /// 4 neighbouring tile-CDFs at every pixel so the result is smooth
  /// across tile boundaries. Dramatically boosts local contrast around
  /// faint scratches without crushing global tonality.
  /// </summary>
  private static byte[] ApplyClahe(byte[] src, int w, int h, int tileSize, double clipLimit) {
    var tilesX = Math.Max(1, w / tileSize);
    var tilesY = Math.Max(1, h / tileSize);
    var tileW = w / tilesX;
    var tileH = h / tilesY;
    var pixelsPerTile = tileW * tileH;
    var clipPixels = (int)Math.Max(1, clipLimit * pixelsPerTile / 256.0);

    // Per-tile cumulative distribution function (CDF) → 256-byte LUT.
    var luts = new byte[tilesY, tilesX][];
    for (var ty = 0; ty < tilesY; ty++) {
      for (var tx = 0; tx < tilesX; tx++) {
        var x0 = tx * tileW;
        var y0 = ty * tileH;
        var x1 = (tx == tilesX - 1) ? w : x0 + tileW;
        var y1 = (ty == tilesY - 1) ? h : y0 + tileH;

        // Build histogram for this tile.
        var hist = new int[256];
        for (var y = y0; y < y1; y++)
          for (var x = x0; x < x1; x++)
            hist[src[y * w + x]]++;

        // Clip + redistribute excess uniformly across all bins. This
        // is the "contrast-limited" part; without it noise gets
        // amplified to image-distorting levels.
        long excess = 0;
        for (var i = 0; i < 256; i++)
          if (hist[i] > clipPixels) {
            excess += hist[i] - clipPixels;
            hist[i] = clipPixels;
          }
        var redistribute = (int)(excess / 256);
        var leftover = (int)(excess % 256);
        for (var i = 0; i < 256; i++) hist[i] += redistribute;
        for (var i = 0; i < leftover; i++) hist[i]++;

        // CDF → normalized LUT.
        var totalPixels = (x1 - x0) * (y1 - y0);
        var lut = new byte[256];
        long cumul = 0;
        for (var i = 0; i < 256; i++) {
          cumul += hist[i];
          lut[i] = (byte)Math.Clamp(255L * cumul / Math.Max(1, totalPixels), 0L, 255L);
        }
        luts[ty, tx] = lut;
      }
    }

    // Apply with bilinear interpolation between the 4 neighbouring
    // tile centres so output is seamless across tile boundaries.
    var dst = new byte[src.Length];
    for (var y = 0; y < h; y++) {
      // Tile-coordinate y in [0, tilesY-1] space, with fractional offset.
      var fy = (y + 0.5) / tileH - 0.5;
      var ty0 = (int)Math.Floor(fy);
      var ty1 = ty0 + 1;
      var dy = fy - ty0;
      ty0 = Math.Clamp(ty0, 0, tilesY - 1);
      ty1 = Math.Clamp(ty1, 0, tilesY - 1);

      for (var x = 0; x < w; x++) {
        var fx = (x + 0.5) / tileW - 0.5;
        var tx0 = (int)Math.Floor(fx);
        var tx1 = tx0 + 1;
        var dxv = fx - tx0;
        tx0 = Math.Clamp(tx0, 0, tilesX - 1);
        tx1 = Math.Clamp(tx1, 0, tilesX - 1);

        var v = src[y * w + x];
        var v00 = luts[ty0, tx0][v];
        var v01 = luts[ty0, tx1][v];
        var v10 = luts[ty1, tx0][v];
        var v11 = luts[ty1, tx1][v];
        var top = v00 * (1 - dxv) + v01 * dxv;
        var bot = v10 * (1 - dxv) + v11 * dxv;
        dst[y * w + x] = (byte)Math.Clamp(Math.Round(top * (1 - dy) + bot * dy), 0, 255);
      }
    }
    return dst;
  }

  // ---------- Frangi vesselness ----------

  /// <summary>
  /// Per-pixel Frangi vesselness response. Computes the Hessian via
  /// finite differences on the smoothed luminance, then evaluates the
  /// Frangi (1998) tube-likeness measure on its eigenvalues:
  ///   V = exp(-(λ₁/λ₂)² / (2β²)) · (1 − exp(−S² / (2c²)))
  /// where S = sqrt(λ₁² + λ₂²) and (λ₁, λ₂) are the eigenvalues sorted
  /// by absolute value (|λ₁| ≤ |λ₂|). Bright scratches require λ₂ &lt; 0
  /// (the second derivative across the ridge is negative); dark
  /// scratches need λ₂ &gt; 0.
  /// </summary>
  private static float[] FrangiResponse(byte[] gray, int w, int h, double beta, double c, bool darkScratches) {
    var response = new float[w * h];
    var beta2 = 2.0 * beta * beta;
    var c2 = 2.0 * c * c;

    for (var y = 1; y < h - 1; y++) {
      for (var x = 1; x < w - 1; x++) {
        var p = y * w + x;
        // Central differences for the Hessian. Working in float so a
        // dark→bright transition gives a positive ∂²/∂x², bright→dark
        // gives negative — exactly what Frangi expects.
        double Hxx = gray[p + 1]      - 2.0 * gray[p] + gray[p - 1];
        double Hyy = gray[p + w]      - 2.0 * gray[p] + gray[p - w];
        double Hxy = (gray[p - w - 1] - gray[p - w + 1] - gray[p + w - 1] + gray[p + w + 1]) / 4.0;

        // Eigenvalues of [[Hxx, Hxy], [Hxy, Hyy]] = (T ± sqrt(T² - 4D)) / 2
        // where T = trace = Hxx + Hyy and D = det = Hxx·Hyy - Hxy².
        var trace = Hxx + Hyy;
        var det = Hxx * Hyy - Hxy * Hxy;
        var disc = trace * trace - 4.0 * det;
        if (disc < 0) disc = 0;
        var rt = Math.Sqrt(disc);
        var ev1 = (trace + rt) / 2.0;
        var ev2 = (trace - rt) / 2.0;

        // Sort by absolute value: |λ₁| ≤ |λ₂|.
        double l1, l2;
        if (Math.Abs(ev1) <= Math.Abs(ev2)) { l1 = ev1; l2 = ev2; }
        else                                 { l1 = ev2; l2 = ev1; }

        // For BRIGHT scratches the dominant eigenvalue across the ridge
        // is NEGATIVE (image curves down on either side of the ridge).
        // For DARK scratches it's positive. Skip the wrong polarity.
        if (!darkScratches && l2 >= 0) continue;
        if (darkScratches && l2 <= 0) continue;
        if (Math.Abs(l2) < 1e-6) continue;

        var Rb = l1 / l2;
        var S = Math.Sqrt(l1 * l1 + l2 * l2);
        var v = Math.Exp(-Rb * Rb / beta2) * (1.0 - Math.Exp(-S * S / c2));
        response[p] = (float)v;
      }
    }
    return response;
  }

  /// <summary>
  /// Separable Gaussian smoothing, σ in pixels. Kernel half-width
  /// = ceil(3σ). Edges use clamping (replicate-border).
  /// </summary>
  private static byte[] GaussianSmooth(byte[] src, int w, int h, double sigma) {
    if (sigma <= 0) return (byte[])src.Clone();
    var halfWidth = (int)Math.Ceiling(3.0 * sigma);
    var kernelSize = 2 * halfWidth + 1;
    var kernel = new double[kernelSize];
    var twoSigma2 = 2.0 * sigma * sigma;
    var sum = 0.0;
    for (var i = 0; i < kernelSize; i++) {
      var x = i - halfWidth;
      kernel[i] = Math.Exp(-x * x / twoSigma2);
      sum += kernel[i];
    }
    for (var i = 0; i < kernelSize; i++)
      kernel[i] /= sum;

    // Horizontal pass.
    var tmp = new double[w * h];
    for (var y = 0; y < h; y++) {
      for (var x = 0; x < w; x++) {
        var acc = 0.0;
        for (var k = 0; k < kernelSize; k++) {
          var sx = Math.Clamp(x + k - halfWidth, 0, w - 1);
          acc += kernel[k] * src[y * w + sx];
        }
        tmp[y * w + x] = acc;
      }
    }
    // Vertical pass.
    var dst = new byte[w * h];
    for (var y = 0; y < h; y++) {
      for (var x = 0; x < w; x++) {
        var acc = 0.0;
        for (var k = 0; k < kernelSize; k++) {
          var sy = Math.Clamp(y + k - halfWidth, 0, h - 1);
          acc += kernel[k] * tmp[sy * w + x];
        }
        dst[y * w + x] = (byte)Math.Clamp(Math.Round(acc), 0, 255);
      }
    }
    return dst;
  }

  // ---------- Morphology ----------

  /// <summary>3×3 dilation — fattens detected scratches into a brush stroke and bridges tiny gaps.</summary>
  private static byte[] Dilate3x3(byte[] src, int w, int h) {
    var dst = new byte[src.Length];
    for (var y = 0; y < h; y++) {
      for (var x = 0; x < w; x++) {
        byte maxVal = 0;
        for (var dy = -1; dy <= 1; dy++) {
          var sy = y + dy;
          if (sy < 0 || sy >= h) continue;
          for (var dx = -1; dx <= 1; dx++) {
            var sx = x + dx;
            if (sx < 0 || sx >= w) continue;
            var v = src[sy * w + sx];
            if (v > maxVal) maxVal = v;
          }
        }
        dst[y * w + x] = maxVal;
      }
    }
    return dst;
  }

  // ---------- Connected components ----------

  /// <summary>
  /// Drop binary blobs that are too small OR not elongated enough to
  /// be plausible scratches. Two-pass union-find labelling + bounding-
  /// box accumulation. Aspect ratio = max(boxW, boxH) / min(boxW, boxH);
  /// scratches are essentially 1D so true scratches have ratio ≫ 1
  /// while non-scratch blobs (eyes, hair clumps) sit closer to 1.
  /// </summary>
  private static byte[] PruneNonScratchComponents(byte[] src, int w, int h, int minArea, double minAspectRatio) {
    var labels = new int[src.Length];
    var parent = new List<int> { 0 };
    var nextLabel = 1;

    int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
    void Union(int a, int b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra] = rb; }

    for (var y = 0; y < h; y++) {
      for (var x = 0; x < w; x++) {
        var p = y * w + x;
        if (src[p] == 0) continue;
        var left = x > 0 ? labels[p - 1] : 0;
        var up   = y > 0 ? labels[p - w] : 0;
        if (left != 0 && up != 0) {
          labels[p] = left;
          Union(left, up);
        } else if (left != 0) {
          labels[p] = left;
        } else if (up != 0) {
          labels[p] = up;
        } else {
          labels[p] = nextLabel;
          parent.Add(nextLabel);
          nextLabel++;
        }
      }
    }

    // Per-component: count area + accumulate bounding box.
    var area = new Dictionary<int, int>();
    var minX = new Dictionary<int, int>();
    var minY = new Dictionary<int, int>();
    var maxX = new Dictionary<int, int>();
    var maxY = new Dictionary<int, int>();
    for (var y = 0; y < h; y++) {
      for (var x = 0; x < w; x++) {
        var p = y * w + x;
        if (labels[p] == 0) continue;
        var r = Find(labels[p]);
        labels[p] = r;
        area[r] = area.GetValueOrDefault(r) + 1;
        if (!minX.ContainsKey(r)) { minX[r] = x; minY[r] = y; maxX[r] = x; maxY[r] = y; }
        else {
          if (x < minX[r]) minX[r] = x;
          if (y < minY[r]) minY[r] = y;
          if (x > maxX[r]) maxX[r] = x;
          if (y > maxY[r]) maxY[r] = y;
        }
      }
    }

    // Decide which components survive.
    var keep = new HashSet<int>();
    foreach (var (r, a) in area) {
      if (a < minArea) continue;
      var boxW = maxX[r] - minX[r] + 1;
      var boxH = maxY[r] - minY[r] + 1;
      var ratio = (double)Math.Max(boxW, boxH) / Math.Max(1, Math.Min(boxW, boxH));
      if (ratio < minAspectRatio) continue;
      keep.Add(r);
    }

    var dst = new byte[src.Length];
    for (var p = 0; p < labels.Length; p++)
      if (labels[p] != 0 && keep.Contains(labels[p]))
        dst[p] = 255;
    return dst;
  }

  // ---------- Skin-tone suppression ----------

  /// <summary>
  /// Zero out mask pixels that overlap skin-toned regions. Cheap
  /// HSV-range test (hue 0–35° + 330–360°, moderate saturation, fair
  /// luminance) catches typical caucasian / asian / hispanic skin
  /// without needing a face detector. Reduces false positives along
  /// hair strands / eyebrows in portrait scans.
  /// </summary>
  private static void SuppressSkinTone(byte[] mask, Image<Rgba32> source, int w, int h) {
    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var p = y * w + x;
          if (mask[p] == 0) continue;
          var px = row[x];
          // Quick skin-tone gate: R > G > B, R-B > 15, R > 95, G > 40, B > 20.
          if (px.R > 95 && px.G > 40 && px.B > 20
              && px.R > px.G && px.G > px.B
              && px.R - px.B > 15
              && Math.Abs(px.R - px.G) > 15)
            mask[p] = 0;
        }
      }
    });
  }

  // ---------- Format conversion ----------

  private static byte[] ToLuminance(Image<Rgba32> source) {
    var w = source.Width;
    var h = source.Height;
    var gray = new byte[w * h];
    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var px = row[x];
          // Rec. 709 luma — same convention used elsewhere in the pipeline.
          gray[y * w + x] = (byte)Math.Round(0.2126f * px.R + 0.7152f * px.G + 0.0722f * px.B);
        }
      }
    });
    return gray;
  }

  private static Image<Rgba32> BinaryToRgbaMask(byte[] binary, int w, int h) {
    var image = new Image<Rgba32>(w, h);
    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < h; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          // Match the brush canvas convention: red, half-transparent
          // when masked; fully transparent black elsewhere.
          row[x] = binary[y * w + x] != 0
            ? new Rgba32((byte)255, (byte)0, (byte)0, (byte)200)
            : default;
      }
    });
    return image;
  }
}

/// <summary>
/// Tunables for <see cref="ScratchDetector.Detect"/>. Defaults are
/// calibrated against typical 1024–2000-pixel-edge scanned old photos.
/// </summary>
public sealed record ScratchDetectorOptions {
  /// <summary>
  /// Apply a heavy-Gaussian high-pass before Frangi (subtract a
  /// strongly-blurred copy from the original, recombine around 128).
  /// Flattens large-scale luminance variation so scratches across
  /// bright skin / sky become as visible to Frangi as scratches on
  /// dark backgrounds. Default on; turn off for already-flat sources.
  /// </summary>
  public bool UseHighPass { get; init; } = true;

  /// <summary>Sigma for the high-pass blur (large = preserves more detail; smaller = stronger flattening).</summary>
  public double HighPassSigma { get; init; } = 12.0;

  /// <summary>How much of the high-pass to add back. 1.0 = identity high-pass, &gt;1 boosts local detail.</summary>
  public double HighPassStrength { get; init; } = 1.5;

  /// <summary>
  /// Apply CLAHE (Contrast-Limited Adaptive Histogram Equalization)
  /// before Frangi runs. Stronger contrast lift than the high-pass but
  /// amplifies fine texture (curtains, fabric, paper grain) into false
  /// positives. Off by default.
  /// </summary>
  public bool UseClahe { get; init; } = false;

  /// <summary>CLAHE tile size in pixels. Smaller = more localized contrast (catches subtler scratches but boosts noise).</summary>
  public int ClaheTileSize { get; init; } = 32;

  /// <summary>
  /// CLAHE clip limit (per-bin amplification ceiling, in multiples of
  /// the uniform per-bin count). Larger = stronger contrast boost,
  /// more noise amplification.
  /// </summary>
  public double ClaheClipLimit { get; init; } = 4.0;

  /// <summary>
  /// Run Frangi at multiple sigmas (1.0, 1.5, 2.5) and take the
  /// per-pixel maximum. Catches scratches of varying widths in a
  /// single pass. Off = single-scale at <see cref="Sigma"/>.
  /// </summary>
  public bool MultiScale { get; init; } = true;

  /// <summary>
  /// Gaussian smoothing scale (in pixels), used when
  /// <see cref="MultiScale"/> is off. Picks the width of features the
  /// Frangi filter responds to most strongly. ≈1.0 catches 1–2-px
  /// hairline scratches; bump to 2-3 for chunkier paper-crease tears.
  /// </summary>
  public double Sigma { get; init; } = 1.2;

  /// <summary>
  /// Frangi β — controls how strongly the filter discriminates between
  /// blob-like and tube-like structures. Smaller = stricter (only very
  /// elongated structures pass). Frangi's original paper uses 0.5.
  /// </summary>
  public double Beta { get; init; } = 0.5;

  /// <summary>
  /// Frangi c — controls sensitivity to "structureness". Smaller =
  /// more sensitive to faint structures (more detections, more false
  /// positives). Pixel intensities here are in [0..255], so c values
  /// in 5–25 are sensible.
  /// </summary>
  public double C { get; init; } = 15.0;

  /// <summary>
  /// Threshold cutoff as a fraction of the 99th-percentile response.
  /// 0.20 = anything ≥ 20% of the strong-response level survives.
  /// Lower = more thorough at the cost of false positives.
  /// Note: very faint scratches (under ~10 luma delta from surrounding
  /// pixels) are essentially undetectable by any classical algorithm
  /// without massive false positives — the brush is the right tool
  /// for those.
  /// </summary>
  public double ResponseCutoff { get; init; } = 0.20;

  /// <summary>
  /// Drop connected components smaller than this many pixels — filters
  /// out dust speckles and paper grain that survive the threshold.
  /// 0 = keep everything.
  /// </summary>
  public int MinComponentArea { get; init; } = 30;

  /// <summary>
  /// Drop connected components whose bounding-box aspect ratio
  /// (long-edge / short-edge) is below this threshold. Real scratches
  /// have ratio ≫ 1 (long-thin lines); blobs (eyes, hair clumps) sit
  /// closer to 1. 3.0 = component must be at least 3× longer than wide.
  /// </summary>
  public double MinAspectRatio { get; init; } = 3.0;

  /// <summary>
  /// Also detect dark scratches (dark lines on lighter background) via
  /// bottom-hat. Off by default because it catches dark hair strands /
  /// eyebrows / shadow lines as false positives. Turn on for landscape
  /// or document scans where damage tends to leave darker marks.
  /// </summary>
  public bool DetectDarkScratches { get; init; }

  /// <summary>
  /// Suppress detections in skin-toned regions so hair / eyebrow lines
  /// don't end up in the mask. On = safer for portraits, off = more
  /// thorough on landscapes. No effect on B&amp;W sources (R = G = B).
  /// </summary>
  public bool SkinToneSuppress { get; init; } = true;
}
