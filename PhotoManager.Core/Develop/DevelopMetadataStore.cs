using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FileFormat.JpegArchive;
using PhotoManager.Core.Metadata.Containers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Develop;

/// <summary>
/// Reads / writes per-image develop settings inside the JPEG XMP packet. The
/// pixel data is never re-encoded — only the XMP APP1 segment is rewritten,
/// and surrounding XMP fields (rating, GPS, regions, foreign tools' tags)
/// are preserved verbatim.
///
/// Two encodings are emitted side-by-side:
/// <list type="bullet">
///   <item><description>Adobe <c>crs:</c> tags carry the settings that map
///   cleanly into Lightroom / Camera Raw / Bridge so 3rd-party tools render
///   a faithful approximation of the edits without us shipping our own
///   preset format.</description></item>
///   <item><description><c>pm:developSettings</c> stores ONLY the extras
///   that <c>crs:</c> can't model — Smoothness, per-channel R/G/B gains,
///   the original tone-curve control points + interpolation mode, and
///   in-pipeline rotation. This avoids duplicating values, so when the
///   user (or another tool) edits a crs: tag the pm: blob doesn't override
///   it on the next read.</description></item>
/// </list>
///
/// Non-JPEG containers currently no-op (see <see cref="SupportsContainer"/>).
/// </summary>
public static class DevelopMetadataStore {
  private static readonly XNamespace X = "adobe:ns:meta/";
  private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
  private static readonly XNamespace Pm = "https://hawkynt.github.io/PhotoManager/xmp/1.0/";
  private static readonly XNamespace Crs = "http://ns.adobe.com/camera-raw-settings/1.0/";

  private static readonly string[] JpegExtensions = { ".jpg", ".jpeg", ".jpe", ".jfif" };

  private static readonly JsonSerializerOptions JsonOptions = new() {
    WriteIndented = false,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
  };

  public static bool SupportsContainer(FileInfo? file)
    => file is not null && JpegExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// The portion of <see cref="DevelopSettings"/> that has no Adobe crs:
  /// counterpart. Stored verbatim as JSON in <c>pm:developSettings</c>; the
  /// rest of the settings are reconstructed from crs: tags.
  /// </summary>
  private sealed record DevelopExtras(
    int RotationDegrees = 0,
    double RedGain = 0,
    double GreenGain = 0,
    double BlueGain = 0,
    CurveInterpolation ToneCurveInterpolation = CurveInterpolation.Linear,
    IReadOnlyList<CurvePoint>? ToneCurvePoints = null,
    IReadOnlyList<CurvePoint>? RedCurvePoints = null,
    IReadOnlyList<CurvePoint>? GreenCurvePoints = null,
    IReadOnlyList<CurvePoint>? BlueCurvePoints = null,
    double LookOpacity = 1.0,
    string? WatermarkText = null,
    double WatermarkOpacity = 0.5,
    string WatermarkPosition = "BottomRight",
    int WatermarkFontSize = 24,
    double AiDenoiseStrength = 0,
    string? AiDenoiseModel = null,
    int AiUpscaleFactor = 1,
    string? AiUpscaleModel = null,
    double AiColorizeAmount = 0,
    string? AiColorizeModel = null
  ) {
    public bool IsEmpty =>
      this.RotationDegrees == 0
      && Math.Abs(this.RedGain) < 1e-6
      && Math.Abs(this.GreenGain) < 1e-6
      && Math.Abs(this.BlueGain) < 1e-6
      && this.ToneCurveInterpolation == CurveInterpolation.Linear
      && IsTrivialPoints(this.ToneCurvePoints)
      && IsTrivialPoints(this.RedCurvePoints)
      && IsTrivialPoints(this.GreenCurvePoints)
      && IsTrivialPoints(this.BlueCurvePoints)
      && Math.Abs(this.LookOpacity - 1.0) < 1e-6
      && string.IsNullOrEmpty(this.WatermarkText)
      && Math.Abs(this.AiDenoiseStrength) < 1e-6
      && this.AiUpscaleFactor <= 1;

    public static DevelopExtras From(DevelopSettings s) => new(
      s.RotationDegrees,
      s.RedGain, s.GreenGain, s.BlueGain,
      s.ToneCurveInterpolation, s.ToneCurvePoints,
      s.RedCurvePoints, s.GreenCurvePoints, s.BlueCurvePoints,
      s.LookOpacity,
      s.WatermarkText, s.WatermarkOpacity, s.WatermarkPosition, s.WatermarkFontSize,
      s.AiDenoiseStrength, s.AiDenoiseModel, s.AiUpscaleFactor, s.AiUpscaleModel,
      s.AiColorizeAmount, s.AiColorizeModel);

    private static bool IsTrivialPoints(IReadOnlyList<CurvePoint>? p)
      => p is null || p.Count < 2;
  }

  /// <summary>HSL band names in crs: order. The Adobe schema uses these
  /// exact suffixes for HueAdjustment / SaturationAdjustment / LuminanceAdjustment.</summary>
  private static readonly string[] HslBandNames =
    { "Red", "Orange", "Yellow", "Green", "Aqua", "Blue", "Purple", "Magenta" };

  /// <summary>
  /// Returns the persisted develop settings embedded in <paramref name="file"/>'s
  /// XMP, or null when no edits are recorded / the container isn't supported /
  /// the parsed settings are identity.
  /// </summary>
  public static Task<DevelopSettings?> LoadAsync(FileInfo file, CancellationToken cancellationToken = default)
    => LoadAsync(file, copyIndex: 0, cancellationToken);

  /// <summary>
  /// Copy-aware load. Copy 0 reads the embedded XMP packet inside
  /// <paramref name="file"/>; copy N (&gt; 0) reads the <c>basename.copyN.xmp</c>
  /// sidecar next to it.
  /// </summary>
  public static async Task<DevelopSettings?> LoadAsync(FileInfo file, int copyIndex, CancellationToken cancellationToken = default) {
    var description = await LoadXmpDescriptionAsync(file, copyIndex, cancellationToken);
    if (description is null)
      return null;

    var fromCrs = ReadCrsElements(description);
    var locals = ReadLocalAdjustments(description);
    fromCrs = fromCrs with { LocalAdjustments = locals };
    var extras = ReadExtras(description);
    var combined = ApplyExtras(fromCrs, extras);
    return combined.IsIdentity ? null : combined;
  }

  /// <summary>
  /// Returns the prior-snapshot stack persisted alongside the develop
  /// settings for the given copy. Newest first; empty when nothing has
  /// been saved yet. Identity / unparseable entries are skipped.
  /// </summary>
  public static async Task<IReadOnlyList<DevelopSnapshot>> LoadHistoryAsync(FileInfo file, int copyIndex = 0, CancellationToken cancellationToken = default) {
    var description = await LoadXmpDescriptionAsync(file, copyIndex, cancellationToken);
    if (description is null)
      return Array.Empty<DevelopSnapshot>();
    return ReadHistory(description);
  }

  /// <summary>Lists copy sidecars next to <paramref name="file"/>; copy 0 is implicit (embedded).</summary>
  public static IReadOnlyList<int> EnumerateVirtualCopies(FileInfo file)
    => VirtualCopyDiscovery.EnumerateIndices(file);

  /// <summary>
  /// Replace the develop-history stack persisted with <paramref name="file"/>'s
  /// current settings (does NOT touch the live <c>pm:developSettings</c> /
  /// <c>crs:</c> values). Used by the EditHistoryWindow's Delete action.
  /// </summary>
  public static async Task<bool> RewriteHistoryAsync(
      FileInfo file, int copyIndex, IReadOnlyList<DevelopSnapshot> history,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(history);
    if (file is null || !file.Exists || copyIndex < 0)
      return false;

    if (copyIndex == 0) {
      if (!SupportsContainer(file))
        return false;
      byte[] bytes;
      try {
        bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
      } catch {
        return false;
      }
      var existingXmp = JpegSegmentSurgery.TryReadXmpSegment(bytes);
      XDocument doc;
      if (existingXmp is null) {
        doc = MakeEmptyXmp();
      } else {
        try { doc = XDocument.Parse(Encoding.UTF8.GetString(existingXmp)); }
        catch { doc = MakeEmptyXmp(); }
      }
      var description = EnsureDescription(doc);
      WriteHistory(description, history);
      var newXmpBytes = SerializeUtf8(doc);
      try {
        var output = JpegSegmentSurgery.ReplaceXmpSegment(bytes, newXmpBytes);
        await AtomicMetadataWrite.WriteAsync(file, output, cancellationToken);
        return true;
      } catch {
        return false;
      }
    }

    var sidecar = VirtualCopyDiscovery.SidecarFor(file, copyIndex);
    XDocument sidecarDoc;
    if (sidecar.Exists) {
      try {
        var text = await File.ReadAllTextAsync(sidecar.FullName, cancellationToken);
        sidecarDoc = XDocument.Parse(text);
      } catch {
        sidecarDoc = MakeEmptyXmp();
      }
    } else {
      sidecarDoc = MakeEmptyXmp();
    }
    var sidecarDescription = EnsureDescription(sidecarDoc);
    WriteHistory(sidecarDescription, history);
    try {
      sidecar.Directory?.Create();
      await File.WriteAllBytesAsync(sidecar.FullName, SerializeUtf8(sidecarDoc), cancellationToken);
      return true;
    } catch {
      return false;
    }
  }

  /// <summary>
  /// Writes <paramref name="settings"/> into <paramref name="file"/>'s XMP.
  /// Existing XMP is preserved (only the develop-related elements are
  /// added/replaced/removed). Identity settings strip every develop element
  /// so a reset leaves no stale state behind. Returns false when the
  /// container isn't supported / the rewrite failed.
  /// </summary>
  public static Task<bool> SaveAsync(FileInfo file, DevelopSettings settings, CancellationToken cancellationToken = default)
    => SaveAsync(file, settings, copyIndex: 0, snapshotLabel: null, cancellationToken);

