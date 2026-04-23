namespace PhotoManager.Core.Develop;

/// <summary>
/// Non-destructive develop parameters applied by <see cref="ImageDeveloper"/>.
/// All fields default to a no-op so callers can build incrementally.
///
/// <para><see cref="RotationDegrees"/> — multiples of 90° only (0, 90, 180, 270).</para>
/// <para><see cref="ExposureStops"/> — EV shift; ±1 doubles/halves brightness. Range roughly ±3.</para>
/// <para><see cref="ContrastPercent"/> — -100..+100 where 0 is unchanged; +50 boosts S-curve.</para>
/// <para><see cref="SaturationPercent"/> — -100 (grayscale) to +100 (double saturation).</para>
/// <para><see cref="TemperatureShift"/> — -100..+100. Negative pushes blue (cooler), positive red (warmer).</para>
/// </summary>
public sealed record DevelopSettings(
  int RotationDegrees = 0,
  double ExposureStops = 0,
  double ContrastPercent = 0,
  double SaturationPercent = 0,
  double TemperatureShift = 0
) {
  public bool IsIdentity =>
    this.RotationDegrees == 0
    && Math.Abs(this.ExposureStops) < 1e-6
    && Math.Abs(this.ContrastPercent) < 1e-6
    && Math.Abs(this.SaturationPercent) < 1e-6
    && Math.Abs(this.TemperatureShift) < 1e-6;
}
