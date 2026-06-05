namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Tunable parameters for the auto-correction algorithms. Every constant
/// that was previously hardcoded inside <see cref="AutoWhiteBalance"/>,
/// <see cref="AutoDeveloper.AutoTone"/>, and
/// <see cref="AutoDeveloper.AutoChannelStretch"/> is exposed here so
/// the user (or a per-camera preset) can bias the algorithms away from
/// outliers / noise or toward more aggressive correction.
///
/// All defaults match the previous hardcoded behavior so existing
/// callers see identical results when passing <c>new AutoAdjustOptions()</c>.
/// </summary>
public sealed record AutoAdjustOptions(
  // ── Auto White Balance ─────────────────────────────────────────
  /// <summary>Percentile of pixels to clip from the dark end before
  /// computing the gray-world average. Clipping the darkest N% removes
  /// noisy shadow pixels that skew the average toward the sensor's
  /// dark-current color. 0 = use all pixels. Range: 0..0.5.</summary>
  double WbClipLowPct = 0.02,

  /// <summary>Percentile to clip from the bright end. Removes blown
  /// highlights that are clamped to (255,255,255) and therefore carry
  /// no color information. Range: 0..0.5.</summary>
  double WbClipHighPct = 0.02,

  /// <summary>Lower bound of the "bright patch" luminance window used
  /// for highlight-based WB refinement. Range: 0..255.</summary>
  int WbHighlightLumMin = 200,

  /// <summary>Upper bound of the bright-patch window. Pixels above this
  /// are likely clipped and ignored. Range: 0..255.</summary>
  int WbHighlightLumMax = 250,

  /// <summary>Minimum number of pixels in the bright-patch window before
  /// the highlight refinement is blended in. Below this, only gray-world
  /// is used. Higher = more conservative (needs a bigger white area).
  /// Range: 1..10000.</summary>
  int WbMinHighlightPixels = 10,

  /// <summary>Blend weight of the highlight refinement vs gray-world.
  /// 0 = pure gray-world, 1 = pure highlight-based. 0.5 = equal blend.
  /// Range: 0..1.</summary>
  double WbHighlightBlendWeight = 0.5,

  /// <summary>Sensitivity multiplier for the temperature correction.
  /// Higher = more aggressive correction. 50 matches the previous
  /// hardcoded behavior. Range: 10..200.</summary>
  double WbTemperatureSensitivity = 50.0,

  /// <summary>Sensitivity multiplier for the tint correction.
  /// Range: 10..200.</summary>
  double WbTintSensitivity = 50.0,

  // ── Auto Tone ──────────────────────────────────────────────────
  /// <summary>Fraction of darkest pixels to clip for black-point
  /// detection (0.005 = 0.5%). Lower = less aggressive black crush.
  /// Range: 0..0.1.</summary>
  double ToneBlackClipPct = 0.005,

  /// <summary>Fraction of brightest pixels to clip for white-point
  /// detection (0.005 = 0.5%). Range: 0..0.1.</summary>
  double ToneWhiteClipPct = 0.005,

  /// <summary>Shadow recovery kicks in when the 5th-percentile
  /// luminance is below this value. Higher = more shadow lift on
  /// dark images. Range: 0..128.</summary>
  int ToneShadowRecoveryThreshold = 40,

  /// <summary>Highlight recovery kicks in when the 95th-percentile
  /// luminance is above this value. Lower = more highlight pull on
  /// bright images. Range: 128..255.</summary>
  int ToneHighlightRecoveryThreshold = 215,

  /// <summary>Multiplier for shadow/highlight recovery strength.
  /// Higher = more aggressive recovery. Range: 0.5..5.0.</summary>
  double ToneRecoveryStrength = 1.5,

  // ── Auto Channel Stretch ───────────────────────────────────────
  /// <summary>Top percentile used to find each channel's effective
  /// white point (0.995 = 99.5%). Lower values clip more aggressively,
  /// which makes the stretch more robust to bright outliers but can
  /// crush real highlights. Range: 0.9..1.0.</summary>
  double StretchTopPercentile = 0.995
);
