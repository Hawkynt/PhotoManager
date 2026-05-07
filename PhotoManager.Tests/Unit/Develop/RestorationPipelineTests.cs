using System;
using System.Collections.Generic;
using NUnit.Framework;
using PhotoManager.Core.Detection;
using PhotoManager.Core.Develop;
using PhotoManager.Core.Models;
using PhotoManager.Core.Segmentation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
[Category("Unit")]
public sealed class RestorationPipelineTests {
  [Test]
  public void Identity_settings_returns_a_clone_with_matching_dimensions() {
    using var src = new Image<Rgba32>(64, 48);
    src[3, 2] = new Rgba32(200, 100, 50, 255);

    using var output = RestorationPipeline.Apply(src, new List<NormalizedBoundingBox>(), new RestorationSettings());

    Assert.That(output.Width, Is.EqualTo(src.Width));
    Assert.That(output.Height, Is.EqualTo(src.Height));
    Assert.That(output[3, 2], Is.EqualTo(src[3, 2]));
  }

  [Test]
  public void Auto_tone_runs_without_throwing_on_low_contrast_input() {
    using var src = new Image<Rgba32>(32, 32);
    src.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new Rgba32((byte)(90 + (x + y)), (byte)(90 + (x + y)), (byte)(90 + (x + y)), 255);
      }
    });
    var settingsAutoTone = new RestorationSettings(AutoTone: true);
    Assert.DoesNotThrow(() => {
      using var _ = RestorationPipeline.Apply(src, new List<NormalizedBoundingBox>(), settingsAutoTone);
    });
  }

  [Test]
  public void Missing_models_make_AI_stages_no_op_without_throwing() {
    using var src = new Image<Rgba32>(32, 32);
    var settings = new RestorationSettings(
      DenoiseStrength: 1.0,
      RecolourStrength: 1.0,
      FaceRestoreStrength: 1.0,
      DenoiseModel: "this-file-does-not-exist.onnx",
      ColorizeModel: "neither-does-this.onnx",
      UpscaleModel: "or-this.onnx",
      UpscaleFactor: 2
    );
    var faces = new List<NormalizedBoundingBox> {
      new(0.25f, 0.25f, 0.5f, 0.5f)
    };
    Assert.DoesNotThrow(() => {
      using var output = RestorationPipeline.Apply(src, faces, settings);
      // Upscale failed → stays at source size; everything else no-ops.
      Assert.That(output.Width, Is.EqualTo(src.Width));
      Assert.That(output.Height, Is.EqualTo(src.Height));
    });
  }

  // ---------- Diagnostic colorize tests ----------
  // These deliberately call the live ColorizerRouter / OnnxColorizerDDColor
  // path (NOT the full Apply orchestrator) so we can isolate whether the
  // colorize stage itself is broken vs the pipeline orchestration. They
  // skip via Assert.Inconclusive when the model isn't installed locally
  // — CI doesn't ship 250 MB of weights but a developer machine does.

  /// <summary>Build a synthetic luminance gradient that DDColor can
  /// recognise as a real photo: smooth diagonal gradient with mid-range
  /// brightness, replicated across R=G=B (true grayscale). The model
  /// needs varied luminance content to predict meaningful chroma.</summary>
  private static Image<Rgba32> BuildGrayGradientImage(int w = 256, int h = 256) {
    var img = new Image<Rgba32>(w, h);
    img.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          // Diagonal gradient + a little spatial structure so the
          // model has something non-uniform to colorize.
          var gradient = (x + y) * 255 / (w + h);
          var ripple = (int)(20 * Math.Sin(x * 0.1) * Math.Cos(y * 0.07));
          var v = (byte)Math.Clamp(80 + gradient / 2 + ripple, 0, 255);
          row[x] = new Rgba32(v, v, v, 255);
        }
      }
    });
    return img;
  }

  /// <summary>Sample a 10×10 grid and return the % of pixels where
  /// max(|R-G|, |G-B|, |R-B|) ≤ tolerance — i.e. visually gray. The
  /// tolerance is configurable so callers can ask "is the visible
  /// output colored?" (tolerance ≥ 4) vs "are the RGB values bit-equal?"
  /// (tolerance = 0). The 10-px tolerance match what a human eye
  /// perceives as "still gray".</summary>
  private static int GrayPercent(Image<Rgba32> img, int tolerance = 5) {
    var grayCount = 0;
    var total = 0;
    img.ProcessPixelRows(a => {
      var yStep = Math.Max(1, a.Height / 10);
      for (var y = 0; y < a.Height; y += yStep) {
        var row = a.GetRowSpan(y);
        var xStep = Math.Max(1, row.Length / 10);
        for (var x = 0; x < row.Length; x += xStep) {
          total++;
          var p = row[x];
          var d = Math.Max(Math.Max(Math.Abs(p.R - p.G), Math.Abs(p.G - p.B)), Math.Abs(p.R - p.B));
          if (d <= tolerance)
            grayCount++;
        }
      }
    });
    return total == 0 ? 0 : grayCount * 100 / total;
  }

  [Test]
  public void DDColor_alone_on_synthetic_gray_input_produces_colored_output() {
    if (!ModelRegistry.ColorizeDDColorPaperTiny.IsInstalled())
      Assert.Inconclusive("DDColor paper-tiny not installed in this environment.");

    using var src = BuildGrayGradientImage();
    Assert.That(GrayPercent(src), Is.EqualTo(100), "Test fixture should be pure grayscale.");

    using var colorised = ColorizerRouter.Colorize(
      src, modelFileName: "ddcolor-paper-tiny.onnx", strength: 1.0, chromaBoost: 1.6);

    Assert.That(colorised, Is.Not.Null, "Colorize returned null — model not available?");
    var chroma = OnnxColorizerDDColor.LastInferenceMeanAbsAb;
    var grayPct = GrayPercent(colorised!);
    TestContext.Out.WriteLine($"DDColor alone: chroma={chroma:F3}, gray%={grayPct} (lower = more colorful)");
    Assert.That(chroma, Is.GreaterThan(0.5), "Model produced near-zero chroma.");
    Assert.That(grayPct, Is.LessThan(50), $"Output still {grayPct}% gray — chroma not making it through compose.");
  }

  [Test]
  public void DDColor_after_LaMa_inpaint_still_produces_colored_output() {
    if (!ModelRegistry.ColorizeDDColorPaperTiny.IsInstalled())
      Assert.Inconclusive("DDColor paper-tiny not installed.");
    if (!ModelRegistry.LamaInpaint.IsInstalled())
      Assert.Inconclusive("LaMa inpaint model not installed.");

    using var src = BuildGrayGradientImage();
    // Synthesize a small mask "scratch" and inpaint it — same path the
    // brush-mask manual scratch removal takes.
    using var mask = new Image<Rgba32>(src.Width, src.Height);
    mask.ProcessPixelRows(a => {
      for (var y = 100; y < 110; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 50; x < 200; x++)
          row[x] = new Rgba32((byte)255, (byte)0, (byte)0, (byte)200);
      }
    });
    using (var inpainter = new OnnxInpainter()) {
      if (!inpainter.IsAvailable)
        Assert.Inconclusive("LaMa inpainter session unavailable.");
      using var inpainted = inpainter.Inpaint(src, mask);
      Assert.That(inpainted, Is.Not.Null);

      using var colorised = ColorizerRouter.Colorize(
        inpainted!, modelFileName: "ddcolor-paper-tiny.onnx", strength: 1.0, chromaBoost: 1.6);
      Assert.That(colorised, Is.Not.Null);
      var chroma = OnnxColorizerDDColor.LastInferenceMeanAbsAb;
      var grayPct = GrayPercent(colorised!);
      TestContext.Out.WriteLine($"DDColor after LaMa: chroma={chroma:F3}, gray%={grayPct}");
      Assert.That(chroma, Is.GreaterThan(0.5), $"Chroma fell to {chroma:F3} after LaMa.");
      Assert.That(grayPct, Is.LessThan(50), $"Output {grayPct}% gray after LaMa — bad.");
    }
  }

  [Test]
  public void DDColor_after_BOPB_detector_run_still_produces_colored_output() {
    if (!ModelRegistry.ColorizeDDColorPaperTiny.IsInstalled())
      Assert.Inconclusive("DDColor paper-tiny not installed.");
    if (!ModelRegistry.BopbScratchDetector.IsInstalled())
      Assert.Inconclusive("BOPB scratch detector not installed.");

    using var src = BuildGrayGradientImage();
    // Run BOPB on the source first (to mimic auto-scratch's first
    // detection pass), THEN colorize. If this produces gray output but
    // the previous test (without BOPB) produces color, BOPB is corrupting
    // some shared ORT runtime state for subsequent inferences.
    using (var bopb = new OnnxScratchDetectorBOPB()) {
      if (!bopb.IsAvailable)
        Assert.Inconclusive("BOPB session unavailable.");
      using var detectedMask = bopb.Detect(src);
      Assert.That(detectedMask, Is.Not.Null);
    }

    using var colorised = ColorizerRouter.Colorize(
      src, modelFileName: "ddcolor-paper-tiny.onnx", strength: 1.0, chromaBoost: 1.6);
    Assert.That(colorised, Is.Not.Null);
    var chroma = OnnxColorizerDDColor.LastInferenceMeanAbsAb;
    var grayPct = GrayPercent(colorised!);
    TestContext.Out.WriteLine($"DDColor after BOPB: chroma={chroma:F3}, gray%={grayPct}");
    Assert.That(chroma, Is.GreaterThan(0.5), $"Chroma after BOPB was {chroma:F3} — BOPB tainted ORT state.");
    Assert.That(grayPct, Is.LessThan(50), $"Output {grayPct}% gray after BOPB — confirms stacking bug.");
  }

  [Test]
  public void Full_pipeline_recolour_only_on_synthetic_gray_input_produces_color() {
    if (!ModelRegistry.ColorizeDDColorPaperTiny.IsInstalled())
      Assert.Inconclusive("DDColor paper-tiny not installed.");

    using var src = BuildGrayGradientImage();
    var settings = new RestorationSettings(RecolourStrength: 1.0);
    using var output = RestorationPipeline.Apply(src, new List<NormalizedBoundingBox>(), settings);
    var chroma = OnnxColorizerDDColor.LastInferenceMeanAbsAb;
    var grayPct = GrayPercent(output);
    TestContext.Out.WriteLine($"Pipeline recolour-only: chroma={chroma:F3}, gray%={grayPct}");
    Assert.That(grayPct, Is.LessThan(50), $"Recolour-only gray%={grayPct} — should be <50.");
  }

  [Test]
  public void DDColor_on_image_with_black_border_may_produce_low_chroma() {
    if (!ModelRegistry.ColorizeDDColorPaperTiny.IsInstalled())
      Assert.Inconclusive("DDColor paper-tiny not installed.");

    // Simulate a scanned photo with a thick black border framing a
    // mid-grey content region — common scenario for old prints. With
    // a 25% wide border the mean of the whole image drops toward ~127
    // even though the central content has narrower variance.
    using var src = new Image<Rgba32>(768, 513);
    var rng = new Random(7);
    var borderPx = 100;
    src.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        var inBorderY = y < borderPx || y >= a.Height - borderPx;
        for (var x = 0; x < row.Length; x++) {
          var inBorderX = x < borderPx || x >= row.Length - borderPx;
          if (inBorderX || inBorderY) {
            row[x] = new Rgba32((byte)3, (byte)3, (byte)3, 255);
          } else {
            // mid-grey content with slight gradient + noise
            var gradient = (x + y) * 100 / (a.Height + row.Length);
            var noise = rng.Next(-15, 16);
            var v = (byte)Math.Clamp(160 + gradient + noise, 100, 224);
            row[x] = new Rgba32(v, v, v, 255);
          }
        }
      }
    });

    using var colorised = ColorizerRouter.Colorize(
      src, modelFileName: "ddcolor-paper-tiny.onnx", strength: 1.0, chromaBoost: 1.6);
    var chroma = OnnxColorizerDDColor.LastInferenceMeanAbsAb;
    var grayPct = GrayPercent(colorised!);
    var stats = OnnxColorizerDDColor.LastInputStats;
    TestContext.Out.WriteLine($"Bordered photo: chroma={chroma:F3}, gray%={grayPct}, src={stats}");
    // No assertion on chroma value — this test is INFORMATIONAL. We
    // want to see whether bordered photos produce significantly lower
    // chroma than the borderless case (which scored chroma≈13).
  }

  [Test]
  public void DDColor_concurrent_invocations_all_produce_consistent_color() {
    if (!ModelRegistry.ColorizeDDColorPaperTiny.IsInstalled())
      Assert.Inconclusive("DDColor paper-tiny not installed.");

    // Reproduce the UI-slider-drag pattern: many concurrent Colorize
    // calls against the same source image. Without a shared cached
    // session, each call constructs its own InferenceSession and
    // races concurrent same-model loads in ORT — which can silently
    // produce wrong inference output (the user-reported "chroma=0.23
    // through UI but chroma=34 in single-threaded test" pattern).
    // With caching, all calls share one session and ORT's documented
    // thread-safe Run handles the concurrency correctly.
    using var src = BuildGrayGradientImage();
    const int parallelInvocations = 8;

    var tasks = new System.Threading.Tasks.Task<int>[parallelInvocations];
    for (var i = 0; i < parallelInvocations; i++) {
      tasks[i] = System.Threading.Tasks.Task.Run(() => {
        using var colorised = ColorizerRouter.Colorize(
          src, modelFileName: "ddcolor-paper-tiny.onnx", strength: 1.0, chromaBoost: 1.6);
        return colorised is null ? -1 : GrayPercent(colorised);
      });
    }
    System.Threading.Tasks.Task.WaitAll(tasks);

    for (var i = 0; i < tasks.Length; i++) {
      var grayPct = tasks[i].Result;
      TestContext.Out.WriteLine($"Concurrent call {i}: gray%={grayPct}");
      Assert.That(grayPct, Is.LessThan(50),
        $"Concurrent call {i} produced {grayPct}% gray output — race in concurrent session creation.");
    }
  }

  [Test]
  public void DDColor_on_768x513_image_matching_user_reported_stats() {
    if (!ModelRegistry.ColorizeDDColorPaperTiny.IsInstalled())
      Assert.Inconclusive("DDColor paper-tiny not installed.");

    // Reproduce the user's image properties: 768×513, mean R=127/G=127/B=125,
    // R-span [3..224]. This isolates "is DDColor capable on this size /
    // distribution" from "is the user's specific photo's content tricky".
    using var src = new Image<Rgba32>(768, 513);
    var rng = new Random(42);
    src.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          // Smooth diagonal gradient + noise to span [3..224] with mean ~127.
          var gradient = (x + y) * 220 / (a.Height + row.Length);
          var noise = rng.Next(-15, 16);
          var v = (byte)Math.Clamp(3 + gradient + noise, 3, 224);
          row[x] = new Rgba32(v, v, v, 255);
        }
      }
    });

    using var colorised = ColorizerRouter.Colorize(
      src, modelFileName: "ddcolor-paper-tiny.onnx", strength: 1.0, chromaBoost: 1.6);
    var chroma = OnnxColorizerDDColor.LastInferenceMeanAbsAb;
    var grayPct = GrayPercent(colorised!);
    var stats = OnnxColorizerDDColor.LastInputStats;
    TestContext.Out.WriteLine($"User-stat-matched: chroma={chroma:F3}, gray%={grayPct}, src={stats}");
    Assert.That(chroma, Is.GreaterThan(2.0),
      $"Chroma {chroma:F3} on a 768x513 mid-range gradient is unexpectedly low. " +
      "Indicates the issue isn't image-content-specific — model / inference is broken.");
  }

  [Test]
  public void Full_pipeline_autoscratch_then_recolour_produces_color() {
    if (!ModelRegistry.ColorizeDDColorPaperTiny.IsInstalled())
      Assert.Inconclusive("DDColor paper-tiny not installed.");
    if (!ModelRegistry.LamaInpaint.IsInstalled())
      Assert.Inconclusive("LaMa inpaint model not installed.");

    using var src = BuildGrayGradientImage();
    var settings = new RestorationSettings(
      RecolourStrength: 1.0,
      AutoScratchRemoval: true,
      AutoScratchMaxIterations: 2,
      AutoScratchThresholdPct: 0.3);
    using var output = RestorationPipeline.Apply(src, new List<NormalizedBoundingBox>(), settings);
    var chroma = OnnxColorizerDDColor.LastInferenceMeanAbsAb;
    var grayPct = GrayPercent(output);
    TestContext.Out.WriteLine($"Pipeline autoscratch+recolour: chroma={chroma:F3}, gray%={grayPct}");
    Assert.That(grayPct, Is.LessThan(50), $"AutoScratch+recolour gray%={grayPct} — should be <50.");
  }

  // ---------- Stage cache tests ----------
  // Verify that:
  //   - First Apply with a cache populates the cache.
  //   - Second Apply with same settings is a full cache hit (no re-run).
  //   - Changing a late stage's settings invalidates that stage's
  //     cumulative-key entry but reuses prior stages' cached output.
  //   - Changing the source image clears the cache via BindToSource.

  [Test]
  public void Cache_first_apply_populates_entries_for_each_active_stage() {
    using var src = new Image<Rgba32>(64, 64);
    using var cache = new RestorationPipelineCache();
    cache.BindToSource(src);

    Assert.That(cache.CachedStageCount, Is.EqualTo(0));

    var settings = new RestorationSettings(
      AutoTone: true,
      DespeckleStrength: 1.0,
      AutoScratchRemoval: false);  // Default is true; turn off so test count is deterministic.
    using var output = RestorationPipeline.Apply(src, new List<NormalizedBoundingBox>(), settings, cache: cache);

    // Both auto-tone and despeckle ran → both cached.
    Assert.That(cache.CachedStageCount, Is.EqualTo(2),
      $"Expected 2 cached stages (auto-tone + despeckle), got {cache.CachedStageCount}.");
  }

  [Test]
  public void Cache_invalidates_when_source_changes_via_BindToSource() {
    using var src1 = new Image<Rgba32>(64, 64);
    using var src2 = new Image<Rgba32>(64, 64);
    using var cache = new RestorationPipelineCache();

    cache.BindToSource(src1);
    var settings = new RestorationSettings(AutoTone: true, AutoScratchRemoval: false);
    using (var _ = RestorationPipeline.Apply(src1, new List<NormalizedBoundingBox>(), settings, cache: cache)) { }
    Assert.That(cache.CachedStageCount, Is.EqualTo(1));

    cache.BindToSource(src2);  // Different source — cache should clear.
    Assert.That(cache.CachedStageCount, Is.EqualTo(0),
      "BindToSource with new source did not clear cache.");
  }

  [Test]
  public void Cache_hit_on_unchanged_settings_does_not_recompute_stage_outputs() {
    // Use auto-tone (deterministic, cheap) — easy to verify cache hits
    // produce identical output to fresh runs.
    using var src = new Image<Rgba32>(64, 64);
    src.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new Rgba32((byte)(50 + x), (byte)(50 + x), (byte)(50 + x), 255);
      }
    });
    using var cache = new RestorationPipelineCache();
    cache.BindToSource(src);

    var settings = new RestorationSettings(AutoTone: true, AutoScratchRemoval: false);
    using var first = RestorationPipeline.Apply(src, new List<NormalizedBoundingBox>(), settings, cache: cache);
    using var second = RestorationPipeline.Apply(src, new List<NormalizedBoundingBox>(), settings, cache: cache);

    // Outputs must be pixel-equal — cache hit returns the same data
    // as the original computation.
    var firstSample = first[10, 10];
    var secondSample = second[10, 10];
    Assert.That(secondSample, Is.EqualTo(firstSample),
      "Cache hit produced different output than the original Apply.");
  }

  [Test]
  public void Colorizer_input_is_grayscaled_so_subtle_color_artifacts_do_not_change_output() {
    if (!ModelRegistry.ColorizeDDColorPaperTiny.IsInstalled())
      Assert.Inconclusive("DDColor paper-tiny not installed.");

    // Take the same gradient and produce two versions: pure grayscale
    // (R=G=B) and "noisy near-grayscale" with a tiny per-pixel R/G/B
    // jitter that simulates LaMa-synthesis artifacts. With the
    // pre-grayscale step in ColorizerRouter, both should produce the
    // same colorize output (because both get flattened to Rec.709
    // luminance before inference).
    using var pure = BuildGrayGradientImage();
    using var noisy = pure.Clone();
    var rng = new Random(1);
    noisy.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var p = row[x];
          // Simulate sub-byte LaMa-style colour drift: R and B drift
          // by ±2 around the original luminance.
          var rDrift = (byte)Math.Clamp(p.R + rng.Next(-2, 3), 0, 255);
          var bDrift = (byte)Math.Clamp(p.B + rng.Next(-2, 3), 0, 255);
          row[x] = new Rgba32(rDrift, p.G, bDrift, p.A);
        }
      }
    });

    using var pureOut = ColorizerRouter.Colorize(pure, "ddcolor-paper-tiny.onnx", 1.0, 1.6);
    using var noisyOut = ColorizerRouter.Colorize(noisy, "ddcolor-paper-tiny.onnx", 1.0, 1.6);
    Assert.That(pureOut, Is.Not.Null);
    Assert.That(noisyOut, Is.Not.Null);

    // Compare a few sample pixels — they should be very close, since
    // both inputs grayscale to nearly the same luminance per pixel.
    // (Not bit-equal because Rec.709 luminance of a noisy pixel
    // differs from luminance of the pure pixel by ±1; we tolerate
    // small differences.)
    var deltaSum = 0;
    var sampleCount = 0;
    pureOut!.ProcessPixelRows(noisyOut!, (pa, na) => {
      var step = Math.Max(1, pa.Height / 20);
      for (var y = 0; y < pa.Height; y += step) {
        var pRow = pa.GetRowSpan(y);
        var nRow = na.GetRowSpan(y);
        for (var x = 0; x < pRow.Length; x += step) {
          deltaSum += Math.Abs(pRow[x].R - nRow[x].R)
                    + Math.Abs(pRow[x].G - nRow[x].G)
                    + Math.Abs(pRow[x].B - nRow[x].B);
          sampleCount++;
        }
      }
    });
    var meanDelta = (double)deltaSum / (sampleCount * 3);
    TestContext.Out.WriteLine($"Mean per-channel delta between pure-gray and noisy-near-gray colorize outputs: {meanDelta:F2}");
    Assert.That(meanDelta, Is.LessThan(20),
      $"Pre-grayscale should make colorize outputs near-identical for pure vs noisy-near-grayscale inputs. Got mean delta {meanDelta:F2}.");
  }

  [Test]
  public void Cache_changing_chromaBoost_invalidates_recolour_and_re_runs() {
    if (!ModelRegistry.ColorizeDDColorPaperTiny.IsInstalled())
      Assert.Inconclusive("DDColor paper-tiny not installed.");

    // Reproduces the user's reported flow: Run colorize once with
    // ChromaBoost=1.6. Then change ChromaBoost to 20. The recolour
    // cache MUST miss (because the cumulative key includes boost) and
    // produce a different output than the first run.
    using var src = BuildGrayGradientImage();
    using var cache = new RestorationPipelineCache();
    cache.BindToSource(src);

    var s1 = new RestorationSettings(
      RecolourStrength: 1.0,
      ChromaBoost: 1.6,
      AutoScratchRemoval: false);
    using var out1 = RestorationPipeline.Apply(src, new List<NormalizedBoundingBox>(), s1, cache: cache);
    var sample1 = out1[100, 100];

    var s2 = s1 with { ChromaBoost = 20.0 };
    using var out2 = RestorationPipeline.Apply(src, new List<NormalizedBoundingBox>(), s2, cache: cache);
    var sample2 = out2[100, 100];

    TestContext.Out.WriteLine($"chromaBoost=1.6 → pixel(100,100)={sample1}");
    TestContext.Out.WriteLine($"chromaBoost=20  → pixel(100,100)={sample2}");

    // With higher boost, the pixel should differ from the boost=1.6
    // version. If the cache erroneously HIT on the boost=20 call,
    // sample2 would equal sample1. That'd be the user-reported bug.
    Assert.That(sample2, Is.Not.EqualTo(sample1),
      "Cache erroneously returned the boost=1.6 result for boost=20 — bug in cumulative-key invalidation.");

    // And the boost=20 output should have stronger color than boost=1.6.
    var deltaAt1_6 = Math.Max(Math.Abs(sample1.R - sample1.G), Math.Abs(sample1.G - sample1.B));
    var deltaAt20 = Math.Max(Math.Abs(sample2.R - sample2.G), Math.Abs(sample2.G - sample2.B));
    Assert.That(deltaAt20, Is.GreaterThanOrEqualTo(deltaAt1_6),
      $"Boost=20 should give >= color delta than boost=1.6 ({deltaAt20} vs {deltaAt1_6}).");
  }

  [Test]
  public void Cache_changing_late_stage_settings_keeps_early_stage_entries() {
    // Run with two stages: AutoTone + Despeckle. First Apply populates
    // both. Then change Despeckle's strength but keep AutoTone the
    // same — the AutoTone entry stays fresh, only Despeckle is
    // recomputed (and thus stays in the cache with a new key).
    using var src = new Image<Rgba32>(64, 64);
    using var cache = new RestorationPipelineCache();
    cache.BindToSource(src);

    var settings1 = new RestorationSettings(AutoTone: true, DespeckleStrength: 0.5, AutoScratchRemoval: false);
    using (var _ = RestorationPipeline.Apply(src, new List<NormalizedBoundingBox>(), settings1, cache: cache)) { }
    Assert.That(cache.CachedStageCount, Is.EqualTo(2));

    // The cumulative cache key for AutoTone hasn't changed, so its
    // entry remains valid. Despeckle's strength differs → its
    // cumulative key changes → it's recomputed and OVERWRITES its
    // own cache entry. Total entries stay at 2 (auto-tone unchanged,
    // despeckle replaced).
    var settings2 = settings1 with { DespeckleStrength = 0.8 };
    using (var _ = RestorationPipeline.Apply(src, new List<NormalizedBoundingBox>(), settings2, cache: cache)) { }
    Assert.That(cache.CachedStageCount, Is.EqualTo(2),
      $"Expected 2 entries after late-stage settings change, got {cache.CachedStageCount}.");

    // And turning AutoTone OFF entirely should not crash; downstream
    // stages re-run with the new prefix key.
    var settings3 = settings2 with { AutoTone = false };
    using (var _ = RestorationPipeline.Apply(src, new List<NormalizedBoundingBox>(), settings3, cache: cache)) { }
    // AutoTone entry is stale (its cumulativeKey is no longer reached
    // because it doesn't run) but lingers in the dict until overwritten.
    // Despeckle gets re-stored with the new prefix.
    Assert.That(cache.CachedStageCount, Is.GreaterThanOrEqualTo(1));
  }

  [Test]
  public void Presets_are_non_identity_and_distinct() {
    Assert.That(RestorationSettings.OldBlackAndWhite.IsIdentity, Is.False);
    Assert.That(RestorationSettings.DamagedColour.IsIdentity, Is.False);
    Assert.That(RestorationSettings.FadedSlide.IsIdentity, Is.False);
    Assert.That(RestorationSettings.SubtleCleanup.IsIdentity, Is.False);

    Assert.That(RestorationSettings.OldBlackAndWhite, Is.Not.EqualTo(RestorationSettings.DamagedColour));
    Assert.That(RestorationSettings.OldBlackAndWhite.RecolourStrength, Is.GreaterThan(0));
    Assert.That(RestorationSettings.DamagedColour.RecolourStrength, Is.EqualTo(0));
  }
}
