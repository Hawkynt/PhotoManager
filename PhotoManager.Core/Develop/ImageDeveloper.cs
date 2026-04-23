using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoManager.Core.Develop;

/// <summary>
/// Applies <see cref="DevelopSettings"/> to an image, producing a new image.
/// Adjustments are done in linear-ish sRGB space (ImageSharp's native Rgba32)
/// — good enough for a casual developer; a true RAW workflow would work in
/// linear scene-referred data, but our "simple developer" ships good-looking
/// JPEGs and not scientifically accurate ones.
/// </summary>
public static class ImageDeveloper {
  /// <summary>
  /// Returns a new image with <paramref name="settings"/> applied. The input
  /// is not mutated. Identity settings return a clone for interface parity.
  /// </summary>
  public static Image<Rgba32> Apply(Image<Rgba32> source, DevelopSettings settings) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(settings);

    var output = source.Clone();

    if (settings.RotationDegrees != 0)
      output.Mutate(c => c.Rotate(NormalizeRotation(settings.RotationDegrees)));

    if (!settings.IsIdentity)
      ApplyPixelAdjustments(output, settings);

    return output;
  }

  /// <summary>Convenience: load, develop, save as JPEG.</summary>
  public static async Task RenderToJpegAsync(
    FileInfo sourceFile,
    FileInfo destinationFile,
    DevelopSettings settings,
    int jpegQuality = 92,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(sourceFile);
    ArgumentNullException.ThrowIfNull(destinationFile);
    if (!sourceFile.Exists)
      throw new FileNotFoundException("Source image not found.", sourceFile.FullName);

    using var source = await Image.LoadAsync<Rgba32>(sourceFile.FullName, cancellationToken);
    using var developed = Apply(source, settings);

    destinationFile.Directory?.Create();
    var encoder = new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = jpegQuality };
    await developed.SaveAsJpegAsync(destinationFile.FullName, encoder, cancellationToken);
  }

  internal static void ApplyPixelAdjustments(Image<Rgba32> image, DevelopSettings settings) {
    var exposureMultiplier = Math.Pow(2, settings.ExposureStops);
    var contrastFactor = 1 + settings.ContrastPercent / 100.0;
    var saturationFactor = 1 + settings.SaturationPercent / 100.0;
    // Map -100..+100 to a per-channel multiplier. At +100 red ×1.3 / blue ×0.7
    // which is a roughly warm-white cast; at -100 the reverse.
    var redMult = 1 + settings.TemperatureShift / 333.0;
    var blueMult = 1 - settings.TemperatureShift / 333.0;

    image.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var px = row[x];
          var r = px.R / 255.0;
          var g = px.G / 255.0;
          var b = px.B / 255.0;

          // Exposure
          r *= exposureMultiplier;
          g *= exposureMultiplier;
          b *= exposureMultiplier;

          // Temperature
          r *= redMult;
          b *= blueMult;

          // Contrast about 0.5 midgray
          r = (r - 0.5) * contrastFactor + 0.5;
          g = (g - 0.5) * contrastFactor + 0.5;
          b = (b - 0.5) * contrastFactor + 0.5;

          // Saturation via luminance-preserving interpolation
          var lum = 0.299 * r + 0.587 * g + 0.114 * b;
          r = lum + (r - lum) * saturationFactor;
          g = lum + (g - lum) * saturationFactor;
          b = lum + (b - lum) * saturationFactor;

          row[x] = new Rgba32(ToByte(r), ToByte(g), ToByte(b), px.A);
        }
      }
    });
  }

  private static byte ToByte(double v) {
    if (v <= 0) return 0;
    if (v >= 1) return 255;
    return (byte)Math.Round(v * 255);
  }

  private static RotateMode NormalizeRotation(int degrees) {
    var norm = ((degrees % 360) + 360) % 360;
    return norm switch {
      90 => RotateMode.Rotate90,
      180 => RotateMode.Rotate180,
      270 => RotateMode.Rotate270,
      _ => RotateMode.None
    };
  }
}
