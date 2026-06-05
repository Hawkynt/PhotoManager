namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Counts how many develop knobs are away from their default values for a
/// <see cref="DevelopSettings"/> instance. Drives the "ready (N edits)"
/// pill in the develop window's bottom status bar so the user always sees
/// at a glance how much non-identity work is on the image.
///
/// Pure logic so it can be unit-tested without spinning up Avalonia. The
/// per-slider counting is deliberate (so each separate adjustment shows as
/// its own +1) rather than collapsed to "any edit at all".
/// </summary>
public static class AppliedEditsCounter {
  /// <summary>
  /// Number of distinct adjustments active on <paramref name="settings"/>.
  /// Local-adjustment list contributes its <c>Count</c>; everything else
  /// contributes 0 or 1 per knob. The slider-tolerance is 1e-6.
  /// </summary>
  public static int Count(DevelopSettings settings) {
    ArgumentNullException.ThrowIfNull(settings);
    var n = 0;
    if (Math.Abs(settings.ExposureStops)       > 1e-6) n++;
    if (Math.Abs(settings.ContrastPercent)     > 1e-6) n++;
    if (Math.Abs(settings.HighlightsPercent)   > 1e-6) n++;
    if (Math.Abs(settings.ShadowsPercent)      > 1e-6) n++;
    if (Math.Abs(settings.WhitesPercent)       > 1e-6) n++;
    if (Math.Abs(settings.BlacksPercent)       > 1e-6) n++;
    if (Math.Abs(settings.ClarityPercent)      > 1e-6) n++;
    if (Math.Abs(settings.VibrancePercent)     > 1e-6) n++;
    if (Math.Abs(settings.SaturationPercent)   > 1e-6) n++;
    if (Math.Abs(settings.TemperatureShift)    > 1e-6) n++;
    if (Math.Abs(settings.TintShift)           > 1e-6) n++;
    if (Math.Abs(settings.RedGain)             > 1e-6) n++;
    if (Math.Abs(settings.GreenGain)           > 1e-6) n++;
    if (Math.Abs(settings.BlueGain)            > 1e-6) n++;
    if (Math.Abs(settings.CropAngleDegrees)    > 1e-6) n++;
    if (settings.RotationDegrees              != 0) n++;
    if (settings.CropLeft   >       1e-6
     || settings.CropTop    >       1e-6
     || settings.CropRight  < 1 -   1e-6
     || settings.CropBottom < 1 -   1e-6) n++;
    if (settings.AiDenoiseStrength             > 1e-6) n++;
    if (settings.AiUpscaleFactor               > 1)    n++;
    if (settings.AiColorizeAmount              > 1e-6) n++;
    if (!string.IsNullOrEmpty(settings.LookName) && settings.LookOpacity > 1e-6) n++;
    if (settings.LocalAdjustments is { Count: > 0 } locs)
      n += locs.Count;
    return n;
  }
}
