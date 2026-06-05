using MetadataExtractor.Formats.Exif;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Hawkynt.PhotoManager.Core.Hdr;

public sealed record HdrOptions(
  bool AlignBeforeMerge = true,
  ToneMapOperator Operator = ToneMapOperator.Reinhard,
  double WhitePoint = 0,
  double DragoBias = 0.85,
  double Saturation = 1.0,
  int? PreviewLongEdge = null
);

public sealed record HdrBracketEntry(FileInfo File, double ExposureSeconds);

public sealed record HdrMergeResult(
  Image<Rgba32> Image,
  IReadOnlyList<Translation> Alignments,
  HdrRadianceMap RadianceMap
);

/// <summary>
/// End-to-end HDR pipeline: per-file decode → optional MTB alignment →
/// Debevec response recovery + radiance build → tone-mapped LDR. Files
/// must be supplied in any order; the orchestrator sorts by exposure time
/// (longest exposure last so the median bracket is used as alignment ref).
/// </summary>
public static class HdrMerger {
  public static async Task<HdrMergeResult> MergeAsync(
    IReadOnlyList<FileInfo> bracketFiles,
    HdrOptions options,
    CancellationToken cancellationToken = default
  ) {
    if (bracketFiles.Count < 2)
      throw new ArgumentException("HDR merge needs at least two bracket frames.", nameof(bracketFiles));

    var entries = await Task.Run(() => bracketFiles.Select(LoadEntry).ToList(), cancellationToken);
    return await MergeAsync(entries, options, cancellationToken);
  }

  public static async Task<HdrMergeResult> MergeAsync(
    IReadOnlyList<HdrBracketEntry> entries,
    HdrOptions options,
    CancellationToken cancellationToken = default
  ) {
    if (entries.Count < 2)
      throw new ArgumentException("HDR merge needs at least two bracket frames.", nameof(entries));

    return await Task.Run(() => MergeCore(entries, options, cancellationToken), cancellationToken);
  }

  internal static HdrMergeResult MergeCore(
    IReadOnlyList<HdrBracketEntry> entries,
    HdrOptions options,
    CancellationToken cancellationToken
  ) {
    var sorted = entries.OrderBy(e => e.ExposureSeconds).ToList();
    var loaded = new List<Image<Rgba32>>(sorted.Count);
    try {
      foreach (var entry in sorted) {
        cancellationToken.ThrowIfCancellationRequested();
        loaded.Add(LoadAndOptionallyResize(entry.File, options.PreviewLongEdge));
      }

      NormalizeToCanvas(loaded);

      var refIndex = sorted.Count / 2;
      var alignments = new Translation[sorted.Count];

      if (options.AlignBeforeMerge) {
        for (var i = 0; i < sorted.Count; i++) {
          cancellationToken.ThrowIfCancellationRequested();
          if (i == refIndex) {
            alignments[i] = new Translation(0, 0);
            continue;
          }
          alignments[i] = ExposureAlignment.Align(loaded[refIndex], loaded[i]);
        }

        for (var i = 0; i < sorted.Count; i++) {
          if (i == refIndex || (alignments[i].Dx == 0 && alignments[i].Dy == 0))
            continue;
          var shifted = ExposureAlignment.Shift(loaded[i], alignments[i]);
          loaded[i].Dispose();
          loaded[i] = shifted;
        }
      }

      var radiance = DebevecResponseRecovery.Recover(
        loaded,
        sorted.Select(e => e.ExposureSeconds).ToArray());

      cancellationToken.ThrowIfCancellationRequested();

      var ldr = ToneMapper.Map(
        radiance,
        options.Operator,
        options.WhitePoint,
        options.DragoBias,
        options.Saturation);

      return new HdrMergeResult(ldr, alignments, radiance);
    } finally {
      foreach (var img in loaded)
        img.Dispose();
    }
  }

  internal static HdrBracketEntry LoadEntry(FileInfo file) {
    var seconds = ReadExposureSeconds(file) ?? 1.0;
    return new HdrBracketEntry(file, seconds);
  }

  /// <summary>
  /// Pulls EXIF ExposureTime (preferred) or ShutterSpeedValue (fallback,
  /// APEX-encoded) from a JPEG/RAW. Returns null if neither is present so
  /// the caller can decide whether to default or surface the gap.
  /// </summary>
  public static double? ReadExposureSeconds(FileInfo file) {
    if (!file.Exists)
      return null;
    IReadOnlyList<MetadataExtractor.Directory> dirs;
    try {
      dirs = ImageMetadataReader.ReadMetadata(file.FullName);
    } catch {
      return null;
    }

    foreach (var subIfd in dirs.OfType<ExifSubIfdDirectory>()) {
      if (subIfd.TryGetRational(ExifDirectoryBase.TagExposureTime, out var rat))
        return rat.ToDouble();
    }
    foreach (var ifd0 in dirs.OfType<ExifIfd0Directory>()) {
      if (ifd0.TryGetRational(ExifDirectoryBase.TagExposureTime, out var rat))
        return rat.ToDouble();
    }
    foreach (var subIfd in dirs.OfType<ExifSubIfdDirectory>()) {
      if (subIfd.TryGetRational(ExifDirectoryBase.TagShutterSpeed, out var apex))
        return Math.Pow(2.0, -apex.ToDouble());
    }
    return null;
  }

  internal static Image<Rgba32> LoadAndOptionallyResize(FileInfo file, int? previewLongEdge) {
    var img = Image.Load<Rgba32>(file.FullName);
    if (previewLongEdge is null)
      return img;

    var longEdge = Math.Max(img.Width, img.Height);
    if (longEdge <= previewLongEdge.Value)
      return img;

    var scale = (double)previewLongEdge.Value / longEdge;
    var nw = Math.Max(1, (int)Math.Round(img.Width * scale));
    var nh = Math.Max(1, (int)Math.Round(img.Height * scale));
    img.Mutate(c => c.Resize(nw, nh));
    return img;
  }

  /// Resizes every frame to share a common (maxWidth × maxHeight) canvas,
  /// preserving aspect ratio and centring with black letterbox padding. The
  /// downstream alignment + Debevec recovery require pixel-aligned frames; if
  /// the user picks a mixed bracket (different cameras, mismatched crops) we
  /// fit-and-pad instead of refusing.
  internal static void NormalizeToCanvas(IList<Image<Rgba32>> images) {
    var maxW = images.Max(i => i.Width);
    var maxH = images.Max(i => i.Height);
    for (var i = 0; i < images.Count; i++) {
      if (images[i].Width == maxW && images[i].Height == maxH)
        continue;
      var src = images[i];
      var resized = src.Clone(c => c.Resize(new ResizeOptions {
        Size = new Size(maxW, maxH),
        Mode = ResizeMode.Pad,
        Position = AnchorPositionMode.Center,
        PadColor = Color.Black
      }));
      src.Dispose();
      images[i] = resized;
    }
  }
}
