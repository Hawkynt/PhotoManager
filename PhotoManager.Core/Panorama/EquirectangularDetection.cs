using Hawkynt.PhotoManager.Core.Metadata;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Panorama;

/// <summary>
/// Lightweight heuristic for "this looks like a 360° equirectangular pano".
/// Any one of these flips the answer to true:
///   • explicit XMP <c>GPano:ProjectionType</c> = "equirectangular" (Google photo-sphere tag)
///   • aspect ratio within 5% of 2:1
///   • width &gt;= 4096 with an aspect ratio at least roughly panoramic
/// FullMetadata is forwarded for future use; GPano isn't first-class on the
/// model yet, so callers wanting an unconditional override pass the
/// projection string directly via <see cref="LooksEquirectangular(int,int,string?)"/>.
/// </summary>
public static class EquirectangularDetection {
  private const double TargetAspect = 2.0;
  private const double AspectTolerance = 0.05;
  private const int LargeWidthThreshold = 4096;

  public static bool LooksEquirectangular(Image<Rgba32> source, FullMetadata? metadata)
    => LooksEquirectangular(source?.Width ?? 0, source?.Height ?? 0, ExtractProjectionType(metadata));

  public static bool LooksEquirectangular(int width, int height, string? projectionType) {
    if (!string.IsNullOrWhiteSpace(projectionType)
        && projectionType.Contains("equirectangular", StringComparison.OrdinalIgnoreCase))
      return true;

    if (width <= 0 || height <= 0)
      return false;

    var aspect = (double)width / height;
    var diff = Math.Abs(aspect - TargetAspect) / TargetAspect;
    if (diff <= AspectTolerance)
      return true;

    // Huge stitched panos are usually wider than 16:9 (~1.78). Only
    // greenlight by sheer width when the aspect is at least vaguely
    // landscape; portrait/square mega-shots almost certainly aren't panos.
    return width >= LargeWidthThreshold && aspect >= 1.7;
  }

  /// <summary>
  /// Best-effort extraction of GPano:ProjectionType from a FullMetadata
  /// snapshot. The model doesn't expose GPano directly today, so we look
  /// for an "equirectangular" hint anywhere in the keyword list as a
  /// soft-import path until the reader grows a dedicated field.
  /// </summary>
  private static string? ExtractProjectionType(FullMetadata? metadata) {
    if (metadata is null)
      return null;
    foreach (var keyword in metadata.Keywords) {
      if (keyword is null) continue;
      if (keyword.Contains("equirectangular", StringComparison.OrdinalIgnoreCase))
        return "equirectangular";
    }
    return null;
  }
}
