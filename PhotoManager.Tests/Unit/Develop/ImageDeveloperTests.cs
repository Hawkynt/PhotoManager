using PhotoManager.Core.Develop;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
public class ImageDeveloperTests {
  private static Image<Rgba32> SolidColor(int width, int height, Rgba32 color) {
    var img = new Image<Rgba32>(width, height);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = color;
      }
    });
    return img;
  }

  [Test]
  public void Apply_IdentitySettings_ReturnsEquivalentImage() {
    using var src = SolidColor(10, 10, new Rgba32(100, 150, 200, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings());
    Assert.Multiple(() => {
      Assert.That(out_.Width, Is.EqualTo(10));
      Assert.That(out_.Height, Is.EqualTo(10));
      out_.ProcessPixelRows(a => {
        var px = a.GetRowSpan(0)[0];
        Assert.That(px.R, Is.EqualTo(100));
        Assert.That(px.G, Is.EqualTo(150));
        Assert.That(px.B, Is.EqualTo(200));
      });
    });
  }

  [Test]
  public void Apply_Rotate90_SwapsWidthHeight() {
    using var src = SolidColor(20, 10, new Rgba32(1, 2, 3, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(RotationDegrees: 90));
    Assert.Multiple(() => {
      Assert.That(out_.Width, Is.EqualTo(10));
      Assert.That(out_.Height, Is.EqualTo(20));
    });
  }

  [Test]
  public void Apply_ExposurePositive_BrightenPixels() {
    using var src = SolidColor(5, 5, new Rgba32(50, 50, 50, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(ExposureStops: 1.0));
    // +1 stop → roughly doubles brightness → about 100.
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      Assert.That((int)px.R, Is.InRange(98, 102));
    });
  }

  [Test]
  public void Apply_SaturationMinus100_ProducesGrayscale() {
    using var src = SolidColor(5, 5, new Rgba32(200, 50, 50, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(SaturationPercent: -100));
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      Assert.That(px.R, Is.EqualTo(px.G));
      Assert.That(px.G, Is.EqualTo(px.B));
    });
  }

  [Test]
  public void Apply_TemperaturePositive_ShiftsTowardWarm() {
    using var src = SolidColor(5, 5, new Rgba32(128, 128, 128, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(TemperatureShift: 100));
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      Assert.That((int)px.R, Is.GreaterThan(130));
      Assert.That((int)px.B, Is.LessThan(120));
    });
  }

  [Test]
  public void Apply_ContrastPositive_PushesFarFromMidGray() {
    using var src = SolidColor(5, 5, new Rgba32(200, 200, 200, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(ContrastPercent: 50));
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      // Above midgray → contrast should push brighter.
      Assert.That((int)px.R, Is.GreaterThan(200));
    });
  }

  [Test]
  public void IsIdentity_AllZero_True() {
    var s = new DevelopSettings();
    Assert.That(s.IsIdentity, Is.True);
  }

  [Test]
  public void IsIdentity_AnyNonZero_False() {
    var s = new DevelopSettings(ExposureStops: 0.1);
    Assert.That(s.IsIdentity, Is.False);
  }

  [Test]
  public void BuildCurveLut_IdentityCurve_ReturnsNull() {
    var lut = ImageDeveloper.BuildCurveLut(new[] { new CurvePoint(0, 0), new CurvePoint(1, 1) });
    Assert.That(lut, Is.Null);
  }

  [Test]
  public void BuildCurveLut_NullOrTooFewPoints_ReturnsNull() {
    Assert.That(ImageDeveloper.BuildCurveLut(null), Is.Null);
    Assert.That(ImageDeveloper.BuildCurveLut(new[] { new CurvePoint(0.5, 0.5) }), Is.Null);
  }

  [TestCase(CurveInterpolation.Linear)]
  [TestCase(CurveInterpolation.CatmullRom)]
  [TestCase(CurveInterpolation.Bezier)]
  public void BuildCurveLut_AnyMode_HitsEndpoints(CurveInterpolation mode) {
    // A clear S-curve so the result actually depends on the sampler.
    var pts = new[] { new CurvePoint(0, 0), new CurvePoint(0.25, 0.1),
                      new CurvePoint(0.75, 0.9), new CurvePoint(1, 1) };
    var lut = ImageDeveloper.BuildCurveLut(pts, mode);
    Assert.That(lut, Is.Not.Null);
    Assert.That(lut![0],   Is.EqualTo(0));
    Assert.That(lut[255],  Is.EqualTo(255));
  }

  [Test]
  public void BuildCurveLut_LinearMode_MidpointMatchesLinearInterpolation() {
    // Y at x=0.5 between (0,0) and (1,0.8) should be 0.4 → byte ~102.
    var pts = new[] { new CurvePoint(0, 0), new CurvePoint(1, 0.8) };
    var lut = ImageDeveloper.BuildCurveLut(pts, CurveInterpolation.Linear);
    Assert.That(lut, Is.Not.Null);
    Assert.That((int)lut![128], Is.InRange(99, 105));
  }

  [Test]
  public void BuildCurveLut_SplineAndBezier_ProduceMonotonicResultsForMonotonicInput() {
    var pts = new[] { new CurvePoint(0, 0), new CurvePoint(0.5, 0.4), new CurvePoint(1, 1) };
    foreach (var mode in new[] { CurveInterpolation.CatmullRom, CurveInterpolation.Bezier }) {
      var lut = ImageDeveloper.BuildCurveLut(pts, mode);
      Assert.That(lut, Is.Not.Null, $"mode {mode}");
      for (var i = 1; i < 256; i++)
        Assert.That(lut![i], Is.GreaterThanOrEqualTo(lut[i - 1]),
          $"mode {mode} not monotonic at i={i}");
    }
  }

  [Test]
  public void Apply_DehazePositive_PushesMidtoneSaturation() {
    using var src = SolidColor(5, 5, new Rgba32(140, 110, 90, 255));  // muted brown
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(DehazePercent: 100));
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      // Saturation should rise — channel spread (max - min) widens.
      var srcSpread = 140 - 90;
      var outSpread = Math.Max(px.R, Math.Max(px.G, px.B)) - Math.Min(px.R, Math.Min(px.G, px.B));
      Assert.That(outSpread, Is.GreaterThan(srcSpread),
        "dehaze should boost midtone saturation (channel spread)");
    });
  }

  [Test]
  public void Apply_HueRangeMask_LimitsToTargetHue() {
    // Half red, half blue. Hue range mask 0.55..0.75 (blue-ish) on a
    // brush mask covering everything should affect blue only.
    using var src = new Image<Rgba32>(20, 20);
    src.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length / 2; x++) row[x] = new Rgba32(220, 60, 60, 255);
        for (var x = row.Length / 2; x < row.Length; x++) row[x] = new Rgba32(60, 60, 220, 255);
      }
    });
    var settings = new DevelopSettings(LocalAdjustments: new[] {
      new LocalAdjustment(
        Mask: new LocalMask(
          Type: LocalMaskType.Brush,
          BrushDabs: new[] { new BrushDab(0.5, 0.5, 1.0, 1.0) },
          HueRangeMin: 0.55, HueRangeMax: 0.75, HueRangeFeather: 0.02),
        Saturation: -100)
    });
    using var out_ = ImageDeveloper.Apply(src, settings);
    int redSpread = 0, blueSpread = 0;
    out_.ProcessPixelRows(a => {
      var redPx = a.GetRowSpan(10)[5];
      var bluePx = a.GetRowSpan(10)[15];
      redSpread = Math.Max(redPx.R, Math.Max(redPx.G, redPx.B)) - Math.Min(redPx.R, Math.Min(redPx.G, redPx.B));
      blueSpread = Math.Max(bluePx.R, Math.Max(bluePx.G, bluePx.B)) - Math.Min(bluePx.R, Math.Min(bluePx.G, bluePx.B));
    });
    Assert.That(redSpread, Is.GreaterThan(100),
      "red side is outside the blue hue band — should keep its saturation");
    Assert.That(blueSpread, Is.LessThan(20),
      "blue side falls inside the hue band and gets desaturated");
  }

  [Test]
  public void Apply_SubMaskSubtract_CarvesHole() {
    // Primary brush mask covering everything brightens; sub-mask
    // (Subtract) carves a hole near the centre so it stays dark.
    using var src = SolidColor(40, 40, new Rgba32(50, 50, 50, 255));
    var settings = new DevelopSettings(LocalAdjustments: new[] {
      new LocalAdjustment(
        Mask: new LocalMask(LocalMaskType.Brush,
          BrushDabs: new[] { new BrushDab(0.5, 0.5, 1.0, 1.0) }),
        SubMasks: new[] {
          new LocalMask(
            Type: LocalMaskType.Radial,
            CenterX: 0.5, CenterY: 0.5,
            RadiusX: 0.15, RadiusY: 0.15,
            Feather: 30,
            Combine: MaskCombineOp.Subtract)
        },
        Exposure: 2.0)
    });
    using var out_ = ImageDeveloper.Apply(src, settings);
    int centerR = 0, edgeR = 0;
    out_.ProcessPixelRows(a => {
      centerR = a.GetRowSpan(20)[20].R;
      edgeR   = a.GetRowSpan(2)[2].R;
    });
    Assert.That(edgeR,   Is.GreaterThan(120), "edge stays inside primary brush, gets +EV");
    Assert.That(centerR, Is.LessThan(80),     "centre is carved out by the radial sub-mask");
  }

  [Test]
  public void Apply_SubMaskIntersect_NarrowsToOverlap() {
    // Primary linear gradient (top→bottom darken) intersected with a
    // radial mask centred on the bottom: only the bottom centre area
    // gets the adjustment, even though linear covers the whole image.
    using var src = SolidColor(40, 40, new Rgba32(200, 200, 200, 255));
    var settings = new DevelopSettings(LocalAdjustments: new[] {
      new LocalAdjustment(
        Mask: new LocalMask(LocalMaskType.Linear,
          X0: 0.5, Y0: 0.0, X1: 0.5, Y1: 1.0),
        SubMasks: new[] {
          new LocalMask(
            Type: LocalMaskType.Radial,
            CenterX: 0.5, CenterY: 0.85,
            RadiusX: 0.2, RadiusY: 0.2,
            Feather: 30,
            Combine: MaskCombineOp.Intersect)
        },
        Exposure: -2.0)
    });
    using var out_ = ImageDeveloper.Apply(src, settings);
    int bottomCenter = 0, bottomCorner = 0;
    out_.ProcessPixelRows(a => {
      bottomCenter = a.GetRowSpan(34)[20].R;
      bottomCorner = a.GetRowSpan(34)[2].R;
    });
    Assert.That(bottomCenter, Is.LessThan(150),
      "bottom centre is in both masks — intersected weight darkens it");
    Assert.That(bottomCorner, Is.GreaterThan(180),
      "bottom corner is outside the radial sub-mask — intersection drops to zero, no darkening");
  }

  [Test]
  public void Apply_RangeMask_LimitsLocalAdjustmentToTargetLuminance() {
    // Half-bright / half-dark image; brush mask covers the whole image
    // but a luminance range mask of [0.6..1] should restrict the +EV
    // exposure to the bright half only.
    using var src = new Image<Rgba32>(20, 20);
    src.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length / 2; x++) row[x] = new Rgba32(40, 40, 40, 255);   // dark
        for (var x = row.Length / 2; x < row.Length; x++) row[x] = new Rgba32(220, 220, 220, 255);  // bright
      }
    });
    var settings = new DevelopSettings(LocalAdjustments: new[] {
      new LocalAdjustment(
        Mask: new LocalMask(
          Type: LocalMaskType.Brush,
          BrushDabs: new[] { new BrushDab(0.5, 0.5, 1.0, 1.0) },
          LuminanceRangeMin: 0.6, LuminanceRangeMax: 1.0, LuminanceRangeFeather: 0.05),
        Exposure: 1.0)
    });
    using var out_ = ImageDeveloper.Apply(src, settings);
    int darkR = 0, brightR = 0;
    out_.ProcessPixelRows(a => { darkR = a.GetRowSpan(10)[5].R; brightR = a.GetRowSpan(10)[15].R; });
    Assert.That(darkR, Is.EqualTo(40),
      "dark pixels lie outside the luminance range and should not receive the +EV adjustment");
    Assert.That(brightR, Is.GreaterThan(220),
      "bright pixels lie inside the luminance range and should receive the +EV adjustment");
  }

  [Test]
  public void Apply_LocalLuminance_LiftsMaskedPixels() {
    using var src = SolidColor(20, 20, new Rgba32(80, 80, 80, 255));
    var settings = new DevelopSettings(LocalAdjustments: new[] {
      new LocalAdjustment(
        Mask: new LocalMask(LocalMaskType.Linear, X0: 0.0, Y0: 0.5, X1: 1.0, Y1: 0.5),
        Luminance: 100)
    });
    using var out_ = ImageDeveloper.Apply(src, settings);
    int rightR = 0;
    out_.ProcessPixelRows(a => rightR = a.GetRowSpan(10)[18].R);
    Assert.That(rightR, Is.GreaterThan(80),
      "+100 LocalLuminance should lift the masked side toward white");
  }

  [Test]
  public void Apply_ColorEnhancement_ProtectsSkinHues() {
    // Skin-ish orange pixel and a saturated blue pixel — same low
    // saturation. ColorEnhancement should boost the blue more than the orange.
    using var skin = SolidColor(5, 5, new Rgba32(180, 140, 110, 255));
    using var blue = SolidColor(5, 5, new Rgba32(110, 140, 180, 255));
    using var skinEnhanced = ImageDeveloper.Apply(skin, new DevelopSettings(ColorEnhancement: 100));
    using var blueEnhanced = ImageDeveloper.Apply(blue, new DevelopSettings(ColorEnhancement: 100));
    int skinSpread = 0, blueSpread = 0;
    skinEnhanced.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      skinSpread = Math.Max(px.R, Math.Max(px.G, px.B)) - Math.Min(px.R, Math.Min(px.G, px.B));
    });
    blueEnhanced.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      blueSpread = Math.Max(px.R, Math.Max(px.G, px.B)) - Math.Min(px.R, Math.Min(px.G, px.B));
    });
    Assert.That(blueSpread, Is.GreaterThan(skinSpread),
      "ColorEnhancement should saturate the blue pixel more than the skin-tone pixel");
  }

  [Test]
  public void Apply_LocalLinearGradient_BrightensFarSideOnly() {
    // Linear gradient from top to bottom; +1 EV exposure at the bottom.
    using var src = SolidColor(20, 20, new Rgba32(80, 80, 80, 255));
    var settings = new DevelopSettings(LocalAdjustments: new[] {
      new LocalAdjustment(
        Mask: new LocalMask(LocalMaskType.Linear, X0: 0.5, Y0: 0.0, X1: 0.5, Y1: 1.0),
        Exposure: 1.0)
    });
    using var out_ = ImageDeveloper.Apply(src, settings);
    int top = 0, bottom = 0;
    out_.ProcessPixelRows(a => {
      top    = a.GetRowSpan(1)[10].R;
      bottom = a.GetRowSpan(18)[10].R;
    });
    Assert.That(bottom, Is.GreaterThan(top + 30),
      "linear gradient should leave the top untouched and brighten the bottom");
  }

  [Test]
  public void Apply_LocalBrushDab_AffectsPaintedPixelsOnly() {
    using var src = SolidColor(40, 40, new Rgba32(80, 80, 80, 255));
    var settings = new DevelopSettings(LocalAdjustments: new[] {
      new LocalAdjustment(
        Mask: new LocalMask(
          Type: LocalMaskType.Brush,
          BrushDabs: new[] { new BrushDab(0.25, 0.25, 0.1, 1.0) }),
        Exposure: 2.0)
    });
    using var out_ = ImageDeveloper.Apply(src, settings);
    int paintedR = 0, untouchedR = 0;
    out_.ProcessPixelRows(a => {
      paintedR   = a.GetRowSpan(10)[10].R;  // ~ (0.25, 0.25)
      untouchedR = a.GetRowSpan(35)[35].R;  // far corner, no dab
    });
    Assert.That(paintedR,   Is.GreaterThan(150),  "painted pixel should brighten with +2 EV");
    Assert.That(untouchedR, Is.EqualTo(80),       "pixel outside the dab radius should stay at 80");
  }

  [Test]
  public void Apply_LocalRadialGradient_AffectsCenterOnly() {
    using var src = SolidColor(40, 40, new Rgba32(80, 80, 80, 255));
    var settings = new DevelopSettings(LocalAdjustments: new[] {
      new LocalAdjustment(
        Mask: new LocalMask(LocalMaskType.Radial, CenterX: 0.5, CenterY: 0.5, RadiusX: 0.25, RadiusY: 0.25, Feather: 30),
        Exposure: 2.0)
    });
    using var out_ = ImageDeveloper.Apply(src, settings);
    int center = 0, corner = 0;
    out_.ProcessPixelRows(a => {
      center = a.GetRowSpan(20)[20].R;
      corner = a.GetRowSpan(2)[2].R;
    });
    Assert.That(center, Is.GreaterThan(corner + 30),
      "radial gradient should brighten the centre but leave the corner untouched");
  }

  [Test]
  public void Apply_PerspectiveScale_HalfShrinksImageContent() {
    using var src = SolidColor(20, 20, new Rgba32(0, 0, 0, 255));
    src.ProcessPixelRows(a => a.GetRowSpan(10)[10] = new Rgba32(255, 255, 255, 255));
    using var scaled = ImageDeveloper.Apply(src, new DevelopSettings(PerspectiveScale: 50));
    // Scale 50 → output samples from a 2× window of source, so the bright
    // pixel at (10,10) maps to output (10,10) (center is fixed) — but
    // neighbours that previously sat at (11,10) now sample further out
    // and become black.
    int neighborSrc = 0, neighborOut = 0;
    src.ProcessPixelRows(a => neighborSrc = a.GetRowSpan(10)[12].R);
    scaled.ProcessPixelRows(a => neighborOut = a.GetRowSpan(10)[12].R);
    Assert.That(neighborOut, Is.EqualTo(neighborSrc),
      "centre pixel position is preserved; off-centre samples come from a wider source neighborhood");
  }

  [Test]
  public void Apply_PerspectiveX_TranslatesContent() {
    // Bright dot at (5, 10). Positive X offset shifts it right in the
    // output → pixel that was bright in the source now becomes black at
    // its old position (because output samples from a different source).
    using var src = SolidColor(20, 20, new Rgba32(0, 0, 0, 255));
    src.ProcessPixelRows(a => a.GetRowSpan(10)[5] = new Rgba32(255, 255, 255, 255));
    using var translated = ImageDeveloper.Apply(src, new DevelopSettings(PerspectiveX: 50));
    int wasBrightR = 0;
    translated.ProcessPixelRows(a => wasBrightR = a.GetRowSpan(10)[5].R);
    Assert.That(wasBrightR, Is.EqualTo(0),
      "+50 PerspectiveX shifts content right, so the dot's old position becomes black");
  }

  [Test]
  public void Apply_LensDistortionPositive_BendsPixelsRadially() {
    // Black image with a single bright dot near the corner. A radial
    // distortion shouldn't leave the dot in the same place.
    using var src = SolidColor(40, 40, new Rgba32(0, 0, 0, 255));
    src.ProcessPixelRows(a => a.GetRowSpan(4)[4] = new Rgba32(255, 255, 255, 255));
    using var distorted = ImageDeveloper.Apply(src, new DevelopSettings(LensManualDistortion: 100));
    // The bright source pixel at (4,4) won't fall onto (4,4) in the output —
    // a positive distortion samples from a contracted neighbourhood, so the
    // brightness near the corner should differ from the original location.
    var origR = 0;
    var distR = 0;
    src.ProcessPixelRows(a => origR = a.GetRowSpan(4)[4].R);
    distorted.ProcessPixelRows(a => distR = a.GetRowSpan(4)[4].R);
    Assert.That(distR, Is.LessThan(origR),
      "with positive distortion, the (4,4) output samples from a different source pixel and the dot smears off");
  }

  [Test]
  public void Apply_DefringePurple_DesaturatesPurpleEdgePixels() {
    // Pure purple-ish pixel (high saturation, hue ~280°).
    using var src = SolidColor(5, 5, new Rgba32(180, 80, 220, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(DefringePurpleAmount: 20));
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      var srcSpread = 220 - 80;
      var outSpread = Math.Max(px.R, Math.Max(px.G, px.B)) - Math.Min(px.R, Math.Min(px.G, px.B));
      Assert.That(outSpread, Is.LessThan(srcSpread),
        "DefringePurple at max should pull a saturated purple pixel toward grey");
    });
  }

  [Test]
  public void Apply_CameraCalibrationRedHue_RotatesRedDominantPixels() {
    using var src = SolidColor(5, 5, new Rgba32(220, 50, 50, 255));
    using var rotated = ImageDeveloper.Apply(src, new DevelopSettings(CalibrationRedHue: 100));
    int srcG = 50, dstG = 50;
    src.ProcessPixelRows(a => srcG = a.GetRowSpan(0)[0].G);
    rotated.ProcessPixelRows(a => dstG = a.GetRowSpan(0)[0].G);
    Assert.That(dstG, Is.Not.EqualTo(srcG),
      "calibration red-hue should rotate the colour of red-dominant pixels");
  }

  [Test]
  public void Apply_BwConversion_FlattensRgbChannels() {
    using var src = SolidColor(5, 5, new Rgba32(220, 50, 50, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(ConvertToGrayscale: true));
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      Assert.That(px.R, Is.EqualTo(px.G));
      Assert.That(px.G, Is.EqualTo(px.B));
    });
  }

  [Test]
  public void Apply_BwGrayMixerRed_DarkensRedDominantPixels() {
    using var src = SolidColor(5, 5, new Rgba32(220, 50, 50, 255));
    using var baseline = ImageDeveloper.Apply(src, new DevelopSettings(ConvertToGrayscale: true));
    using var darkened = ImageDeveloper.Apply(src, new DevelopSettings(ConvertToGrayscale: true, GrayMixerRed: -100));
    int baselineGray = 0, darkGray = 0;
    baseline.ProcessPixelRows(a => baselineGray = a.GetRowSpan(0)[0].R);
    darkened.ProcessPixelRows(a => darkGray = a.GetRowSpan(0)[0].R);
    Assert.That(darkGray, Is.LessThan(baselineGray),
      "GrayMixerRed=-100 should pull a red-dominant pixel toward black in B&W output");
  }

  [Test]
  public void Apply_ParametricCurve_LiftsShadowsBand() {
    using var src = SolidColor(5, 5, new Rgba32(40, 40, 40, 255));  // dark grey -> shadows band
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(ParametricShadows: 100));
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      Assert.That((int)px.R, Is.GreaterThan(40),
        "+100 ParametricShadows should lift dark pixels");
    });
  }

  [Test]
  public void Apply_ColorGradingHighlightTint_ShiftsBrightPixels() {
    using var src = SolidColor(5, 5, new Rgba32(220, 220, 220, 255));  // bright pixel
    var settings = new DevelopSettings(GradeHighlightHue: 240, GradeHighlightSat: 100);  // strong blue tint
    using var out_ = ImageDeveloper.Apply(src, settings);
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      Assert.That((int)px.B, Is.GreaterThan(px.R),
        "blue highlight tint should pull a bright pixel toward blue");
    });
  }

  [Test]
  public void Apply_VignetteHighlightContrast_SparesBrightPixels() {
    // 30×30 grey square with a bright pixel near the corner. With a
    // strong negative vignette, corners darken — but with HighlightContrast=100
    // the bright pixel should be spared more than its grey neighbour.
    using var src = SolidColor(30, 30, new Rgba32(80, 80, 80, 255));
    src.ProcessPixelRows(a => a.GetRowSpan(2)[2] = new Rgba32(240, 240, 240, 255));
    using var noProtect = ImageDeveloper.Apply(src, new DevelopSettings(VignetteAmount: -100));
    using var withProtect = ImageDeveloper.Apply(src, new DevelopSettings(VignetteAmount: -100, VignetteHighlightContrast: 100));
    int unprotectedR = 0, protectedR = 0;
    noProtect.ProcessPixelRows(a => unprotectedR = a.GetRowSpan(2)[2].R);
    withProtect.ProcessPixelRows(a => protectedR = a.GetRowSpan(2)[2].R);
    Assert.That(protectedR, Is.GreaterThan(unprotectedR),
      "highlight protect should keep the bright corner pixel brighter than without protection");
  }

  [Test]
  public void Apply_GrainSize_LargerProducesChunkierPattern() {
    using var src = SolidColor(40, 40, new Rgba32(128, 128, 128, 255));
    using var fineGrain = ImageDeveloper.Apply(src, new DevelopSettings(GrainAmount: 100, GrainSize: 0));
    using var coarseGrain = ImageDeveloper.Apply(src, new DevelopSettings(GrainAmount: 100, GrainSize: 100));
    // Coarser grain creates blocks of correlated pixels: adjacent samples
    // tend to share the same noise value. Compare neighbour differences.
    var fineDiffSum = 0L;
    var coarseDiffSum = 0L;
    fineGrain.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height - 1; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length - 1; x++)
          fineDiffSum += Math.Abs(row[x].R - row[x + 1].R);
      }
    });
    coarseGrain.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height - 1; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length - 1; x++)
          coarseDiffSum += Math.Abs(row[x].R - row[x + 1].R);
      }
    });
    Assert.That(coarseDiffSum, Is.LessThan(fineDiffSum),
      "with the same amount but bigger size, neighbours share noise so total adjacent-pixel difference drops");
  }

  [Test]
  public void Apply_LumNrDetail_PreservesSharperEdgesThanPlainSmoothness() {
    // High-contrast image: half white, half black.
    using var src = SolidColor(20, 20, new Rgba32(255, 255, 255, 255));
    src.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length / 2; x++) row[x] = new Rgba32(0, 0, 0, 255);
      }
    });
    using var blurred = ImageDeveloper.Apply(src, new DevelopSettings(SmoothnessPercent: 100));
    using var blurredWithDetail = ImageDeveloper.Apply(src, new DevelopSettings(SmoothnessPercent: 100, LuminanceNrDetail: 100));
    int blurredEdge = 0, preservedEdge = 0;
    blurred.ProcessPixelRows(a => blurredEdge = a.GetRowSpan(10)[10].R);
    blurredWithDetail.ProcessPixelRows(a => preservedEdge = a.GetRowSpan(10)[10].R);
    Assert.That(Math.Abs(preservedEdge - 255), Is.LessThan(Math.Abs(blurredEdge - 255)),
      "LumNrDetail=100 should preserve the white side better than plain smoothness");
  }

  [Test]
  public void Apply_VignetteNegative_DarkensCorners() {
    using var src = SolidColor(20, 20, new Rgba32(200, 200, 200, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(VignetteAmount: -100));
    out_.ProcessPixelRows(a => {
      var center = a.GetRowSpan(10)[10];
      var corner = a.GetRowSpan(0)[0];
      Assert.That((int)corner.R, Is.LessThan(center.R),
        "negative vignette should darken corners more than the center");
    });
  }

  [Test]
  public void Apply_GrainAmount_AddsPerPixelVariation() {
    using var src = SolidColor(20, 20, new Rgba32(128, 128, 128, 255));
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(GrainAmount: 100));
    var min = 255; var max = 0;
    out_.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          if (row[x].R < min) min = row[x].R;
          if (row[x].R > max) max = row[x].R;
        }
      }
    });
    Assert.That(max - min, Is.GreaterThan(5),
      "grain should produce per-pixel variation across an originally flat region");
  }

  [Test]
  public void Apply_HslRedHueShift_RotatesRedTowardYellow() {
    // Pure red → expect green channel to rise (red rotated toward yellow).
    using var src = SolidColor(5, 5, new Rgba32(220, 50, 50, 255));
    var hueShifts = new double[] { 100, 0, 0, 0, 0, 0, 0, 0 };  // +100 on Red band → +30°
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(HslHueShifts: hueShifts));
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      Assert.That((int)px.G, Is.GreaterThan(50),
        "rotating red toward yellow should pull green up");
    });
  }

  [Test]
  public void Apply_HslSaturationDesaturatesOneBand() {
    using var src = SolidColor(5, 5, new Rgba32(220, 50, 50, 255));
    var satShifts = new double[] { -100, 0, 0, 0, 0, 0, 0, 0 };  // kill saturation on Red band
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(HslSaturationShifts: satShifts));
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      var spread = Math.Max(px.R, Math.Max(px.G, px.B)) - Math.Min(px.R, Math.Min(px.G, px.B));
      Assert.That(spread, Is.LessThan(40),
        "Red-band saturation -100 should pull the pixel toward grayscale");
    });
  }

  [Test]
  public void Apply_CropRectangle_ShrinksOutput() {
    using var src = SolidColor(100, 60, new Rgba32(128, 128, 128, 255));
    var settings = new DevelopSettings(CropLeft: 0.25, CropTop: 0.25, CropRight: 0.75, CropBottom: 0.75);
    using var out_ = ImageDeveloper.Apply(src, settings);
    Assert.Multiple(() => {
      Assert.That(out_.Width,  Is.EqualTo(50));
      Assert.That(out_.Height, Is.EqualTo(30));
    });
  }

  [Test]
  public void Apply_RedChannelCurveDarkening_OnlyDropsRed() {
    using var src = SolidColor(5, 5, new Rgba32(200, 200, 200, 255));
    var redCurveDown = new[] { new CurvePoint(0, 0), new CurvePoint(0.5, 0.25), new CurvePoint(1, 1) };
    using var out_ = ImageDeveloper.Apply(src, new DevelopSettings(RedCurvePoints: redCurveDown));
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      Assert.That((int)px.R, Is.LessThan(200), "red channel should drop");
      Assert.That((int)px.G, Is.EqualTo(200), "green untouched");
      Assert.That((int)px.B, Is.EqualTo(200), "blue untouched");
    });
  }

  [Test]
  public void Apply_ToneCurveDarkening_DecreasesBrightness() {
    // Pull midgray down via curve.
    var pts = new[] { new CurvePoint(0, 0), new CurvePoint(0.5, 0.25), new CurvePoint(1, 1) };
    var settings = new DevelopSettings(ToneCurvePoints: pts, ToneCurveInterpolation: CurveInterpolation.Linear);
    using var src = SolidColor(5, 5, new Rgba32(128, 128, 128, 255));
    using var out_ = ImageDeveloper.Apply(src, settings);
    out_.ProcessPixelRows(a => {
      var px = a.GetRowSpan(0)[0];
      Assert.That((int)px.R, Is.LessThan(100));
    });
  }
}