  /// <summary>
  /// Copy + history aware save. Writes <paramref name="settings"/> into the
  /// embedded XMP (when <paramref name="copyIndex"/> = 0) or the
  /// <c>basename.copyN.xmp</c> sidecar (when N &gt; 0). When
  /// <paramref name="snapshotLabel"/> is non-null, the prior settings (if
  /// any) are pushed onto the develop-history stack with that label before
  /// being overwritten — null means a silent autosave that leaves the stack
  /// alone. Identity-only saves with no snapshot still strip stale state.
  /// </summary>
  public static async Task<bool> SaveAsync(
      FileInfo file,
      DevelopSettings settings,
      int copyIndex,
      string? snapshotLabel,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(settings);
    if (file is null || !file.Exists)
      return false;
    if (copyIndex == 0 && !SupportsContainer(file))
      return false;
    if (copyIndex < 0)
      return false;

    if (copyIndex == 0)
      return await SaveEmbeddedAsync(file, settings, snapshotLabel, cancellationToken);
    return await SaveSidecarAsync(file, copyIndex, settings, snapshotLabel, cancellationToken);
  }

  private static async Task<bool> SaveEmbeddedAsync(FileInfo file, DevelopSettings settings, string? snapshotLabel, CancellationToken cancellationToken) {
    byte[] bytes;
    try {
      bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
    } catch {
      return false;
    }

    var existingXmp = JpegSegmentSurgery.TryReadXmpSegment(bytes);
    if (existingXmp is null && settings.IsIdentity && snapshotLabel is null)
      return true;

    XDocument doc;
    if (existingXmp != null) {
      try { doc = XDocument.Parse(Encoding.UTF8.GetString(existingXmp)); }
      catch { doc = MakeEmptyXmp(); }
    } else {
      doc = MakeEmptyXmp();
    }

    var description = EnsureDescription(doc);
    UpdateDescriptionPayload(description, settings, snapshotLabel);

    var newXmpBytes = SerializeUtf8(doc);
    byte[] output;
    try {
      output = JpegSegmentSurgery.ReplaceXmpSegment(bytes, newXmpBytes);
    } catch (InvalidDataException) {
      return false;
    } catch (InvalidOperationException) {
      return false;
    }

    try {
      await AtomicMetadataWrite.WriteAsync(file, output, cancellationToken);
      return true;
    } catch {
      return false;
    }
  }

  private static async Task<bool> SaveSidecarAsync(FileInfo file, int copyIndex, DevelopSettings settings, string? snapshotLabel, CancellationToken cancellationToken) {
    var sidecar = VirtualCopyDiscovery.SidecarFor(file, copyIndex);
    XDocument doc;
    if (sidecar.Exists) {
      try {
        var text = await File.ReadAllTextAsync(sidecar.FullName, cancellationToken);
        doc = XDocument.Parse(text);
      } catch {
        doc = MakeEmptyXmp();
      }
    } else {
      doc = MakeEmptyXmp();
    }

    var description = EnsureDescription(doc);
    UpdateDescriptionPayload(description, settings, snapshotLabel);

    try {
      var bytes = SerializeUtf8(doc);
      sidecar.Directory?.Create();
      await File.WriteAllBytesAsync(sidecar.FullName, bytes, cancellationToken);
      return true;
    } catch {
      return false;
    }
  }

  /// <summary>
  /// Apply the develop payload (crs: tags + pm:developSettings + history) to
  /// the given <c>rdf:Description</c>, pushing the previous settings onto
  /// the snapshot stack when <paramref name="snapshotLabel"/> is non-null.
  /// </summary>
  private static void UpdateDescriptionPayload(XElement description, DevelopSettings settings, string? snapshotLabel) {
    if (snapshotLabel is not null) {
      var priorSettings = ReadEffectiveSettings(description);
      if (priorSettings is not null) {
        var existingHistory = ReadHistory(description);
        // Cap at 20 for XMP persistence — the XMP APP1 segment has a ~64 KB
        // budget, so we can't store 50 full DevelopSettings blobs inline.
        // The in-memory API (DevelopHistory.DefaultMaxDepth = 50) supports
        // a higher cap for callers that persist elsewhere.
        var pushed = DevelopHistory.Push(existingHistory, priorSettings, snapshotLabel, maxDepth: 20);
        WriteHistory(description, pushed);
      }
    }

    description.Element(Pm + "developSettings")?.Remove();
    // Capture foreign mask li's BEFORE we strip the existing
    // crs:MaskGroupBasedCorrections so we can re-emit them after our
    // modeled li's. This is what keeps Adobe AI / Brush masks alive
    // through PhotoManager save cycles.
    var foreignMaskLis = CaptureForeignLocalAdjustmentLis(description);
    RemoveCrsElements(description);

    if (!settings.IsIdentity || foreignMaskLis.Count > 0) {
      EnsureNamespace(description, "crs", Crs);
      AddCrsElements(description, settings);
      EmitLocalAdjustments(description, settings.LocalAdjustments, foreignMaskLis);

      var extras = DevelopExtras.From(settings);
      if (!extras.IsEmpty) {
        EnsureNamespace(description, "pm", Pm);
        description.Add(new XElement(Pm + "developSettings", JsonSerializer.Serialize(extras, JsonOptions)));
      }
    }
  }

  private static XElement EnsureDescription(XDocument doc) {
    var description = doc.Descendants(Rdf + "Description").FirstOrDefault();
    if (description is not null)
      return description;
    description = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", string.Empty));
    var rdf = doc.Descendants(Rdf + "RDF").FirstOrDefault();
    if (rdf is null) {
      // No RDF root at all — replace with a fresh skeleton.
      doc.RemoveNodes();
      var fresh = MakeEmptyXmp();
      foreach (var node in fresh.Nodes())
        doc.Add(node);
      return doc.Descendants(Rdf + "Description").First();
    }
    rdf.Add(description);
    return description;
  }

  /// <summary>
  /// Re-derive the current effective <see cref="DevelopSettings"/> from the
  /// XMP description (crs: + pm:developSettings + locals). Used to capture
  /// the prior state before a snapshot-pushing save.
  /// </summary>
  private static DevelopSettings? ReadEffectiveSettings(XElement description) {
    var fromCrs = ReadCrsElements(description);
    var locals = ReadLocalAdjustments(description);
    fromCrs = fromCrs with { LocalAdjustments = locals };
    var extras = ReadExtras(description);
    var combined = ApplyExtras(fromCrs, extras);
    return combined.IsIdentity ? null : combined;
  }

  /// <summary>
  /// Resolves the <c>rdf:Description</c> for the given file + copy. Copy 0
  /// reads from the JPEG XMP packet; copy N (&gt; 0) reads the
  /// <c>basename.copyN.xmp</c> sidecar. Returns null when nothing is found.
  /// </summary>
  private static async Task<XElement?> LoadXmpDescriptionAsync(FileInfo file, int copyIndex, CancellationToken cancellationToken) {
    if (file is null || !file.Exists || copyIndex < 0)
      return null;
    string xmlText;
    if (copyIndex == 0) {
      if (!SupportsContainer(file))
        return null;
      byte[] bytes;
      try {
        bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
      } catch {
        return null;
      }
      var xmpBytes = JpegSegmentSurgery.TryReadXmpSegment(bytes);
      if (xmpBytes is null)
        return null;
      xmlText = Encoding.UTF8.GetString(xmpBytes);
    } else {
      var sidecar = VirtualCopyDiscovery.SidecarFor(file, copyIndex);
      if (!sidecar.Exists)
        return null;
      try {
        xmlText = await File.ReadAllTextAsync(sidecar.FullName, cancellationToken);
      } catch {
        return null;
      }
    }

    XDocument doc;
    try { doc = XDocument.Parse(xmlText); } catch { return null; }
    return doc.Descendants(Rdf + "Description").FirstOrDefault();
  }

  /// <summary>Outcome of a thumbnail-bake attempt.</summary>
  public sealed record BakeThumbnailResult(
    bool Success,
    int Width = 0,
    int Height = 0,
    int Quality = 0,
    int ThumbnailByteCount = 0,
    int ExifPayloadByteCount = 0,
    double PsnrDb = 0,
    int? BytesOverBudget = null,
    bool DidAutoFit = false,
    string? Error = null
  );

