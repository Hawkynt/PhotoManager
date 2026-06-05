namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Mask geometry for a local adjustment. A flat record (rather than a
/// type hierarchy) so System.Text.Json round-trips cleanly through
/// pm:developSettings without polymorphism attributes.
///
/// Coordinates are normalised image-space (0..1), so masks survive
/// resizing the preview vs the full image.
///
/// Linear gradient: a line from (X0,Y0) to (X1,Y1). Mask weight is 0
/// at the "Zero" point projecting onto the line and 1 at the "Full"
/// point — pixels off either end clamp to the nearest end's weight.
///
/// Radial gradient: an ellipse centred at (CenterX, CenterY) with
/// half-axes (RadiusX, RadiusY), optionally rotated by Angle degrees.
/// Feather (0..100) controls the falloff curve; Invert flips inside
/// vs outside.
/// </summary>
public sealed record LocalMask(
  LocalMaskType Type = LocalMaskType.Linear,
  double X0 = 0.5,
  double Y0 = 0.0,
  double X1 = 0.5,
  double Y1 = 1.0,
  double CenterX = 0.5,
  double CenterY = 0.5,
  double RadiusX = 0.25,
  double RadiusY = 0.25,
  double Feather = 50,
  double Angle = 0,
  bool Invert = false,
  // Brush-only: every dab the user painted. Each has its own normalised
  // position + radius + signed flow. Negative flow = eraser. Adobe stores
  // these in a crs:Dabs sequence as "u x,y,r" strings.
  IReadOnlyList<BrushDab>? BrushDabs = null,
  // Range filter — narrows the geometric mask to pixels whose luminance
  // falls inside [min, max]. Defaults (0..1) are a no-op. Feather softens
  // the edge of the range so the constraint isn't a hard cliff.
  double LuminanceRangeMin = 0,
  double LuminanceRangeMax = 1,
  double LuminanceRangeFeather = 0.1,
  // Color range filter — same idea but on the pixel's hue (0..1 fraction
  // of the colour wheel; wraps around so 0.95..0.05 selects reds across
  // the wheel boundary). Defaults select the full wheel.
  double HueRangeMin = 0,
  double HueRangeMax = 1,
  double HueRangeFeather = 0.05,
  // How this mask combines with earlier masks in the same adjustment.
  // The primary mask (first in the list) is implicitly Add; sub-masks
  // can Subtract from it or Intersect with it. Adobe's wire format
  // is just a MaskValue scale (+1 / -1) plus order, but the explicit
  // op makes our UI / round-trip easier to reason about.
  MaskCombineOp Combine = MaskCombineOp.Add
);

/// <summary>How a sub-mask combines with the primary mask weight.</summary>
public enum MaskCombineOp {
  /// <summary>Add this mask's weight to the running total (primary's default).</summary>
  Add = 0,
  /// <summary>Subtract this mask's weight from the running total — carves a hole.</summary>
  Subtract = 1,
  /// <summary>Multiply with this mask's weight — narrows to the intersection.</summary>
  Intersect = 2
}

/// <summary>One brush stroke dab in normalised image coords.</summary>
public sealed record BrushDab(double X, double Y, double Radius, double Flow);

/// <summary>Discriminator for <see cref="LocalMask.Type"/>.</summary>
public enum LocalMaskType {
  /// <summary>Two-point gradient (Adobe <c>Mask/Gradient</c>).</summary>
  Linear = 0,
  /// <summary>Centred ellipse gradient (Adobe <c>Mask/CircularGradient</c>).</summary>
  Radial = 1,
  /// <summary>User-painted dab cloud (Adobe <c>Mask/Brush</c>).</summary>
  Brush = 2,
  /// <summary>Content-aware fill / object removal. The brush dabs define the
  /// mask region to remove; at render time the mask is dilated by a small
  /// feather margin and fed to <see cref="Hawkynt.PhotoManager.Core.Segmentation.OnnxInpainter"/>
  /// which replaces those pixels with surrounding content.</summary>
  Inpaint = 3
}

/// <summary>
/// One local adjustment: a mask (Linear / Radial) plus the per-mask
/// develop sliders. The slider set mirrors Adobe's "local"-prefixed
/// crs: tags (Local Exposure 2012, Local Contrast 2012, etc.) so values
/// emitted by PhotoManager show up in Lightroom and vice versa.
/// </summary>
public sealed record LocalAdjustment(
  LocalMask Mask,
  /// <summary>Display name for the UI list. Doesn't affect pixels.</summary>
  string Name = "",
  /// <summary>Master strength multiplier 0..1. Adobe's <c>CorrectionAmount</c>.</summary>
  double Amount = 1.0,
  double Exposure = 0,
  double Contrast = 0,
  double Highlights = 0,
  double Shadows = 0,
  double Saturation = 0,
  double Temperature = 0,
  double Tint = 0,
  double Clarity = 0,
  // Adobe local-only sliders we hadn't modeled before. Luminance is a
  // luminance-only lift / crush; ToningHue + ToningSaturation tint the
  // masked region with a single-colour cast (think of it as a per-mask
  // mini-version of split toning); Defringe desaturates purple/green
  // halos within the mask only.
  double Luminance = 0,
  double ToningHue = 0,
  double ToningSaturation = 0,
  double Defringe = 0,
  // Optional sub-masks combined with the primary <see cref="Mask"/>
  // via each entry's <see cref="LocalMask.Combine"/> op. Lets a user
  // build "this gradient AND only inside this ellipse, AND not the
  // bright bits" by stacking masks.
  IReadOnlyList<LocalMask>? SubMasks = null
) {
  public bool IsZero =>
       Math.Abs(this.Exposure)    < 1e-6
    && Math.Abs(this.Contrast)    < 1e-6
    && Math.Abs(this.Highlights)  < 1e-6
    && Math.Abs(this.Shadows)     < 1e-6
    && Math.Abs(this.Saturation)  < 1e-6
    && Math.Abs(this.Temperature) < 1e-6
    && Math.Abs(this.Tint)        < 1e-6
    && Math.Abs(this.Clarity)     < 1e-6
    && Math.Abs(this.Luminance)        < 1e-6
    && Math.Abs(this.ToningSaturation) < 1e-6
    && Math.Abs(this.Defringe)         < 1e-6;
}
