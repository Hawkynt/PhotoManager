using FileFormat.JpegArchive;
using PhotoManager.Core.Develop;
using PhotoManager.Tests.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Tests.Unit.Develop;

[TestFixture]
public class DevelopMetadataStoreTests {
  private DirectoryInfo _tempDir = null!;

  [SetUp]
  public void SetUp() {
    this._tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(),
      "PhotoManagerDevelopMetaTests_" + Guid.NewGuid().ToString("N")));
    this._tempDir.Create();
  }

  [TearDown]
  public void TearDown() {
    try { this._tempDir.Delete(recursive: true); } catch { /* best effort */ }
  }

  private FileInfo NewJpeg(string name = "photo.jpg") {
    var path = Path.Combine(this._tempDir.FullName, name);
    TestJpegFactory.Write(path, exifSubIfdDateTimeOriginal: new DateTime(2024, 1, 1, 12, 0, 0));
    return new FileInfo(path);
  }

  [Test]
  public async Task LoadAsync_NoXmp_ReturnsNull() {
    var file = this.NewJpeg();
    Assert.That(await DevelopMetadataStore.LoadAsync(file), Is.Null);
  }

  [Test]
  public async Task SaveThenLoad_RoundTripsAcrossClose() {
    var file = this.NewJpeg();
    var redCurve = new[] { new CurvePoint(0, 0), new CurvePoint(0.5, 0.4), new CurvePoint(1, 1) };
    var settings = new DevelopSettings(
      ExposureStops: 0.5,
      ContrastPercent: 25,
      DehazePercent: 30,
      SharpenRadius: 1.4,
      SharpenDetail: 60,
      SharpenMasking: 25,
      RedGain: 7,
      SmoothnessPercent: 12,
      ToneCurvePoints: new[] { new CurvePoint(0, 0), new CurvePoint(0.5, 0.6), new CurvePoint(1, 1) },
      ToneCurveInterpolation: CurveInterpolation.CatmullRom,
      RedCurvePoints: redCurve);

    Assert.That(await DevelopMetadataStore.SaveAsync(file, settings), Is.True);
    var loaded = await DevelopMetadataStore.LoadAsync(file);

    Assert.That(loaded, Is.Not.Null);
    // crs: round-trip
    Assert.That(loaded!.ExposureStops,    Is.EqualTo(0.5).Within(0.01));
    Assert.That(loaded.ContrastPercent,   Is.EqualTo(25));
    Assert.That(loaded.DehazePercent,     Is.EqualTo(30));
    Assert.That(loaded.SharpenRadius,     Is.EqualTo(1.4).Within(0.05));
    Assert.That(loaded.SharpenDetail,     Is.EqualTo(60));
    Assert.That(loaded.SharpenMasking,    Is.EqualTo(25));
    // pm:developSettings extras
    Assert.That(loaded.RedGain,                Is.EqualTo(7));
    Assert.That(loaded.SmoothnessPercent,      Is.EqualTo(12));
    Assert.That(loaded.ToneCurveInterpolation, Is.EqualTo(CurveInterpolation.CatmullRom));
    Assert.That(loaded.ToneCurvePoints!.Count, Is.EqualTo(3));
    Assert.That(loaded.RedCurvePoints,         Is.Not.Null);
    Assert.That(loaded.RedCurvePoints!.Count,  Is.EqualTo(3));
  }

  [Test]
  public async Task PmBlobOnlyContainsExtras_NotCrsCoveredFields() {
    var file = this.NewJpeg();
    var settings = new DevelopSettings(
      ExposureStops: 0.5,
      ContrastPercent: 25,
      RedGain: 7,
      ToneCurveInterpolation: CurveInterpolation.Bezier);

    Assert.That(await DevelopMetadataStore.SaveAsync(file, settings), Is.True);

    var xmp = JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName));
    var xml = System.Text.Encoding.UTF8.GetString(xmp!);
    var pmBlob = ExtractPmDevelopSettings(xml);

    Assert.That(pmBlob, Is.Not.Null, "pm:developSettings missing");
    Assert.That(pmBlob, Does.Contain("redGain"));           // extras
    Assert.That(pmBlob, Does.Contain("toneCurveInterpolation"));
    Assert.That(pmBlob, Does.Not.Contain("exposureStops"),  // crs-covered
      "exposureStops shouldn't duplicate into pm:developSettings");
    Assert.That(pmBlob, Does.Not.Contain("contrastPercent"));
  }

  [Test]
  public async Task LoadAsync_ReadsCrsOnlyFile_AsThirdPartyEditWould() {
    var file = this.NewJpeg();
    // Save once via PhotoManager so the file gets crs: + pm: tags...
    await DevelopMetadataStore.SaveAsync(file, new DevelopSettings(ExposureStops: 0.75, ContrastPercent: 40));
    // ...then strip the pm: blob to simulate a file edited in Lightroom.
    var bytes = File.ReadAllBytes(file.FullName);
    var xmp = JpegSegmentSurgery.TryReadXmpSegment(bytes)!;
    var xml = System.Text.Encoding.UTF8.GetString(xmp);
    var stripped = System.Text.RegularExpressions.Regex.Replace(xml,
      @"<pm:developSettings[^<]*</pm:developSettings>", "");
    var newJpeg = JpegSegmentSurgery.ReplaceXmpSegment(bytes, System.Text.Encoding.UTF8.GetBytes(stripped));
    await File.WriteAllBytesAsync(file.FullName, newJpeg);

    var loaded = await DevelopMetadataStore.LoadAsync(file);
    Assert.That(loaded, Is.Not.Null);
    Assert.That(loaded!.ExposureStops, Is.EqualTo(0.75).Within(0.01));
    Assert.That(loaded.ContrastPercent, Is.EqualTo(40));
  }

  [Test]
  public async Task Save_EmitsPerChannelCurveLuts() {
    var file = this.NewJpeg();
    var settings = new DevelopSettings(
      RedCurvePoints:   new[] { new CurvePoint(0, 0), new CurvePoint(0.5, 0.4), new CurvePoint(1, 1) },
      GreenCurvePoints: new[] { new CurvePoint(0, 0.05), new CurvePoint(1, 1) },
      BlueCurvePoints:  new[] { new CurvePoint(0, 0), new CurvePoint(1, 0.95) });

    Assert.That(await DevelopMetadataStore.SaveAsync(file, settings), Is.True);

    var xmp = JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName));
    var xml = System.Text.Encoding.UTF8.GetString(xmp!);
    Assert.That(xml, Does.Contain("crs:ToneCurvePV2012Red"));
    Assert.That(xml, Does.Contain("crs:ToneCurvePV2012Green"));
    Assert.That(xml, Does.Contain("crs:ToneCurvePV2012Blue"));
  }

  [Test]
  public async Task Save_EmitsLocalAdjustments_RoundTripsLinearAndRadial() {
    var file = this.NewJpeg();
    var settings = new DevelopSettings(LocalAdjustments: new[] {
      new LocalAdjustment(
        Mask: new LocalMask(LocalMaskType.Linear, X0: 0.5, Y0: 0.0, X1: 0.5, Y1: 1.0),
        Name: "Sky darkening",
        Exposure: -1.0,
        Highlights: -50),
      new LocalAdjustment(
        Mask: new LocalMask(LocalMaskType.Radial, CenterX: 0.4, CenterY: 0.5, RadiusX: 0.2, RadiusY: 0.3, Feather: 60),
        Name: "Subject pop",
        Exposure: 0.5,
        Saturation: 20)
    });

    Assert.That(await DevelopMetadataStore.SaveAsync(file, settings), Is.True);

    var xmp = JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName));
    var xml = System.Text.Encoding.UTF8.GetString(xmp!);
    Assert.That(xml, Does.Contain("crs:MaskGroupBasedCorrections"));
    Assert.That(xml, Does.Contain("Mask/Gradient"));
    Assert.That(xml, Does.Contain("Mask/CircularGradient"));
    Assert.That(xml, Does.Contain("LocalExposure2012"));
    Assert.That(xml, Does.Contain("LocalSaturation"));

    var loaded = await DevelopMetadataStore.LoadAsync(file);
    Assert.That(loaded, Is.Not.Null);
    Assert.That(loaded!.LocalAdjustments, Is.Not.Null);
    Assert.That(loaded.LocalAdjustments!.Count, Is.EqualTo(2));
    Assert.That(loaded.LocalAdjustments[0].Mask.Type, Is.EqualTo(LocalMaskType.Linear));
    Assert.That(loaded.LocalAdjustments[0].Exposure, Is.EqualTo(-1.0).Within(0.01));
    Assert.That(loaded.LocalAdjustments[0].Highlights, Is.EqualTo(-50).Within(0.5));
    Assert.That(loaded.LocalAdjustments[1].Mask.Type, Is.EqualTo(LocalMaskType.Radial));
    Assert.That(loaded.LocalAdjustments[1].Mask.RadiusY, Is.EqualTo(0.3).Within(0.01));
    Assert.That(loaded.LocalAdjustments[1].Saturation, Is.EqualTo(20).Within(0.5));
  }

  [Test]
  public async Task Save_EmitsSubMasksAndRanges_RoundTripsCleanly() {
    var file = this.NewJpeg();
    var settings = new DevelopSettings(LocalAdjustments: new[] {
      new LocalAdjustment(
        Mask: new LocalMask(LocalMaskType.Linear,
          X0: 0.0, Y0: 0.5, X1: 1.0, Y1: 0.5,
          LuminanceRangeMin: 0.4, LuminanceRangeMax: 0.95, LuminanceRangeFeather: 0.05,
          HueRangeMin: 0.55, HueRangeMax: 0.75, HueRangeFeather: 0.02),
        SubMasks: new[] {
          new LocalMask(
            Type: LocalMaskType.Radial,
            CenterX: 0.4, CenterY: 0.5,
            RadiusX: 0.1, RadiusY: 0.1,
            Feather: 50,
            Combine: MaskCombineOp.Subtract),
          new LocalMask(
            Type: LocalMaskType.Brush,
            BrushDabs: new[] { new BrushDab(0.6, 0.6, 0.05, 1.0) },
            Combine: MaskCombineOp.Intersect)
        },
        Exposure: 0.3)
    });

    Assert.That(await DevelopMetadataStore.SaveAsync(file, settings), Is.True);
    var xmp = JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName));
    var xml = System.Text.Encoding.UTF8.GetString(xmp!);
    Assert.That(xml, Does.Contain("LumRangeMin"));
    Assert.That(xml, Does.Contain("HueRangeMin"));
    Assert.That(xml, Does.Contain("MaskValue=\"-1\""),  "subtract sub-mask should emit MaskValue=-1");
    Assert.That(xml, Does.Contain("CombineOp=\"Intersect\""), "intersect sub-mask should emit our pm:CombineOp marker");

    var loaded = await DevelopMetadataStore.LoadAsync(file);
    Assert.That(loaded, Is.Not.Null);
    var loadedAdj = loaded!.LocalAdjustments![0];
    Assert.That(loadedAdj.Mask.LuminanceRangeMin,   Is.EqualTo(0.4).Within(0.001));
    Assert.That(loadedAdj.Mask.HueRangeMax,         Is.EqualTo(0.75).Within(0.001));
    Assert.That(loadedAdj.SubMasks, Is.Not.Null);
    Assert.That(loadedAdj.SubMasks!.Count, Is.EqualTo(2));
    Assert.That(loadedAdj.SubMasks[0].Combine, Is.EqualTo(MaskCombineOp.Subtract));
    Assert.That(loadedAdj.SubMasks[1].Combine, Is.EqualTo(MaskCombineOp.Intersect));
    Assert.That(loadedAdj.SubMasks[1].Type,    Is.EqualTo(LocalMaskType.Brush));
  }

  [Test]
  public async Task Save_EmitsBrushMask_RoundTripsDabs() {
    var file = this.NewJpeg();
    var dabs = new[] {
      new BrushDab(0.30, 0.40, 0.05, 1.0),
      new BrushDab(0.32, 0.42, 0.05, 0.5),
      new BrushDab(0.35, 0.45, 0.05, -0.8)  // eraser
    };
    var settings = new DevelopSettings(LocalAdjustments: new[] {
      new LocalAdjustment(
        Mask: new LocalMask(Type: LocalMaskType.Brush, BrushDabs: dabs),
        Name: "Painted dodge",
        Exposure: 0.7)
    });

    Assert.That(await DevelopMetadataStore.SaveAsync(file, settings), Is.True);

    var xmp = JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName));
    var xml = System.Text.Encoding.UTF8.GetString(xmp!);
    Assert.That(xml, Does.Contain("Mask/Brush"));
    Assert.That(xml, Does.Contain("crs:Dabs"));

    var loaded = await DevelopMetadataStore.LoadAsync(file);
    Assert.That(loaded, Is.Not.Null);
    Assert.That(loaded!.LocalAdjustments, Is.Not.Null);
    var loadedMask = loaded.LocalAdjustments![0].Mask;
    Assert.That(loadedMask.Type, Is.EqualTo(LocalMaskType.Brush));
    Assert.That(loadedMask.BrushDabs, Is.Not.Null);
    Assert.That(loadedMask.BrushDabs!.Count, Is.EqualTo(3));
    Assert.That(loadedMask.BrushDabs[0].X, Is.EqualTo(0.30).Within(0.001));
    Assert.That(loadedMask.BrushDabs[1].Flow, Is.EqualTo(0.5).Within(0.05));
    Assert.That(loadedMask.BrushDabs[2].Flow, Is.LessThan(0),
      "eraser dab should round-trip with negative flow");
  }

  [Test]
  public async Task Save_PreservesForeignAiSubjectMaskLi() {
    // Write a PhotoManager Linear adjustment, then inject a synthetic AI
    // subject-mask li (Adobe's Mask/Image — we don't model it) directly
    // into the saved XMP, then save again. The AI mask li must survive
    // even though we can't render it.
    var file = this.NewJpeg();
    var initial = new DevelopSettings(LocalAdjustments: new[] {
      new LocalAdjustment(Mask: new LocalMask(LocalMaskType.Linear), Exposure: 0.5)
    });
    Assert.That(await DevelopMetadataStore.SaveAsync(file, initial), Is.True);

    var bytes = File.ReadAllBytes(file.FullName);
    var xmp = JpegSegmentSurgery.TryReadXmpSegment(bytes)!;
    var xml = System.Text.Encoding.UTF8.GetString(xmp);
    var aiLi =
      "<rdf:li><rdf:Description crs:What=\"Correction\" crs:CorrectionAmount=\"1\" crs:LocalExposure2012=\"0.25\">" +
        "<crs:CorrectionMasks><rdf:Seq><rdf:li>" +
          "<rdf:Description crs:What=\"Mask/Image\" crs:MaskValue=\"1\" crs:MaskSubType=\"1\" crs:ReferencePoint=\"0.5 0.5\"/>" +
        "</rdf:li></rdf:Seq></crs:CorrectionMasks>" +
      "</rdf:Description></rdf:li>";
    var injected = System.Text.RegularExpressions.Regex.Replace(xml,
      @"(</rdf:Seq>\s*</crs:MaskGroupBasedCorrections>)",
      aiLi + "$1");
    Assert.That(injected, Does.Contain("Mask/Image"),
      "preflight: regex injection must produce AI subject-mask li");
    var withForeign = JpegSegmentSurgery.ReplaceXmpSegment(bytes, System.Text.Encoding.UTF8.GetBytes(injected));
    await File.WriteAllBytesAsync(file.FullName, withForeign);

    var diskXml = System.Text.Encoding.UTF8.GetString(JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName))!);
    Assert.That(diskXml, Does.Contain("Mask/Image"),
      "preflight: file on disk must have the AI mask li before the second save");

    var loaded = await DevelopMetadataStore.LoadAsync(file);
    Assert.That(loaded, Is.Not.Null);
    Assert.That(loaded!.LocalAdjustments!.Count, Is.EqualTo(1),
      "load should see only the modeled Linear; the AI mask is foreign");
    var nextSettings = loaded with { ExposureStops = 0.5 };
    Assert.That(await DevelopMetadataStore.SaveAsync(file, nextSettings), Is.True);

    var afterXml = System.Text.Encoding.UTF8.GetString(JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName))!);
    Assert.That(afterXml, Does.Contain("Mask/Image"),
      "AI subject-mask li must round-trip through PhotoManager save");
  }

  [Test]
  public async Task Save_EmitsPerspective_RoundTrips() {
    var file = this.NewJpeg();
    var settings = new DevelopSettings(
      PerspectiveVertical: 25, PerspectiveHorizontal: -15,
      PerspectiveRotate: 1.5, PerspectiveScale: 110, PerspectiveAspect: 8,
      PerspectiveX: -5, PerspectiveY: 10);

    Assert.That(await DevelopMetadataStore.SaveAsync(file, settings), Is.True);

    var xmp = JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName));
    var xml = System.Text.Encoding.UTF8.GetString(xmp!);
    Assert.That(xml, Does.Contain("crs:PerspectiveVertical"));
    Assert.That(xml, Does.Contain("crs:PerspectiveScale"));
    Assert.That(xml, Does.Contain("crs:PerspectiveAspect"));
    Assert.That(xml, Does.Contain("crs:PerspectiveX"));

    var loaded = await DevelopMetadataStore.LoadAsync(file);
    Assert.That(loaded, Is.Not.Null);
    Assert.That(loaded!.PerspectiveVertical,   Is.EqualTo(25));
    Assert.That(loaded.PerspectiveHorizontal,  Is.EqualTo(-15));
    Assert.That(loaded.PerspectiveScale,       Is.EqualTo(110));
    Assert.That(loaded.PerspectiveAspect,      Is.EqualTo(8));
    Assert.That(loaded.PerspectiveX,           Is.EqualTo(-5));
    Assert.That(loaded.PerspectiveY,           Is.EqualTo(10));
  }

  [Test]
  public async Task Save_EmitsLensCalibrationDefringe_RoundTrips() {
    var file = this.NewJpeg();
    var settings = new DevelopSettings(
      LensManualDistortion: -25,
      ChromaticAberrationR: 5, ChromaticAberrationB: -3,
      DefringePurpleAmount: 8, DefringeGreenAmount: 4,
      CalibrationRedHue: 12,  CalibrationRedSaturation: -5,
      CalibrationGreenHue: -8, CalibrationGreenSaturation: 10,
      CalibrationBlueHue: 6,  CalibrationBlueSaturation: 4);

    Assert.That(await DevelopMetadataStore.SaveAsync(file, settings), Is.True);

    var xmp = JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName));
    var xml = System.Text.Encoding.UTF8.GetString(xmp!);
    Assert.That(xml, Does.Contain("crs:LensManualDistortionAmount"));
    Assert.That(xml, Does.Contain("crs:ChromaticAberrationR"));
    Assert.That(xml, Does.Contain("crs:ChromaticAberrationB"));
    Assert.That(xml, Does.Contain("crs:DefringePurpleAmount"));
    Assert.That(xml, Does.Contain("crs:DefringeGreenAmount"));
    Assert.That(xml, Does.Contain("crs:RedHue"));
    Assert.That(xml, Does.Contain("crs:RedSaturation"));
    Assert.That(xml, Does.Contain("crs:GreenHue"));
    Assert.That(xml, Does.Contain("crs:BlueSaturation"));

    var loaded = await DevelopMetadataStore.LoadAsync(file);
    Assert.That(loaded, Is.Not.Null);
    Assert.That(loaded!.LensManualDistortion, Is.EqualTo(-25));
    Assert.That(loaded.ChromaticAberrationR,  Is.EqualTo(5));
    Assert.That(loaded.ChromaticAberrationB,  Is.EqualTo(-3));
    Assert.That(loaded.DefringePurpleAmount,  Is.EqualTo(8));
    Assert.That(loaded.DefringeGreenAmount,   Is.EqualTo(4));
    Assert.That(loaded.CalibrationRedHue,     Is.EqualTo(12));
    Assert.That(loaded.CalibrationGreenHue,   Is.EqualTo(-8));
    Assert.That(loaded.CalibrationBlueSaturation, Is.EqualTo(4));
  }

  [Test]
  public async Task Save_EmitsBwSplitToningParametric_RoundTrips() {
    var file = this.NewJpeg();
    var settings = new DevelopSettings(
      ConvertToGrayscale: true,
      GrayMixerRed: 30, GrayMixerOrange: -20, GrayMixerBlue: 50,
      SplitToningShadowHue: 220, SplitToningShadowSaturation: 25,
      SplitToningHighlightHue: 60, SplitToningHighlightSaturation: 15,
      SplitToningBalance: -10,
      ParametricShadows: -15, ParametricDarks: 10,
      ParametricLights: -5, ParametricHighlights: 20,
      ParametricShadowSplit: 30, ParametricMidtoneSplit: 55, ParametricHighlightSplit: 80);

    Assert.That(await DevelopMetadataStore.SaveAsync(file, settings), Is.True);

    var xmp = JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName));
    var xml = System.Text.Encoding.UTF8.GetString(xmp!);
    Assert.That(xml, Does.Contain("crs:ConvertToGrayscale"));
    Assert.That(xml, Does.Contain("crs:GrayMixerRed"));
    Assert.That(xml, Does.Contain("crs:GrayMixerBlue"));
    Assert.That(xml, Does.Contain("crs:SplitToningShadowHue"));
    Assert.That(xml, Does.Contain("crs:SplitToningHighlightSaturation"));
    Assert.That(xml, Does.Contain("crs:SplitToningBalance"));
    Assert.That(xml, Does.Contain("crs:ParametricShadows"));
    Assert.That(xml, Does.Contain("crs:ParametricHighlightSplit"));

    var loaded = await DevelopMetadataStore.LoadAsync(file);
    Assert.That(loaded, Is.Not.Null);
    Assert.That(loaded!.ConvertToGrayscale,             Is.True);
    Assert.That(loaded.GrayMixerRed,                    Is.EqualTo(30));
    Assert.That(loaded.GrayMixerBlue,                   Is.EqualTo(50));
    Assert.That(loaded.SplitToningShadowHue,            Is.EqualTo(220));
    Assert.That(loaded.SplitToningShadowSaturation,     Is.EqualTo(25));
    Assert.That(loaded.SplitToningHighlightHue,         Is.EqualTo(60));
    Assert.That(loaded.SplitToningBalance,              Is.EqualTo(-10));
    Assert.That(loaded.ParametricShadows,               Is.EqualTo(-15));
    Assert.That(loaded.ParametricHighlightSplit,        Is.EqualTo(80));
  }

  [Test]
  public async Task Save_PreservesUnknownCrsTags_AsThirdPartyToolWroteThem() {
    // This is the contract that lets us claim "completely on par": Lightroom
    // / ACR write dozens of crs: fields we don't model (calibration, lens
    // profile path, perspective transforms, AI mask groups, etc.). Those tags
    // must travel with the photo unchanged through PhotoManager save cycles.
    var file = this.NewJpeg();
    await DevelopMetadataStore.SaveAsync(file, new DevelopSettings(ExposureStops: 0.5));

    // Inject some Adobe-only tags that nobody in our model claims.
    var bytes = File.ReadAllBytes(file.FullName);
    var xmp = JpegSegmentSurgery.TryReadXmpSegment(bytes)!;
    var xml = System.Text.Encoding.UTF8.GetString(xmp);
    var injected = xml.Replace("</rdf:Description>",
      "<crs:ColorMode>2</crs:ColorMode>" +
      "<crs:CameraProfile>Camera Standard</crs:CameraProfile>" +
      "<crs:LensProfileFilename>Sigma 35mm.lcp</crs:LensProfileFilename>" +
      "<crs:PerspectiveAuto>Off</crs:PerspectiveAuto>" +
      "<crs:LookName>My Custom Look</crs:LookName>" +
      "</rdf:Description>");
    var withForeign = JpegSegmentSurgery.ReplaceXmpSegment(bytes, System.Text.Encoding.UTF8.GetBytes(injected));
    await File.WriteAllBytesAsync(file.FullName, withForeign);

    // Save again through PhotoManager — this is where stripping would
    // happen if the store were too aggressive.
    await DevelopMetadataStore.SaveAsync(file, new DevelopSettings(ExposureStops: 0.75));

    var afterXml = System.Text.Encoding.UTF8.GetString(JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName))!);
    Assert.Multiple(() => {
      Assert.That(afterXml, Does.Contain("crs:ColorMode"),            "ColorMode must survive PhotoManager save");
      Assert.That(afterXml, Does.Contain("Camera Standard"),          "CameraProfile must survive");
      Assert.That(afterXml, Does.Contain("crs:LensProfileFilename"),  "lens profile path must survive");
      Assert.That(afterXml, Does.Contain("crs:PerspectiveAuto"),      "perspective transform must survive");
      Assert.That(afterXml, Does.Contain("crs:LookName"),             "custom look must survive");
      Assert.That(afterXml, Does.Contain("My Custom Look"));
    });
  }

  [Test]
  public async Task Save_EmitsColorGrading_VignetteGrain_NR_RoundTrips() {
    var file = this.NewJpeg();
    var settings = new DevelopSettings(
      GradeShadowHue: 220, GradeShadowSat: 30, GradeShadowLum: -10,
      GradeHighlightHue: 50, GradeHighlightSat: 25, GradeHighlightLum: 8,
      GradeGlobalHue: 180, GradeGlobalSat: 5,
      VignetteAmount: -40, VignetteMidpoint: 60, VignetteFeather: 70,
      GrainAmount: 25, GrainSize: 30,
      ColorNoiseReduction: 35, LuminanceNrDetail: 60);

    Assert.That(await DevelopMetadataStore.SaveAsync(file, settings), Is.True);

    var xmp = JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName));
    var xml = System.Text.Encoding.UTF8.GetString(xmp!);
    Assert.That(xml, Does.Contain("crs:ColorGradeShadowHue"));
    Assert.That(xml, Does.Contain("crs:ColorGradeHighlightSat"));
    Assert.That(xml, Does.Contain("crs:PostCropVignetteAmount"));
    Assert.That(xml, Does.Contain("crs:GrainAmount"));
    Assert.That(xml, Does.Contain("crs:ColorNoiseReduction"));
    Assert.That(xml, Does.Contain("crs:LuminanceNoiseReductionDetail"));

    var loaded = await DevelopMetadataStore.LoadAsync(file);
    Assert.That(loaded, Is.Not.Null);
    Assert.That(loaded!.GradeShadowHue,        Is.EqualTo(220));
    Assert.That(loaded.GradeShadowSat,         Is.EqualTo(30));
    Assert.That(loaded.GradeHighlightLum,      Is.EqualTo(8));
    Assert.That(loaded.VignetteAmount,         Is.EqualTo(-40));
    Assert.That(loaded.VignetteMidpoint,       Is.EqualTo(60));
    Assert.That(loaded.GrainAmount,            Is.EqualTo(25));
    Assert.That(loaded.ColorNoiseReduction,    Is.EqualTo(35));
    Assert.That(loaded.LuminanceNrDetail,      Is.EqualTo(60));
  }

  [Test]
  public async Task Save_EmitsHslAndCrop_RoundTripsThroughLoad() {
    var file = this.NewJpeg();
    var hueShifts = new double[] { 50, 0, -25, 0, 0, 30, 0, 0 };
    var settings = new DevelopSettings(
      HslHueShifts: hueShifts,
      HslSaturationShifts: new double[] { 0, 0, 0, 40, 0, 0, 0, 0 },
      CropAngleDegrees: 2.5,
      CropLeft: 0.05, CropTop: 0.10, CropRight: 0.95, CropBottom: 0.90);

    Assert.That(await DevelopMetadataStore.SaveAsync(file, settings), Is.True);

    var xmp = JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName));
    var xml = System.Text.Encoding.UTF8.GetString(xmp!);
    // Adobe-readable HSL + crop tags.
    Assert.That(xml, Does.Contain("crs:HueAdjustmentRed"));
    Assert.That(xml, Does.Contain("crs:HueAdjustmentYellow"));
    Assert.That(xml, Does.Contain("crs:HueAdjustmentBlue"));
    Assert.That(xml, Does.Contain("crs:SaturationAdjustmentGreen"));
    Assert.That(xml, Does.Contain("crs:HasCrop"));
    Assert.That(xml, Does.Contain("crs:CropAngle"));

    var loaded = await DevelopMetadataStore.LoadAsync(file);
    Assert.That(loaded, Is.Not.Null);
    Assert.That(loaded!.HslHueShifts,        Is.Not.Null);
    Assert.That(loaded.HslHueShifts![0],     Is.EqualTo(50));
    Assert.That(loaded.HslHueShifts![2],     Is.EqualTo(-25));
    Assert.That(loaded.HslHueShifts![5],     Is.EqualTo(30));
    Assert.That(loaded.HslSaturationShifts!, Is.Not.Null);
    Assert.That(loaded.HslSaturationShifts![3], Is.EqualTo(40));
    Assert.That(loaded.CropAngleDegrees,     Is.EqualTo(2.5).Within(0.01));
    Assert.That(loaded.CropLeft,             Is.EqualTo(0.05).Within(0.001));
    Assert.That(loaded.CropRight,            Is.EqualTo(0.95).Within(0.001));
  }

  [Test]
  public async Task Save_EmitsDehazeAndSharpenSubparams() {
    var file = this.NewJpeg();
    var settings = new DevelopSettings(
      DehazePercent: 35,
      SharpeningAmount: 50,
      SharpenRadius: 1.2,
      SharpenDetail: 75,
      SharpenMasking: 30);

    await DevelopMetadataStore.SaveAsync(file, settings);
    var xmp = JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName));
    var xml = System.Text.Encoding.UTF8.GetString(xmp!);
    Assert.That(xml, Does.Contain("crs:Dehaze"));
    Assert.That(xml, Does.Contain("crs:SharpenRadius"));
    Assert.That(xml, Does.Contain("crs:SharpenDetail"));
    Assert.That(xml, Does.Contain("crs:SharpenEdgeMasking"));
  }

  private static string? ExtractPmDevelopSettings(string xml) {
    var m = System.Text.RegularExpressions.Regex.Match(xml,
      @"<pm:developSettings[^>]*>([^<]*)</pm:developSettings>");
    return m.Success ? m.Groups[1].Value : null;
  }

  [Test]
  public async Task Save_IdentitySettings_RemovesElement() {
    var file = this.NewJpeg();
    await DevelopMetadataStore.SaveAsync(file, new DevelopSettings(ExposureStops: 1.0));
    Assert.That(await DevelopMetadataStore.LoadAsync(file), Is.Not.Null);

    await DevelopMetadataStore.SaveAsync(file, new DevelopSettings());
    Assert.That(await DevelopMetadataStore.LoadAsync(file), Is.Null);
  }

  [Test]
  public async Task Save_EmitsAdobeCrsTagsForThirdPartyTools() {
    var file = this.NewJpeg();
    var settings = new DevelopSettings(
      ExposureStops: 1.25,
      ContrastPercent: 30,
      HighlightsPercent: -40,
      ShadowsPercent: 25,
      VibrancePercent: 15,
      TemperatureShift: 50,
      TintShift: -10);

    Assert.That(await DevelopMetadataStore.SaveAsync(file, settings), Is.True);

    var xmpBytes = JpegSegmentSurgery.TryReadXmpSegment(File.ReadAllBytes(file.FullName));
    Assert.That(xmpBytes, Is.Not.Null);
    var xml = System.Text.Encoding.UTF8.GetString(xmpBytes!);

    // Lightroom / Bridge / ACR look for these tag names directly. Spot-check
    // the ones that round-trip cleanly so a 3rd-party tool has a useful
    // approximation of the edits even if it can't read pm:developSettings.
    // Spot-check the crs: tags Lightroom & friends look for.
    Assert.That(xml, Does.Contain("crs:Exposure2012"));
    Assert.That(xml, Does.Contain("+1.25"));
    Assert.That(xml, Does.Contain("crs:Contrast2012"));
    Assert.That(xml, Does.Contain("crs:Highlights2012"));
    Assert.That(xml, Does.Contain("crs:Shadows2012"));
    Assert.That(xml, Does.Contain("crs:Vibrance"));
    Assert.That(xml, Does.Contain("crs:Temperature"));  // Kelvin
    Assert.That(xml, Does.Contain("crs:Tint"));
    // Process version stamp tells Lightroom these are 2012-process numbers.
    Assert.That(xml, Does.Contain("crs:ProcessVersion"));
  }

  [Test]
  public async Task Save_PreservesPixelDataByteForByte() {
    var file = this.NewJpeg();
    var originalScanData = ExtractScanData(File.ReadAllBytes(file.FullName));

    await DevelopMetadataStore.SaveAsync(file, new DevelopSettings(ExposureStops: 0.5));
    var afterScanData = ExtractScanData(File.ReadAllBytes(file.FullName));

    Assert.That(afterScanData, Is.EqualTo(originalScanData),
      "embedded develop write must not touch the JPEG scan data");
  }

  [Test]
  public async Task BakeThumbnail_WritesJpegOffsetAndLengthIntoIfd1() {
    var file = this.NewJpeg();
    using var preview = new Image<Rgba32>(800, 600);

    var result = await DevelopMetadataStore.BakeThumbnailAsync(file, preview, requestedLongEdge: 80, requestedQuality: 75);
    Assert.That(result.Success, Is.True);
    Assert.That(result.DidAutoFit, Is.False);
    Assert.That(result.Width,  Is.EqualTo(80));
    Assert.That(result.Height, Is.EqualTo(60));

    // The new IFD1 must point at a valid JPEG with the right length.
    var jpeg = File.ReadAllBytes(file.FullName);
    var exif = JpegSegmentSurgery.TryReadExifSegment(jpeg);
    Assert.That(exif, Is.Not.Null);
    var tiffArea = StripExifPrefix(exif!);
    var tiff = TiffReader.Parse(tiffArea);
    Assert.That(tiff.Ifd0.Next, Is.Not.Null, "IFD1 missing");

    var ifd1 = tiff.Ifd0.Next!;
    var offsetEntry = ifd1.FindEntry(TiffTags.JpegInterchangeFormat);
    var lengthEntry = ifd1.FindEntry(TiffTags.JpegInterchangeFormatLength);
    Assert.That(offsetEntry, Is.Not.Null);
    Assert.That(lengthEntry, Is.Not.Null);

    var offset = (int)TiffReader.ReadUnsignedLong(offsetEntry!.ValueBytes, tiff.LittleEndian);
    var length = (int)TiffReader.ReadUnsignedLong(lengthEntry!.ValueBytes, tiff.LittleEndian);
    Assert.That(length, Is.EqualTo(result.ThumbnailByteCount));

    // Bytes at offset must be a JPEG (start with FF D8).
    Assert.That(offset, Is.LessThan(tiffArea.Length));
    var atOffset = tiffArea.AsSpan(offset, length).ToArray();
    Assert.That(atOffset[0], Is.EqualTo((byte)0xFF));
    Assert.That(atOffset[1], Is.EqualTo((byte)0xD8));
  }

  [Test]
  public async Task BakeThumbnail_OverBudget_AutoFitsToHighestPsnr() {
    // 2048-edge q=100 thumbnail will way overrun the 64 KB EXIF cap, so the
    // fitter has to step down dimensions / quality.
    var file = this.NewJpeg();
    using var preview = MakeNoisyImage(1600, 1200);

    var result = await DevelopMetadataStore.BakeThumbnailAsync(file, preview, requestedLongEdge: 2048, requestedQuality: 100);
    Assert.That(result.Success, Is.True);
    Assert.That(result.DidAutoFit, Is.True);
    Assert.That(result.ExifPayloadByteCount, Is.LessThanOrEqualTo(JpegSegmentSurgery.MaxApp1PayloadBytes));
    Assert.That(result.Width,  Is.LessThan(2048));
    Assert.That(result.PsnrDb, Is.GreaterThan(20),
      "auto-fitted candidate should still be visually meaningful (>20 dB)");
  }

  /// <summary>
  /// Build an image with random pixels so JPEG can't compress it down to
  /// nothing — needed so the cap-overrun branch actually triggers.
  /// </summary>
  private static Image<Rgba32> MakeNoisyImage(int width, int height) {
    var img = new Image<Rgba32>(width, height);
    var rng = new Random(42);
    img.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++)
          row[x] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 255);
      }
    });
    return img;
  }

  private static readonly byte[] ExifHeader = System.Text.Encoding.ASCII.GetBytes("Exif\0\0");

  private static byte[] StripExifPrefix(byte[] exif) {
    if (exif.Length >= ExifHeader.Length && exif.AsSpan(0, ExifHeader.Length).SequenceEqual(ExifHeader))
      return exif.AsSpan(ExifHeader.Length).ToArray();
    return exif;
  }

  [Test]
  public async Task BakeThumbnail_PreservesIfd0EntriesFromOriginalExif() {
    var file = this.NewJpeg();
    var beforeExif = JpegSegmentSurgery.TryReadExifSegment(File.ReadAllBytes(file.FullName));
    Assert.That(beforeExif, Is.Not.Null);
    var beforeIfd0Count = TiffReader.Parse(StripExifPrefix(beforeExif!)).Ifd0.Entries.Count;

    using var preview = new Image<Rgba32>(40, 40);
    var result = await DevelopMetadataStore.BakeThumbnailAsync(file, preview, requestedLongEdge: 20, requestedQuality: 75);
    Assert.That(result.Success, Is.True);

    var afterExif = JpegSegmentSurgery.TryReadExifSegment(File.ReadAllBytes(file.FullName));
    var afterIfd0Count = TiffReader.Parse(StripExifPrefix(afterExif!)).Ifd0.Entries.Count;
    Assert.That(afterIfd0Count, Is.EqualTo(beforeIfd0Count),
      "IFD0 entry set should not change when only IFD1 is rebuilt");
  }

  /// <summary>
  /// Pull out the bytes between SOS and EOI — the actual compressed scan —
  /// so we can compare them across a metadata write.
  /// </summary>
  private static byte[] ExtractScanData(byte[] jpeg) {
    var i = 2;  // skip SOI
    while (i < jpeg.Length - 1) {
      if (jpeg[i] != 0xFF) { i++; continue; }
      var marker = jpeg[i + 1];
      i += 2;
      if (marker == 0xDA) {
        // Skip the SOS header length...
        var len = (jpeg[i] << 8) | jpeg[i + 1];
        i += len;
        // ...then read until EOI.
        var start = i;
        while (i < jpeg.Length - 1) {
          if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xD9)
            return jpeg.AsSpan(start, i - start).ToArray();
          i++;
        }
        return jpeg.AsSpan(start).ToArray();
      }
      if (marker is 0xD8 or 0xD9 or (>= 0xD0 and <= 0xD7))
        continue;
      var segLen = (jpeg[i] << 8) | jpeg[i + 1];
      i += segLen;
    }
    return Array.Empty<byte>();
  }
}