  /// <summary>
  /// Encode + embed an IFD1 thumbnail under the JPEG APP1 budget. If the
  /// requested (long-edge, quality) wouldn't fit, runs <see cref="ThumbnailFitter"/>
  /// to find the highest-PSNR combination that does, and embeds that
  /// instead. Pixel data of the main image is never re-encoded.
  /// Aspect ratio is taken from <paramref name="developedPreview"/> so
  /// the caller only sets the long edge.
  /// </summary>
  public static async Task<BakeThumbnailResult> BakeThumbnailAsync(
      FileInfo file,
      Image<Rgba32> developedPreview,
      int requestedLongEdge,
      int requestedQuality,
      CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(developedPreview);
    if (!SupportsContainer(file) || !file.Exists)
      return new BakeThumbnailResult(false, Error: "Unsupported container");

    byte[] hostBytes;
    try {
      hostBytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
    } catch (Exception ex) {
      return new BakeThumbnailResult(false, Error: ex.Message);
    }

    // Encode at the user's requested settings first — fast path when it
    // fits, and gives us a baseline byte count for the failure message.
    var aspect = (double)developedPreview.Width / developedPreview.Height;
    var (initialW, initialH) = developedPreview.Width >= developedPreview.Height
      ? (requestedLongEdge, Math.Max(1, (int)Math.Round(requestedLongEdge / aspect)))
      : (Math.Max(1, (int)Math.Round(requestedLongEdge * aspect)), requestedLongEdge);

    using var initialResized = developedPreview.Clone(c => c.Resize(initialW, initialH));
    using var initialMs = new MemoryStream();
    initialResized.SaveAsJpeg(initialMs, new JpegEncoder { Quality = requestedQuality });
    var initialThumbBytes = initialMs.ToArray();

    var initialPayload = JpegMetadataEditor.BuildEmbeddedThumbnailPayload(hostBytes, initialThumbBytes, initialW, initialH);
    if (initialPayload.Length <= JpegSegmentSurgery.MaxApp1PayloadBytes) {
      var psnr = ThumbnailFitter.ComputePsnr(initialResized, initialThumbBytes);
      return await Embed(file, hostBytes, initialThumbBytes, initialW, initialH,
        requestedQuality, initialPayload.Length, psnr, didAutoFit: false, cancellationToken);
    }

    // Over budget — figure out the thumbnail-bytes budget from the
    // overshoot, then ask the fitter for the best PSNR-fitting combo.
    var overshoot = initialPayload.Length - JpegSegmentSurgery.MaxApp1PayloadBytes;
    var thumbBudget = initialThumbBytes.Length - overshoot;
    if (thumbBudget < 256)  // pathological — nothing realistic will fit
      return new BakeThumbnailResult(false, BytesOverBudget: overshoot,
        Error: $"EXIF segment would exceed cap by {overshoot} bytes; even a tiny thumbnail can't fit.");

    var fit = ThumbnailFitter.FindBestFit(developedPreview, requestedLongEdge, requestedQuality, thumbBudget);
    if (fit is null)
      return new BakeThumbnailResult(false, BytesOverBudget: overshoot,
        Error: $"Couldn't squeeze a thumbnail under {thumbBudget} bytes — try a smaller request.");

    var fitPayload = JpegMetadataEditor.BuildEmbeddedThumbnailPayload(hostBytes, fit.JpegBytes, fit.Width, fit.Height);
    if (fitPayload.Length > JpegSegmentSurgery.MaxApp1PayloadBytes)
      return new BakeThumbnailResult(false, BytesOverBudget: fitPayload.Length - JpegSegmentSurgery.MaxApp1PayloadBytes,
        Error: "Auto-fit candidate still over budget after re-measure.");

    return await Embed(file, hostBytes, fit.JpegBytes, fit.Width, fit.Height,
      fit.Quality, fitPayload.Length, fit.PsnrDb, didAutoFit: true, cancellationToken);
  }

  private static async Task<BakeThumbnailResult> Embed(
      FileInfo file, byte[] hostBytes, byte[] thumbBytes, int width, int height,
      int quality, int payloadBytes, double psnr, bool didAutoFit, CancellationToken cancellationToken) {
    byte[] output;
    try {
      output = JpegMetadataEditor.SetEmbeddedThumbnail(hostBytes, thumbBytes, width, height);
    } catch (Exception ex) {
      return new BakeThumbnailResult(false, Error: ex.Message);
    }

    try {
      await AtomicMetadataWrite.WriteAsync(file, output, cancellationToken);
    } catch (Exception ex) {
      return new BakeThumbnailResult(false, Error: ex.Message);
    }

    return new BakeThumbnailResult(true, width, height, quality,
      thumbBytes.Length, payloadBytes, psnr, DidAutoFit: didAutoFit);
  }

  // ---------- crs: emit ----------

  private static readonly string[] CrsTagsWeOwn =
    new[] {
      "ProcessVersion", "Exposure2012", "Contrast2012", "Highlights2012",
      "Shadows2012", "Whites2012", "Blacks2012", "Texture", "Clarity2012",
      "Dehaze", "Vibrance", "Saturation",
      "Sharpness", "SharpenRadius", "SharpenDetail", "SharpenEdgeMasking",
      "Temperature", "Tint",
      "ToneCurveName2012", "ToneCurvePV2012",
      "ToneCurvePV2012Red", "ToneCurvePV2012Green", "ToneCurvePV2012Blue",
      "CropTop", "CropLeft", "CropBottom", "CropRight", "CropAngle", "HasCrop",
      // Color Grading
      "ColorGradeShadowHue", "ColorGradeShadowSat", "ColorGradeShadowLuminance",
      "ColorGradeMidtoneHue", "ColorGradeMidtoneSat", "ColorGradeMidtoneLuminance",
      "ColorGradeHighlightHue", "ColorGradeHighlightSat", "ColorGradeHighlightLuminance",
      "ColorGradeGlobalHue", "ColorGradeGlobalSat", "ColorGradeGlobalLuminance",
      // Vignette + Grain
      "PostCropVignetteAmount", "PostCropVignetteMidpoint", "PostCropVignetteFeather",
      "PostCropVignetteRoundness", "PostCropVignetteHighlightContrast",
      "GrainAmount", "GrainSize", "GrainFrequency",
      // Noise reduction
      "LuminanceSmoothing", "LuminanceNoiseReductionDetail", "LuminanceNoiseReductionContrast",
      "ColorNoiseReduction", "ColorNoiseReductionDetail", "ColorNoiseReductionSmoothness",
      // B&W
      "ConvertToGrayscale",
      "GrayMixerRed", "GrayMixerOrange", "GrayMixerYellow", "GrayMixerGreen",
      "GrayMixerAqua", "GrayMixerBlue", "GrayMixerPurple", "GrayMixerMagenta",
      // Split Toning
      "SplitToningShadowHue", "SplitToningShadowSaturation",
      "SplitToningHighlightHue", "SplitToningHighlightSaturation",
      "SplitToningBalance",
      // Parametric tone curve
      "ParametricShadows", "ParametricDarks", "ParametricLights", "ParametricHighlights",
      "ParametricShadowSplit", "ParametricMidtoneSplit", "ParametricHighlightSplit",
      // Lens corrections
      "LensManualDistortionAmount", "ChromaticAberrationR", "ChromaticAberrationB",
      // Defringe
      "DefringePurpleAmount", "DefringeGreenAmount",
      // Camera calibration
      "RedHue", "RedSaturation", "GreenHue", "GreenSaturation", "BlueHue", "BlueSaturation",
      // Perspective / Upright
      "PerspectiveVertical", "PerspectiveHorizontal", "PerspectiveRotate",
      "PerspectiveScale", "PerspectiveAspect", "PerspectiveX", "PerspectiveY",
      // Local adjustments — owned because we re-emit (with foreign li's
      // captured first so brush / AI masks survive). Stripping the wrapper
      // and rebuilding from settings + foreign keeps everything in sync.
      "MaskGroupBasedCorrections",
      // Color Enhancement (post-2023 Adobe slider).
      "ColorEnhancement"
      // Note: crs:LookName is intentionally NOT in this list. It's preserved
      // as a foreign tag (so 3rd-party-tool values aren't clobbered) and
      // only emitted below when settings.LookName is non-null.
    }
    .Concat(HslBandNames.Select(n => "HueAdjustment" + n))
    .Concat(HslBandNames.Select(n => "SaturationAdjustment" + n))
    .Concat(HslBandNames.Select(n => "LuminanceAdjustment" + n))
    .ToArray();

  private static void RemoveCrsElements(XElement description) {
    foreach (var name in CrsTagsWeOwn)
      description.Element(Crs + name)?.Remove();
  }

  private static void AddCrsElements(XElement description, DevelopSettings settings) {
    var inv = CultureInfo.InvariantCulture;

    // Process version stamp tells Lightroom these numbers are 2012-style.
    description.Add(new XElement(Crs + "ProcessVersion", "11.0"));

    if (Math.Abs(settings.ExposureStops) > 1e-6)
      description.Add(new XElement(Crs + "Exposure2012", settings.ExposureStops.ToString("+0.00;-0.00;0.00", inv)));
    AddCrsInt(description, "Contrast2012",   settings.ContrastPercent);
    AddCrsInt(description, "Highlights2012", settings.HighlightsPercent);
    AddCrsInt(description, "Shadows2012",    settings.ShadowsPercent);
    AddCrsInt(description, "Whites2012",     settings.WhitesPercent);
    AddCrsInt(description, "Blacks2012",     settings.BlacksPercent);
    AddCrsInt(description, "Texture",        settings.TexturePercent);
    AddCrsInt(description, "Clarity2012",    settings.ClarityPercent);
    AddCrsInt(description, "Dehaze",         settings.DehazePercent);
    AddCrsInt(description, "Vibrance",       settings.VibrancePercent);
    AddCrsInt(description, "Saturation",     settings.SaturationPercent);
    AddCrsInt(description, "ColorEnhancement", settings.ColorEnhancement);
    AddCrsInt(description, "Sharpness",      settings.SharpeningAmount);
    if (settings.SharpenRadius > 1e-6)
      description.Add(new XElement(Crs + "SharpenRadius",
        settings.SharpenRadius.ToString("0.0", inv)));
    AddCrsInt(description, "SharpenDetail",      settings.SharpenDetail);
    AddCrsInt(description, "SharpenEdgeMasking", settings.SharpenMasking);
    AddCrsInt(description, "Tint",           settings.TintShift);
    if (Math.Abs(settings.TemperatureShift) > 1e-6) {
      var kelvin = ShiftToKelvin(settings.TemperatureShift);
      description.Add(new XElement(Crs + "Temperature", kelvin.ToString(inv)));
    }

    // Tone curve LUT — 16-step Seq of "input, output" pairs in 0..255.
    EmitToneCurveLut(description, "ToneCurvePV2012",      settings.ToneCurvePoints,  settings.ToneCurveInterpolation);
    EmitToneCurveLut(description, "ToneCurvePV2012Red",   settings.RedCurvePoints,   settings.ToneCurveInterpolation);
    EmitToneCurveLut(description, "ToneCurvePV2012Green", settings.GreenCurvePoints, settings.ToneCurveInterpolation);
    EmitToneCurveLut(description, "ToneCurvePV2012Blue",  settings.BlueCurvePoints,  settings.ToneCurveInterpolation);
    if (settings.ToneCurvePoints is { Count: >= 2 } && !settings.IsCurveIdentity)
      description.Add(new XElement(Crs + "ToneCurveName2012", "Custom"));

    // HSL color mixer — 8 bands × Hue / Sat / Lum.
    EmitHslBandList(description, "HueAdjustment",        settings.HslHueShifts);
    EmitHslBandList(description, "SaturationAdjustment", settings.HslSaturationShifts);
    EmitHslBandList(description, "LuminanceAdjustment",  settings.HslLuminanceShifts);

    // Color Grading wheels — Adobe uses Hue 0..360, Sat 0..100, Luminance -100..+100.
    EmitGradeWheel(description, "Shadow",    settings.GradeShadowHue,    settings.GradeShadowSat,    settings.GradeShadowLum);
    EmitGradeWheel(description, "Midtone",   settings.GradeMidtoneHue,   settings.GradeMidtoneSat,   settings.GradeMidtoneLum);
    EmitGradeWheel(description, "Highlight", settings.GradeHighlightHue, settings.GradeHighlightSat, settings.GradeHighlightLum);
    EmitGradeWheel(description, "Global",    settings.GradeGlobalHue,    settings.GradeGlobalSat,    settings.GradeGlobalLum);

    // Post-crop vignette + grain.
    if (Math.Abs(settings.VignetteAmount) > 1e-6) {
      AddCrsInt(description, "PostCropVignetteAmount",            settings.VignetteAmount);
      AddCrsInt(description, "PostCropVignetteMidpoint",          settings.VignetteMidpoint);
      AddCrsInt(description, "PostCropVignetteFeather",           settings.VignetteFeather);
      AddCrsInt(description, "PostCropVignetteRoundness",         settings.VignetteRoundness);
      AddCrsInt(description, "PostCropVignetteHighlightContrast", settings.VignetteHighlightContrast);
    }
    if (Math.Abs(settings.GrainAmount) > 1e-6) {
      AddCrsInt(description, "GrainAmount",    settings.GrainAmount);
      AddCrsInt(description, "GrainSize",      settings.GrainSize);
      AddCrsInt(description, "GrainFrequency", settings.GrainFrequency);
    }

    // Noise reduction. Smoothness drives crs:LuminanceSmoothing; the
    // detail / contrast / chroma counterparts emit when set.
    AddCrsInt(description, "LuminanceSmoothing",              settings.SmoothnessPercent);
    AddCrsInt(description, "LuminanceNoiseReductionDetail",   settings.LuminanceNrDetail);
    AddCrsInt(description, "LuminanceNoiseReductionContrast", settings.LuminanceNrContrast);
    AddCrsInt(description, "ColorNoiseReduction",             settings.ColorNoiseReduction);
    AddCrsInt(description, "ColorNoiseReductionDetail",       settings.ColorNrDetail);
    AddCrsInt(description, "ColorNoiseReductionSmoothness",   settings.ColorNrSmoothness);

    // Black & White conversion. ConvertToGrayscale must be present even when
    // false in some Lightroom catalogues, but only emit it when actually true
    // — the foreign-element preserver round-trips the original "False" if any.
    if (settings.ConvertToGrayscale) {
      description.Add(new XElement(Crs + "ConvertToGrayscale", "True"));
      AddCrsInt(description, "GrayMixerRed",     settings.GrayMixerRed);
      AddCrsInt(description, "GrayMixerOrange",  settings.GrayMixerOrange);
      AddCrsInt(description, "GrayMixerYellow",  settings.GrayMixerYellow);
      AddCrsInt(description, "GrayMixerGreen",   settings.GrayMixerGreen);
      AddCrsInt(description, "GrayMixerAqua",    settings.GrayMixerAqua);
      AddCrsInt(description, "GrayMixerBlue",    settings.GrayMixerBlue);
      AddCrsInt(description, "GrayMixerPurple",  settings.GrayMixerPurple);
      AddCrsInt(description, "GrayMixerMagenta", settings.GrayMixerMagenta);
    }

    // Split Toning (legacy; superseded by Color Grading but still in catalogues).
    if (Math.Abs(settings.SplitToningShadowSaturation) > 1e-6
     || Math.Abs(settings.SplitToningHighlightSaturation) > 1e-6) {
      AddCrsInt(description, "SplitToningShadowHue",         settings.SplitToningShadowHue);
      AddCrsInt(description, "SplitToningShadowSaturation",  settings.SplitToningShadowSaturation);
      AddCrsInt(description, "SplitToningHighlightHue",      settings.SplitToningHighlightHue);
      AddCrsInt(description, "SplitToningHighlightSaturation", settings.SplitToningHighlightSaturation);
      AddCrsInt(description, "SplitToningBalance",           settings.SplitToningBalance);
    }

    // Parametric tone curve. Adobe defaults the splits to 25/50/75; only
    // emit them when the user has at least one non-zero lift, so a
    // pristine catalogue doesn't gain noise.
    if (Math.Abs(settings.ParametricShadows)    > 1e-6
     || Math.Abs(settings.ParametricDarks)      > 1e-6
     || Math.Abs(settings.ParametricLights)     > 1e-6
     || Math.Abs(settings.ParametricHighlights) > 1e-6) {
      AddCrsInt(description, "ParametricShadows",        settings.ParametricShadows);
      AddCrsInt(description, "ParametricDarks",          settings.ParametricDarks);
      AddCrsInt(description, "ParametricLights",         settings.ParametricLights);
      AddCrsInt(description, "ParametricHighlights",     settings.ParametricHighlights);
      AddCrsInt(description, "ParametricShadowSplit",    settings.ParametricShadowSplit);
      AddCrsInt(description, "ParametricMidtoneSplit",   settings.ParametricMidtoneSplit);
      AddCrsInt(description, "ParametricHighlightSplit", settings.ParametricHighlightSplit);
    }

    // Lens corrections — manual distortion + per-channel CA.
    AddCrsInt(description, "LensManualDistortionAmount", settings.LensManualDistortion);
    AddCrsInt(description, "ChromaticAberrationR",       settings.ChromaticAberrationR);
    AddCrsInt(description, "ChromaticAberrationB",       settings.ChromaticAberrationB);

    // Defringe — Adobe range 0..20, so emit small ints.
    AddCrsInt(description, "DefringePurpleAmount", settings.DefringePurpleAmount);
    AddCrsInt(description, "DefringeGreenAmount",  settings.DefringeGreenAmount);

    // Camera calibration — small per-primary nudges.
    AddCrsInt(description, "RedHue",          settings.CalibrationRedHue);
    AddCrsInt(description, "RedSaturation",   settings.CalibrationRedSaturation);
    AddCrsInt(description, "GreenHue",        settings.CalibrationGreenHue);
    AddCrsInt(description, "GreenSaturation", settings.CalibrationGreenSaturation);
    AddCrsInt(description, "BlueHue",         settings.CalibrationBlueHue);
    AddCrsInt(description, "BlueSaturation",  settings.CalibrationBlueSaturation);

    // Perspective / Upright. Adobe defaults Scale to 100, so emit it
    // unconditionally when any other perspective slider is non-default —
    // otherwise old-Lightroom round-trips set Scale to 0 which breaks layout.
    var hasPerspective = Math.Abs(settings.PerspectiveVertical)   > 1e-6
                      || Math.Abs(settings.PerspectiveHorizontal) > 1e-6
                      || Math.Abs(settings.PerspectiveRotate)     > 1e-6
                      || Math.Abs(settings.PerspectiveScale - 100) > 1e-6
                      || Math.Abs(settings.PerspectiveAspect)     > 1e-6
                      || Math.Abs(settings.PerspectiveX)          > 1e-6
                      || Math.Abs(settings.PerspectiveY)          > 1e-6;
    if (hasPerspective) {
      AddCrsInt(description, "PerspectiveVertical",   settings.PerspectiveVertical);
      AddCrsInt(description, "PerspectiveHorizontal", settings.PerspectiveHorizontal);
      AddCrsInt(description, "PerspectiveRotate",     settings.PerspectiveRotate);
      // Scale is required; emit at full precision when not 100.
      description.Add(new XElement(Crs + "PerspectiveScale",
        ((int)Math.Round(settings.PerspectiveScale)).ToString(inv)));
      AddCrsInt(description, "PerspectiveAspect", settings.PerspectiveAspect);
      AddCrsInt(description, "PerspectiveX",      settings.PerspectiveX);
      AddCrsInt(description, "PerspectiveY",      settings.PerspectiveY);
    }

    // Crop rectangle + free rotation. Adobe uses HasCrop="True" + 0..1
    // edge offsets in normalised image coords, plus an explicit angle.
    var hasCrop = Math.Abs(settings.CropAngleDegrees) > 1e-6
      || settings.CropLeft > 1e-6 || settings.CropTop > 1e-6
      || settings.CropRight < 1 - 1e-6 || settings.CropBottom < 1 - 1e-6;
    if (hasCrop) {
      description.Add(new XElement(Crs + "HasCrop", "True"));
      description.Add(new XElement(Crs + "CropTop",    settings.CropTop.ToString("0.######", inv)));
      description.Add(new XElement(Crs + "CropLeft",   settings.CropLeft.ToString("0.######", inv)));
      description.Add(new XElement(Crs + "CropBottom", settings.CropBottom.ToString("0.######", inv)));
      description.Add(new XElement(Crs + "CropRight",  settings.CropRight.ToString("0.######", inv)));
      if (Math.Abs(settings.CropAngleDegrees) > 1e-6)
        description.Add(new XElement(Crs + "CropAngle", settings.CropAngleDegrees.ToString("0.0##", inv)));
    }

    if (!string.IsNullOrWhiteSpace(settings.LookName)) {
      description.Element(Crs + "LookName")?.Remove();
      description.Add(new XElement(Crs + "LookName", settings.LookName));
    }
  }

  private static void EmitHslBandList(XElement description, string prefix, IReadOnlyList<double>? values) {
    if (values is null)
      return;
    for (var i = 0; i < HslBandNames.Length && i < values.Count; i++)
      AddCrsInt(description, prefix + HslBandNames[i], values[i]);
  }

  private static void EmitGradeWheel(XElement description, string section, double hue, double sat, double lum) {
    if (Math.Abs(sat) < 1e-6 && Math.Abs(lum) < 1e-6)
      return;  // a hue with no sat / lum has no visible effect
    AddCrsInt(description, "ColorGrade" + section + "Hue",        hue);
    AddCrsInt(description, "ColorGrade" + section + "Sat",        sat);
    AddCrsInt(description, "ColorGrade" + section + "Luminance",  lum);
  }

  // ---------- Local adjustments (crs:MaskGroupBasedCorrections) ----------

  /// <summary>
  /// Walk the existing <c>crs:MaskGroupBasedCorrections</c> rdf:Seq and
  /// pull out every &lt;rdf:li&gt; whose inner mask we DON'T model. These
  /// are returned as-is so a follow-up emit can re-add them, keeping
  /// brush masks / AI subject masks / range masks alive across saves.
  /// </summary>
  private static List<XElement> CaptureForeignLocalAdjustmentLis(XElement description) {
    var foreign = new List<XElement>();
    var seq = description.Element(Crs + "MaskGroupBasedCorrections")?.Element(Rdf + "Seq");
    if (seq is null)
      return foreign;
    foreach (var li in seq.Elements(Rdf + "li").ToArray()) {
      if (!IsModeledLocalAdjustment(li))
        foreign.Add(new XElement(li));
    }
    return foreign;
  }

  /// <summary>
  /// True when this rdf:li corresponds to a local-adjustment we model:
  /// at least one mask li with a type we recognise (Gradient /
  /// CircularGradient / Brush). Compositions of recognised masks count;
  /// AI / range / depth masks alone do not.
  /// </summary>
  private static bool IsModeledLocalAdjustment(XElement li) {
    // The correction's inner masks live in crs:CorrectionMasks/rdf:Seq/rdf:li.
    var masksContainer = FindCrsAttributeOrChild(li, "CorrectionMasks");
    if (masksContainer is null)
      return false;
    var maskList = masksContainer.Element(Rdf + "Seq")?.Elements(Rdf + "li").ToList();
    if (maskList is null || maskList.Count == 0)
      return false;
    foreach (var maskLi in maskList) {
      var what = ReadCrsAttribute(maskLi, "What");
      if (what is "Mask/Gradient" or "Mask/CircularGradient" or "Mask/Brush")
        return true;
    }
    return false;
  }

  private static XElement? FindCrsAttributeOrChild(XElement parent, string localName) {
    // RDF lets attributes be either XML attributes on the description or
    // separate child elements — we need to handle both forms when reading.
    var child = parent.Element(Crs + localName)
              ?? parent.Element(Rdf + "Description")?.Element(Crs + localName);
    return child;
  }

  private static string? ReadCrsAttribute(XElement el, string localName) {
    var attr = el.Attribute(Crs + localName)
            ?? el.Element(Rdf + "Description")?.Attribute(Crs + localName);
    if (attr != null)
      return attr.Value;
    return el.Element(Crs + localName)?.Value
        ?? el.Element(Rdf + "Description")?.Element(Crs + localName)?.Value;
  }

  private static void EmitLocalAdjustments(XElement description, IReadOnlyList<LocalAdjustment>? adjustments, IReadOnlyList<XElement> foreignLis) {
    if ((adjustments is null || adjustments.Count == 0) && foreignLis.Count == 0)
      return;

    var seq = new XElement(Rdf + "Seq");
    if (adjustments is not null)
      foreach (var adj in adjustments)
        if (!adj.IsZero)
          seq.Add(BuildLocalAdjustmentLi(adj));
    foreach (var fli in foreignLis)
      seq.Add(new XElement(fli));

    if (!seq.HasElements)
      return;
    description.Add(new XElement(Crs + "MaskGroupBasedCorrections", seq));
  }

  /// <summary>Build the rdf:li for one PhotoManager local adjustment.</summary>
  private static XElement BuildLocalAdjustmentLi(LocalAdjustment adj) {
    var inv = CultureInfo.InvariantCulture;
    var corrDesc = new XElement(Rdf + "Description",
      new XAttribute(Crs + "What", "Correction"),
      new XAttribute(Crs + "CorrectionAmount", adj.Amount.ToString("0.######", inv)),
      new XAttribute(Crs + "CorrectionActive", "true"),
      new XAttribute(Crs + "CorrectionName", string.IsNullOrWhiteSpace(adj.Name) ? "Local" : adj.Name)
    );
    AddOptionalCrsAttribute(corrDesc, "LocalExposure2012",   adj.Exposure,    "0.######");
    AddOptionalCrsAttribute(corrDesc, "LocalContrast2012",   adj.Contrast,    "0.######");
    AddOptionalCrsAttribute(corrDesc, "LocalHighlights2012", adj.Highlights,  "0.######");
    AddOptionalCrsAttribute(corrDesc, "LocalShadows2012",    adj.Shadows,     "0.######");
    AddOptionalCrsAttribute(corrDesc, "LocalSaturation",     adj.Saturation,  "0.######");
    AddOptionalCrsAttribute(corrDesc, "LocalTemperature",    adj.Temperature, "0.######");
    AddOptionalCrsAttribute(corrDesc, "LocalTint",           adj.Tint,        "0.######");
    AddOptionalCrsAttribute(corrDesc, "LocalClarity2012",    adj.Clarity,     "0.######");
    AddOptionalCrsAttribute(corrDesc, "LocalLuminanceNoise", adj.Luminance,   "0.######");
    AddOptionalCrsAttribute(corrDesc, "LocalToningHue",      adj.ToningHue,   "0.##");
    AddOptionalCrsAttribute(corrDesc, "LocalToningSaturation", adj.ToningSaturation, "0.######");
    AddOptionalCrsAttribute(corrDesc, "LocalDefringe",       adj.Defringe,    "0.######");

    var masksList = new XElement(Rdf + "Seq");
    masksList.Add(new XElement(Rdf + "li", BuildMaskDescription(adj.Mask, isPrimary: true)));
    if (adj.SubMasks is { } subs)
      foreach (var sub in subs)
        masksList.Add(new XElement(Rdf + "li", BuildMaskDescription(sub, isPrimary: false)));
    corrDesc.Add(new XElement(Crs + "CorrectionMasks", masksList));

    return new XElement(Rdf + "li", corrDesc);
  }

  /// <summary>
  /// Build the rdf:Description for one mask li. Adobe's MaskValue carries
  /// the +1 / -1 sign for Add / Subtract; our Intersect op gets a custom
  /// pm:CombineOp attribute that Lightroom will ignore (degrading to Add)
  /// while PhotoManager round-trips it faithfully.
  /// </summary>
  private static XElement BuildMaskDescription(LocalMask mask, bool isPrimary) {
    var inv = CultureInfo.InvariantCulture;
    var maskValue = mask.Combine == MaskCombineOp.Subtract ? "-1" : "1";

    XElement maskDesc;
    switch (mask.Type) {
      case LocalMaskType.Linear:
        maskDesc = new XElement(Rdf + "Description",
          new XAttribute(Crs + "What",     "Mask/Gradient"),
          new XAttribute(Crs + "MaskValue", maskValue),
          new XAttribute(Crs + "ZeroX",    mask.X0.ToString("0.######", inv)),
          new XAttribute(Crs + "ZeroY",    mask.Y0.ToString("0.######", inv)),
          new XAttribute(Crs + "FullX",    mask.X1.ToString("0.######", inv)),
          new XAttribute(Crs + "FullY",    mask.Y1.ToString("0.######", inv)));
        break;
      case LocalMaskType.Radial:
        maskDesc = new XElement(Rdf + "Description",
          new XAttribute(Crs + "What",      "Mask/CircularGradient"),
          new XAttribute(Crs + "MaskValue", maskValue),
          new XAttribute(Crs + "Top",       (mask.CenterY - mask.RadiusY).ToString("0.######", inv)),
          new XAttribute(Crs + "Left",      (mask.CenterX - mask.RadiusX).ToString("0.######", inv)),
          new XAttribute(Crs + "Bottom",    (mask.CenterY + mask.RadiusY).ToString("0.######", inv)),
          new XAttribute(Crs + "Right",     (mask.CenterX + mask.RadiusX).ToString("0.######", inv)),
          new XAttribute(Crs + "Angle",     mask.Angle.ToString("0.##", inv)),
          new XAttribute(Crs + "Feather",   ((int)Math.Round(mask.Feather)).ToString(inv)),
          new XAttribute(Crs + "Flipped",   mask.Invert ? "true" : "false"));
        break;
      case LocalMaskType.Brush:
        maskDesc = new XElement(Rdf + "Description",
          new XAttribute(Crs + "What",      "Mask/Brush"),
          new XAttribute(Crs + "MaskValue", maskValue));
        if (mask.BrushDabs is { Count: > 0 } dabs) {
          var dabsSeq = new XElement(Rdf + "Seq");
          foreach (var d in dabs) {
            var prefix = d.Flow >= 0 ? "u" : "e";
            dabsSeq.Add(new XElement(Rdf + "li",
              $"{prefix} {d.X.ToString("0.######", inv)},{d.Y.ToString("0.######", inv)},{d.Radius.ToString("0.######", inv)},{Math.Abs(d.Flow).ToString("0.##", inv)}"));
          }
          maskDesc.Add(new XElement(Crs + "Dabs", dabsSeq));
        }
        break;
      case LocalMaskType.Inpaint:
        // Content-aware fill — stored as a pm: extension. The mask is a
        // brush-dab cloud identical to Brush, but the "What" discriminator
        // tells the renderer to feed it to LaMa instead of applying local
        // develop sliders.
        maskDesc = new XElement(Rdf + "Description",
          new XAttribute(Crs + "What",      "Mask/Brush"),
          new XAttribute(Crs + "MaskValue", maskValue),
          new XAttribute(Pm + "Inpaint",    "true"));
        if (mask.BrushDabs is { Count: > 0 } inpaintDabs) {
          var inpaintDabsSeq = new XElement(Rdf + "Seq");
          foreach (var d in inpaintDabs) {
            var prefix = d.Flow >= 0 ? "u" : "e";
            inpaintDabsSeq.Add(new XElement(Rdf + "li",
              $"{prefix} {d.X.ToString("0.######", inv)},{d.Y.ToString("0.######", inv)},{d.Radius.ToString("0.######", inv)},{Math.Abs(d.Flow).ToString("0.##", inv)}"));
          }
          maskDesc.Add(new XElement(Crs + "Dabs", inpaintDabsSeq));
        }
        break;
      default:
        throw new InvalidOperationException("Unknown LocalMaskType " + mask.Type);
    }

    // Range-mask attributes — luminance + hue.
    if (mask.LuminanceRangeMin > 1e-6 || mask.LuminanceRangeMax < 1 - 1e-6) {
      maskDesc.Add(new XAttribute(Crs + "LumRangeMin",     mask.LuminanceRangeMin.ToString("0.######", inv)));
      maskDesc.Add(new XAttribute(Crs + "LumRangeMax",     mask.LuminanceRangeMax.ToString("0.######", inv)));
      maskDesc.Add(new XAttribute(Crs + "LumRangeFeather", mask.LuminanceRangeFeather.ToString("0.######", inv)));
    }
    if (mask.HueRangeMin > 1e-6 || mask.HueRangeMax < 1 - 1e-6) {
      maskDesc.Add(new XAttribute(Crs + "HueRangeMin",     mask.HueRangeMin.ToString("0.######", inv)));
      maskDesc.Add(new XAttribute(Crs + "HueRangeMax",     mask.HueRangeMax.ToString("0.######", inv)));
      maskDesc.Add(new XAttribute(Crs + "HueRangeFeather", mask.HueRangeFeather.ToString("0.######", inv)));
    }

    // Combine op — Adobe captures Add/Subtract via MaskValue's sign, but
    // Intersect needs an extension. Emit our marker for non-default sub-masks.
    if (!isPrimary && mask.Combine == MaskCombineOp.Intersect)
      maskDesc.Add(new XAttribute(Pm + "CombineOp", "Intersect"));

    return maskDesc;
  }

  private static void AddOptionalCrsAttribute(XElement target, string name, double value, string format) {
    if (Math.Abs(value) < 1e-6)
      return;
    target.Add(new XAttribute(Crs + name, value.ToString(format, CultureInfo.InvariantCulture)));
  }

  /// <summary>Parse our PhotoManager-modeled local adjustments out of the existing XMP.</summary>
  private static IReadOnlyList<LocalAdjustment>? ReadLocalAdjustments(XElement description) {
    var seq = description.Element(Crs + "MaskGroupBasedCorrections")?.Element(Rdf + "Seq");
    if (seq is null)
      return null;
    var result = new List<LocalAdjustment>();
    foreach (var li in seq.Elements(Rdf + "li")) {
      if (!IsModeledLocalAdjustment(li))
        continue;
      var corr = li.Element(Rdf + "Description") ?? li;
      var maskLis = corr.Element(Crs + "CorrectionMasks")?.Element(Rdf + "Seq")?.Elements(Rdf + "li").ToList();
      if (maskLis is null || maskLis.Count == 0)
        continue;

      // Walk every mask li; the first recognised one becomes the primary,
      // the rest become sub-masks. RangeMasks (which we don't model) are
      // skipped here — they survive as part of the foreign passthrough.
      LocalMask? primary = null;
      var subMasks = new List<LocalMask>();
      foreach (var maskLi in maskLis) {
        var maskDesc = maskLi.Element(Rdf + "Description") ?? maskLi;
        var parsed = ParseMaskDescription(maskDesc);
        if (parsed is null)
          continue;
        if (primary is null)
          primary = parsed;
        else
          subMasks.Add(parsed);
      }
      if (primary is null)
        continue;

      result.Add(new LocalAdjustment(
        Mask: primary,
        Name:             ReadCrsAttribute(corr, "CorrectionName") ?? "",
        Amount:           ReadCrsDouble(corr, "CorrectionAmount", 1.0),
        Exposure:         ReadCrsDouble(corr, "LocalExposure2012", 0),
        Contrast:         ReadCrsDouble(corr, "LocalContrast2012", 0),
        Highlights:       ReadCrsDouble(corr, "LocalHighlights2012", 0),
        Shadows:          ReadCrsDouble(corr, "LocalShadows2012", 0),
        Saturation:       ReadCrsDouble(corr, "LocalSaturation", 0),
        Temperature:      ReadCrsDouble(corr, "LocalTemperature", 0),
        Tint:             ReadCrsDouble(corr, "LocalTint", 0),
        Clarity:          ReadCrsDouble(corr, "LocalClarity2012", 0),
        Luminance:        ReadCrsDouble(corr, "LocalLuminanceNoise", 0),
        ToningHue:        ReadCrsDouble(corr, "LocalToningHue", 0),
        ToningSaturation: ReadCrsDouble(corr, "LocalToningSaturation", 0),
        Defringe:         ReadCrsDouble(corr, "LocalDefringe", 0),
        SubMasks:         subMasks.Count > 0 ? subMasks : null));
    }
    return result.Count == 0 ? null : result;
  }

  /// <summary>
  /// Parse one mask description into a <see cref="LocalMask"/>, including
  /// range-filter attributes and the combine op. Returns null for mask
  /// types we don't model so the caller can pass through to the next li.
  /// </summary>
  private static LocalMask? ParseMaskDescription(XElement maskDesc) {
    var what = ReadCrsAttribute(maskDesc, "What");
    LocalMask mask;
    switch (what) {
      case "Mask/Gradient":
        mask = new LocalMask(
          Type: LocalMaskType.Linear,
          X0: ReadCrsDouble(maskDesc, "ZeroX", 0.5),
          Y0: ReadCrsDouble(maskDesc, "ZeroY", 0),
          X1: ReadCrsDouble(maskDesc, "FullX", 0.5),
          Y1: ReadCrsDouble(maskDesc, "FullY", 1));
        break;
      case "Mask/Brush":
        // Check for pm:Inpaint="true" — if present this is a content-aware
        // fill mask rather than a regular brush mask.
        var isInpaint = string.Equals(
          maskDesc.Attribute(Pm + "Inpaint")?.Value, "true",
          StringComparison.OrdinalIgnoreCase);
        mask = new LocalMask(
          Type: isInpaint ? LocalMaskType.Inpaint : LocalMaskType.Brush,
          BrushDabs: ParseBrushDabs(maskDesc.Element(Crs + "Dabs")));
        break;
      case "Mask/CircularGradient": {
        var top    = ReadCrsDouble(maskDesc, "Top",    0.25);
        var left   = ReadCrsDouble(maskDesc, "Left",   0.25);
        var bottom = ReadCrsDouble(maskDesc, "Bottom", 0.75);
        var right  = ReadCrsDouble(maskDesc, "Right",  0.75);
        mask = new LocalMask(
          Type: LocalMaskType.Radial,
          CenterX: (left + right) * 0.5,
          CenterY: (top + bottom) * 0.5,
          RadiusX: (right - left) * 0.5,
          RadiusY: (bottom - top) * 0.5,
          Angle:   ReadCrsDouble(maskDesc, "Angle",   0),
          Feather: ReadCrsDouble(maskDesc, "Feather", 50),
          Invert:  string.Equals(ReadCrsAttribute(maskDesc, "Flipped"), "true", StringComparison.OrdinalIgnoreCase));
        break;
      }
      default:
        return null;
    }

    // Range-filter attributes (PhotoManager extension).
    mask = mask with {
      LuminanceRangeMin     = ReadCrsDouble(maskDesc, "LumRangeMin",     0),
      LuminanceRangeMax     = ReadCrsDouble(maskDesc, "LumRangeMax",     1),
      LuminanceRangeFeather = ReadCrsDouble(maskDesc, "LumRangeFeather", 0.1),
      HueRangeMin           = ReadCrsDouble(maskDesc, "HueRangeMin",     0),
      HueRangeMax           = ReadCrsDouble(maskDesc, "HueRangeMax",     1),
      HueRangeFeather       = ReadCrsDouble(maskDesc, "HueRangeFeather", 0.05)
    };

    // Combine op — Adobe's MaskValue carries the sign for Add / Subtract;
    // Intersect comes from our pm:CombineOp extension. Older Adobe writers
    // use MaskValue but never Intersect, so missing value → Add.
    var maskValueText = ReadCrsAttribute(maskDesc, "MaskValue");
    var pmOp = maskDesc.Attribute(Pm + "CombineOp")?.Value;
    var op = MaskCombineOp.Add;
    if (string.Equals(pmOp, "Intersect", StringComparison.OrdinalIgnoreCase))
      op = MaskCombineOp.Intersect;
    else if (!string.IsNullOrEmpty(maskValueText)
          && double.TryParse(maskValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var mv)
          && mv < 0)
      op = MaskCombineOp.Subtract;
    mask = mask with { Combine = op };
    return mask;
  }

  private static double ReadCrsDouble(XElement el, string name, double @default) {
    var text = ReadCrsAttribute(el, name);
    if (string.IsNullOrWhiteSpace(text))
      return @default;
    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : @default;
  }

  /// <summary>
  /// Parse the <c>crs:Dabs/rdf:Seq</c> brush-stroke list. Adobe's format is
  /// "u x,y,r" per dab; PhotoManager additionally writes "e x,y,r,flow" for
  /// eraser dabs, and "u x,y,r,flow" with explicit flow. Missing flow → 1.0.
  /// </summary>
  private static IReadOnlyList<BrushDab> ParseBrushDabs(XElement? dabsContainer) {
    var seq = dabsContainer?.Element(Rdf + "Seq");
    if (seq is null)
      return Array.Empty<BrushDab>();
    var inv = CultureInfo.InvariantCulture;
    var result = new List<BrushDab>();
    foreach (var li in seq.Elements(Rdf + "li")) {
      var text = li.Value?.Trim();
      if (string.IsNullOrEmpty(text)) continue;
      var prefix = text[0];                          // 'u' = paint, 'e' = eraser
      var rest = text.AsSpan(1).TrimStart();
      var parts = rest.ToString().Split(',');
      if (parts.Length < 3) continue;
      if (!double.TryParse(parts[0], NumberStyles.Float, inv, out var x)) continue;
      if (!double.TryParse(parts[1], NumberStyles.Float, inv, out var y)) continue;
      if (!double.TryParse(parts[2], NumberStyles.Float, inv, out var r)) continue;
      var flow = 1.0;
      if (parts.Length >= 4 && double.TryParse(parts[3], NumberStyles.Float, inv, out var parsedFlow))
        flow = parsedFlow;
      if (prefix == 'e') flow = -flow;
      result.Add(new BrushDab(x, y, r, flow));
    }
    return result;
  }

  private static void EmitToneCurveLut(XElement description, string tag, IReadOnlyList<CurvePoint>? points, CurveInterpolation mode) {
    if (points is not { Count: >= 2 })
      return;
    var lut = ImageDeveloper.BuildCurveLut(points, mode);
    if (lut is null)
      return;  // identity curve
    var seq = new XElement(Rdf + "Seq");
    for (var i = 0; i <= 15; i++) {
      var input = i * 255 / 15;
      var output = lut[input];
      seq.Add(new XElement(Rdf + "li", $"{input}, {output}"));
    }
    description.Add(new XElement(Crs + tag, seq));
  }

  private static void AddCrsInt(XElement description, string tag, double value) {
    if (Math.Abs(value) < 1e-6)
      return;
    var rounded = (int)Math.Round(value);
    description.Add(new XElement(Crs + tag, rounded.ToString(CultureInfo.InvariantCulture)));
  }

  // ---------- crs: parse ----------

  private static DevelopSettings ReadCrsElements(XElement description) {
    var inv = CultureInfo.InvariantCulture;
    DevelopSettings s = new();

    if (TryReadDouble(description, Crs + "Exposure2012", out var exposure))
      s = s with { ExposureStops = exposure };
    if (TryReadDouble(description, Crs + "Contrast2012",   out var v)) s = s with { ContrastPercent   = v };
    if (TryReadDouble(description, Crs + "Highlights2012", out v))     s = s with { HighlightsPercent = v };
    if (TryReadDouble(description, Crs + "Shadows2012",    out v))     s = s with { ShadowsPercent    = v };
    if (TryReadDouble(description, Crs + "Whites2012",     out v))     s = s with { WhitesPercent     = v };
    if (TryReadDouble(description, Crs + "Blacks2012",     out v))     s = s with { BlacksPercent     = v };
    if (TryReadDouble(description, Crs + "Texture",        out v))     s = s with { TexturePercent    = v };
    if (TryReadDouble(description, Crs + "Clarity2012",    out v))     s = s with { ClarityPercent    = v };
    if (TryReadDouble(description, Crs + "Vibrance",       out v))     s = s with { VibrancePercent   = v };
    if (TryReadDouble(description, Crs + "Saturation",     out v))     s = s with { SaturationPercent = v };
    if (TryReadDouble(description, Crs + "ColorEnhancement", out v))   s = s with { ColorEnhancement  = v };
    if (TryReadDouble(description, Crs + "Sharpness",      out v))     s = s with { SharpeningAmount  = v };
    if (TryReadDouble(description, Crs + "Dehaze",         out v))     s = s with { DehazePercent     = v };
    if (TryReadDouble(description, Crs + "SharpenRadius",  out v))     s = s with { SharpenRadius     = v };
    if (TryReadDouble(description, Crs + "SharpenDetail",  out v))     s = s with { SharpenDetail     = v };
    if (TryReadDouble(description, Crs + "SharpenEdgeMasking", out v)) s = s with { SharpenMasking    = v };
    if (TryReadDouble(description, Crs + "Tint",           out v))     s = s with { TintShift         = v };
    if (TryReadDouble(description, Crs + "Temperature",    out var k)) s = s with { TemperatureShift  = KelvinToShift(k) };

    s = s with {
      HslHueShifts        = ReadHslBandList(description, "HueAdjustment"),
      HslSaturationShifts = ReadHslBandList(description, "SaturationAdjustment"),
      HslLuminanceShifts  = ReadHslBandList(description, "LuminanceAdjustment")
    };

    if (TryReadDouble(description, Crs + "CropTop",    out v)) s = s with { CropTop    = Math.Clamp(v, 0, 1) };
    if (TryReadDouble(description, Crs + "CropLeft",   out v)) s = s with { CropLeft   = Math.Clamp(v, 0, 1) };
    if (TryReadDouble(description, Crs + "CropBottom", out v)) s = s with { CropBottom = Math.Clamp(v, 0, 1) };
    if (TryReadDouble(description, Crs + "CropRight",  out v)) s = s with { CropRight  = Math.Clamp(v, 0, 1) };
    if (TryReadDouble(description, Crs + "CropAngle",  out v)) s = s with { CropAngleDegrees = v };

    // Color Grading
    if (TryReadDouble(description, Crs + "ColorGradeShadowHue",          out v)) s = s with { GradeShadowHue       = v };
    if (TryReadDouble(description, Crs + "ColorGradeShadowSat",          out v)) s = s with { GradeShadowSat       = v };
    if (TryReadDouble(description, Crs + "ColorGradeShadowLuminance",    out v)) s = s with { GradeShadowLum       = v };
    if (TryReadDouble(description, Crs + "ColorGradeMidtoneHue",         out v)) s = s with { GradeMidtoneHue      = v };
    if (TryReadDouble(description, Crs + "ColorGradeMidtoneSat",         out v)) s = s with { GradeMidtoneSat      = v };
    if (TryReadDouble(description, Crs + "ColorGradeMidtoneLuminance",   out v)) s = s with { GradeMidtoneLum      = v };
    if (TryReadDouble(description, Crs + "ColorGradeHighlightHue",       out v)) s = s with { GradeHighlightHue    = v };
    if (TryReadDouble(description, Crs + "ColorGradeHighlightSat",       out v)) s = s with { GradeHighlightSat    = v };
    if (TryReadDouble(description, Crs + "ColorGradeHighlightLuminance", out v)) s = s with { GradeHighlightLum    = v };
    if (TryReadDouble(description, Crs + "ColorGradeGlobalHue",          out v)) s = s with { GradeGlobalHue       = v };
    if (TryReadDouble(description, Crs + "ColorGradeGlobalSat",          out v)) s = s with { GradeGlobalSat       = v };
    if (TryReadDouble(description, Crs + "ColorGradeGlobalLuminance",    out v)) s = s with { GradeGlobalLum       = v };

    // Vignette + grain
    if (TryReadDouble(description, Crs + "PostCropVignetteAmount",            out v)) s = s with { VignetteAmount             = v };
    if (TryReadDouble(description, Crs + "PostCropVignetteMidpoint",          out v)) s = s with { VignetteMidpoint           = v };
    if (TryReadDouble(description, Crs + "PostCropVignetteFeather",           out v)) s = s with { VignetteFeather            = v };
    if (TryReadDouble(description, Crs + "PostCropVignetteRoundness",         out v)) s = s with { VignetteRoundness          = v };
    if (TryReadDouble(description, Crs + "PostCropVignetteHighlightContrast", out v)) s = s with { VignetteHighlightContrast  = v };
    if (TryReadDouble(description, Crs + "GrainAmount",                       out v)) s = s with { GrainAmount                = v };
    if (TryReadDouble(description, Crs + "GrainSize",                         out v)) s = s with { GrainSize                  = v };
    if (TryReadDouble(description, Crs + "GrainFrequency",                    out v)) s = s with { GrainFrequency             = v };

    // Noise reduction
    if (TryReadDouble(description, Crs + "LuminanceSmoothing",              out v)) s = s with { SmoothnessPercent     = v };
    if (TryReadDouble(description, Crs + "LuminanceNoiseReductionDetail",   out v)) s = s with { LuminanceNrDetail     = v };
    if (TryReadDouble(description, Crs + "LuminanceNoiseReductionContrast", out v)) s = s with { LuminanceNrContrast   = v };
    if (TryReadDouble(description, Crs + "ColorNoiseReduction",             out v)) s = s with { ColorNoiseReduction   = v };
    if (TryReadDouble(description, Crs + "ColorNoiseReductionDetail",       out v)) s = s with { ColorNrDetail         = v };
    if (TryReadDouble(description, Crs + "ColorNoiseReductionSmoothness",   out v)) s = s with { ColorNrSmoothness     = v };

    // B&W
    var grayText = description.Element(Crs + "ConvertToGrayscale")?.Value;
    if (string.Equals(grayText, "True", StringComparison.OrdinalIgnoreCase))
      s = s with { ConvertToGrayscale = true };
    if (TryReadDouble(description, Crs + "GrayMixerRed",     out v)) s = s with { GrayMixerRed     = v };
    if (TryReadDouble(description, Crs + "GrayMixerOrange",  out v)) s = s with { GrayMixerOrange  = v };
    if (TryReadDouble(description, Crs + "GrayMixerYellow",  out v)) s = s with { GrayMixerYellow  = v };
    if (TryReadDouble(description, Crs + "GrayMixerGreen",   out v)) s = s with { GrayMixerGreen   = v };
    if (TryReadDouble(description, Crs + "GrayMixerAqua",    out v)) s = s with { GrayMixerAqua    = v };
    if (TryReadDouble(description, Crs + "GrayMixerBlue",    out v)) s = s with { GrayMixerBlue    = v };
    if (TryReadDouble(description, Crs + "GrayMixerPurple",  out v)) s = s with { GrayMixerPurple  = v };
    if (TryReadDouble(description, Crs + "GrayMixerMagenta", out v)) s = s with { GrayMixerMagenta = v };

    // Split Toning
    if (TryReadDouble(description, Crs + "SplitToningShadowHue",            out v)) s = s with { SplitToningShadowHue            = v };
    if (TryReadDouble(description, Crs + "SplitToningShadowSaturation",     out v)) s = s with { SplitToningShadowSaturation     = v };
    if (TryReadDouble(description, Crs + "SplitToningHighlightHue",         out v)) s = s with { SplitToningHighlightHue         = v };
    if (TryReadDouble(description, Crs + "SplitToningHighlightSaturation",  out v)) s = s with { SplitToningHighlightSaturation  = v };
    if (TryReadDouble(description, Crs + "SplitToningBalance",              out v)) s = s with { SplitToningBalance              = v };

    // Parametric tone curve
    if (TryReadDouble(description, Crs + "ParametricShadows",        out v)) s = s with { ParametricShadows         = v };
    if (TryReadDouble(description, Crs + "ParametricDarks",          out v)) s = s with { ParametricDarks           = v };
    if (TryReadDouble(description, Crs + "ParametricLights",         out v)) s = s with { ParametricLights          = v };
    if (TryReadDouble(description, Crs + "ParametricHighlights",     out v)) s = s with { ParametricHighlights      = v };
    if (TryReadDouble(description, Crs + "ParametricShadowSplit",    out v)) s = s with { ParametricShadowSplit     = v };
    if (TryReadDouble(description, Crs + "ParametricMidtoneSplit",   out v)) s = s with { ParametricMidtoneSplit    = v };
    if (TryReadDouble(description, Crs + "ParametricHighlightSplit", out v)) s = s with { ParametricHighlightSplit  = v };

    // Lens corrections
    if (TryReadDouble(description, Crs + "LensManualDistortionAmount", out v)) s = s with { LensManualDistortion = v };
    if (TryReadDouble(description, Crs + "ChromaticAberrationR",       out v)) s = s with { ChromaticAberrationR = v };
    if (TryReadDouble(description, Crs + "ChromaticAberrationB",       out v)) s = s with { ChromaticAberrationB = v };

    // Defringe
    if (TryReadDouble(description, Crs + "DefringePurpleAmount", out v)) s = s with { DefringePurpleAmount = v };
    if (TryReadDouble(description, Crs + "DefringeGreenAmount",  out v)) s = s with { DefringeGreenAmount  = v };

    // Camera calibration
    if (TryReadDouble(description, Crs + "RedHue",          out v)) s = s with { CalibrationRedHue          = v };
    if (TryReadDouble(description, Crs + "RedSaturation",   out v)) s = s with { CalibrationRedSaturation   = v };
    if (TryReadDouble(description, Crs + "GreenHue",        out v)) s = s with { CalibrationGreenHue        = v };
    if (TryReadDouble(description, Crs + "GreenSaturation", out v)) s = s with { CalibrationGreenSaturation = v };
    if (TryReadDouble(description, Crs + "BlueHue",         out v)) s = s with { CalibrationBlueHue         = v };
    if (TryReadDouble(description, Crs + "BlueSaturation",  out v)) s = s with { CalibrationBlueSaturation  = v };

    // Perspective / Upright
    if (TryReadDouble(description, Crs + "PerspectiveVertical",   out v)) s = s with { PerspectiveVertical   = v };
    if (TryReadDouble(description, Crs + "PerspectiveHorizontal", out v)) s = s with { PerspectiveHorizontal = v };
    if (TryReadDouble(description, Crs + "PerspectiveRotate",     out v)) s = s with { PerspectiveRotate     = v };
    if (TryReadDouble(description, Crs + "PerspectiveScale",      out v)) s = s with { PerspectiveScale      = v };
    if (TryReadDouble(description, Crs + "PerspectiveAspect",     out v)) s = s with { PerspectiveAspect     = v };
    if (TryReadDouble(description, Crs + "PerspectiveX",          out v)) s = s with { PerspectiveX          = v };
    if (TryReadDouble(description, Crs + "PerspectiveY",          out v)) s = s with { PerspectiveY          = v };

    var lookName = description.Element(Crs + "LookName")?.Value;
    if (!string.IsNullOrWhiteSpace(lookName))
      s = s with { LookName = lookName };

    return s;
  }

  private static IReadOnlyList<double>? ReadHslBandList(XElement description, string prefix) {
    var values = new double[HslBandNames.Length];
    var any = false;
    for (var i = 0; i < HslBandNames.Length; i++) {
      if (TryReadDouble(description, Crs + (prefix + HslBandNames[i]), out var v)) {
        values[i] = v;
        if (Math.Abs(v) > 1e-6) any = true;
      }
    }
    return any ? values : null;
  }

  private static bool TryReadDouble(XElement description, XName name, out double value) {
    value = 0;
    var text = description.Element(name)?.Value;
    return !string.IsNullOrWhiteSpace(text)
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
  }

  private static int ShiftToKelvin(double shift) {
    var kelvin = (int)Math.Round(5500 + shift * 35);
    return Math.Clamp(kelvin, 2000, 50000);
  }

  private static double KelvinToShift(double kelvin) {
    var shift = (kelvin - 5500) / 35.0;
    return Math.Clamp(shift, -100, 100);
  }

  // ---------- pm:developSettings (extras only) ----------

  private static DevelopExtras ReadExtras(XElement description) {
    var element = description.Element(Pm + "developSettings");
    var json = element?.Value;
    if (string.IsNullOrWhiteSpace(json))
      return new DevelopExtras();
    try {
      return JsonSerializer.Deserialize<DevelopExtras>(json, JsonOptions) ?? new DevelopExtras();
    } catch {
      return new DevelopExtras();
    }
  }

  private static DevelopSettings ApplyExtras(DevelopSettings baseSettings, DevelopExtras extras)
    => baseSettings with {
      RotationDegrees = extras.RotationDegrees,
      RedGain = extras.RedGain,
      GreenGain = extras.GreenGain,
      BlueGain = extras.BlueGain,
      ToneCurveInterpolation = extras.ToneCurveInterpolation,
      ToneCurvePoints = extras.ToneCurvePoints,
      RedCurvePoints = extras.RedCurvePoints,
      GreenCurvePoints = extras.GreenCurvePoints,
      BlueCurvePoints = extras.BlueCurvePoints,
      LookOpacity = extras.LookOpacity,
      WatermarkText = string.IsNullOrEmpty(extras.WatermarkText) ? null : extras.WatermarkText,
      WatermarkOpacity = extras.WatermarkOpacity,
      WatermarkPosition = string.IsNullOrEmpty(extras.WatermarkPosition) ? "BottomRight" : extras.WatermarkPosition,
      WatermarkFontSize = extras.WatermarkFontSize <= 0 ? 24 : extras.WatermarkFontSize,
      AiDenoiseStrength = extras.AiDenoiseStrength,
      AiDenoiseModel = extras.AiDenoiseModel,
      AiUpscaleFactor = extras.AiUpscaleFactor < 1 ? 1 : extras.AiUpscaleFactor,
      AiUpscaleModel = extras.AiUpscaleModel,
      AiColorizeAmount = Math.Clamp(extras.AiColorizeAmount, 0, 1),
      AiColorizeModel = extras.AiColorizeModel
    };

  // ---------- pm:developHistory (snapshot stack) ----------

  /// <summary>JSON shape for one history entry. We round-trip the entire
  /// <see cref="DevelopSettings"/> rather than diffing — readers stay simple,
  /// and the cap (20 entries) keeps the XMP packet bounded in size.</summary>
  private sealed record HistoryEntryDto(string TimestampUtc, string? Label, DevelopSettings Settings);

  private static IReadOnlyList<DevelopSnapshot> ReadHistory(XElement description) {
    var element = description.Element(Pm + "developHistory");
    var json = element?.Value;
    if (string.IsNullOrWhiteSpace(json))
      return Array.Empty<DevelopSnapshot>();
    try {
      var dto = JsonSerializer.Deserialize<List<HistoryEntryDto>>(json, JsonOptions);
      if (dto is null)
        return Array.Empty<DevelopSnapshot>();
      var result = new List<DevelopSnapshot>(dto.Count);
      foreach (var entry in dto) {
        if (!DateTime.TryParse(entry.TimestampUtc, CultureInfo.InvariantCulture,
              DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts))
          continue;
        result.Add(new DevelopSnapshot(ts, entry.Label, entry.Settings));
      }
      return result;
    } catch {
      return Array.Empty<DevelopSnapshot>();
    }
  }

  private static void WriteHistory(XElement description, IReadOnlyList<DevelopSnapshot> history) {
    description.Element(Pm + "developHistory")?.Remove();
    if (history.Count == 0)
      return;
    EnsureNamespace(description, "pm", Pm);
    var dto = history
      .Select(s => new HistoryEntryDto(s.TimestampUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), s.Label, s.Settings))
      .ToList();
    description.Add(new XElement(Pm + "developHistory", JsonSerializer.Serialize(dto, JsonOptions)));
  }

  // ---------- helpers ----------

  private static void EnsureNamespace(XElement description, string prefix, XNamespace ns) {
    if (description.Attribute(XNamespace.Xmlns + prefix) is null)
      description.Add(new XAttribute(XNamespace.Xmlns + prefix, ns.NamespaceName));
  }

  private static XDocument MakeEmptyXmp() => new(
    new XDeclaration("1.0", "UTF-8", null),
    new XElement(X + "xmpmeta",
      new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName),
      new XElement(Rdf + "RDF",
        new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName),
        new XElement(Rdf + "Description",
          new XAttribute(Rdf + "about", string.Empty),
          new XAttribute(XNamespace.Xmlns + "pm", Pm.NamespaceName)))));

  private static byte[] SerializeUtf8(XDocument doc) {
    using var ms = new MemoryStream();
    var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    using (var writer = XmlWriter.Create(ms, new XmlWriterSettings {
      OmitXmlDeclaration = false, Indent = true, IndentChars = "  ", Encoding = utf8
    }))
      doc.Save(writer);
    return ms.ToArray();
  }
}
