using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// Immutable 3D lookup table — a creative "look" baked as an N×N×N cube of
/// RGB samples. Every input colour (after tone / curves) gets remapped to
/// the corresponding output via trilinear interpolation between the 8
/// surrounding cube vertices.
/// </summary>
/// <param name="Size">Cube dimension N. Adobe ships .cube files at 17, 25, 33, 64.</param>
/// <param name="Table">Interleaved RGB samples, length Size³ × 3, indexed as
/// (((b * Size + g) * Size + r) * 3 + c). Each value is normalised 0..1.</param>
public sealed record Lut3D(int Size, float[] Table) {
  /// <summary>
  /// Apply this LUT to <paramref name="source"/> in place via trilinear
  /// interpolation between the 8 surrounding cube vertices. <paramref name="opacity"/>
  /// linearly blends between input (0) and looked-up output (1).
  /// </summary>
  public static void Apply(Image<Rgba32> source, Lut3D lut, double opacity = 1.0) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(lut);
    var amount = (float)Math.Clamp(opacity, 0.0, 1.0);
    if (amount < 1e-6f)
      return;

    var n = lut.Size;
    var nMinus1 = n - 1;
    var table = lut.Table;

    source.ProcessPixelRows(accessor => {
      for (var y = 0; y < accessor.Height; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          ref var px = ref row[x];
          var rIn = px.R / 255f;
          var gIn = px.G / 255f;
          var bIn = px.B / 255f;

          var rf = rIn * nMinus1;
          var gf = gIn * nMinus1;
          var bf = bIn * nMinus1;

          var r0 = (int)rf; var g0 = (int)gf; var b0 = (int)bf;
          if (r0 >= nMinus1) r0 = nMinus1 - 1;
          if (g0 >= nMinus1) g0 = nMinus1 - 1;
          if (b0 >= nMinus1) b0 = nMinus1 - 1;
          if (r0 < 0) r0 = 0;
          if (g0 < 0) g0 = 0;
          if (b0 < 0) b0 = 0;
          var r1 = r0 + 1; var g1 = g0 + 1; var b1 = b0 + 1;

          var dr = rf - r0;
          var dg = gf - g0;
          var db = bf - b0;

          // Trilinear: lerp along R first, then G, then B.
          float c000R, c000G, c000B; Sample(table, n, r0, g0, b0, out c000R, out c000G, out c000B);
          float c100R, c100G, c100B; Sample(table, n, r1, g0, b0, out c100R, out c100G, out c100B);
          float c010R, c010G, c010B; Sample(table, n, r0, g1, b0, out c010R, out c010G, out c010B);
          float c110R, c110G, c110B; Sample(table, n, r1, g1, b0, out c110R, out c110G, out c110B);
          float c001R, c001G, c001B; Sample(table, n, r0, g0, b1, out c001R, out c001G, out c001B);
          float c101R, c101G, c101B; Sample(table, n, r1, g0, b1, out c101R, out c101G, out c101B);
          float c011R, c011G, c011B; Sample(table, n, r0, g1, b1, out c011R, out c011G, out c011B);
          float c111R, c111G, c111B; Sample(table, n, r1, g1, b1, out c111R, out c111G, out c111B);

          var c00R = c000R + (c100R - c000R) * dr;
          var c00G = c000G + (c100G - c000G) * dr;
          var c00B = c000B + (c100B - c000B) * dr;
          var c10R = c010R + (c110R - c010R) * dr;
          var c10G = c010G + (c110G - c010G) * dr;
          var c10B = c010B + (c110B - c010B) * dr;
          var c01R = c001R + (c101R - c001R) * dr;
          var c01G = c001G + (c101G - c001G) * dr;
          var c01B = c001B + (c101B - c001B) * dr;
          var c11R = c011R + (c111R - c011R) * dr;
          var c11G = c011G + (c111G - c011G) * dr;
          var c11B = c011B + (c111B - c011B) * dr;

          var c0R = c00R + (c10R - c00R) * dg;
          var c0G = c00G + (c10G - c00G) * dg;
          var c0B = c00B + (c10B - c00B) * dg;
          var c1R = c01R + (c11R - c01R) * dg;
          var c1G = c01G + (c11G - c01G) * dg;
          var c1B = c01B + (c11B - c01B) * dg;

          var outR = c0R + (c1R - c0R) * db;
          var outG = c0G + (c1G - c0G) * db;
          var outB = c0B + (c1B - c0B) * db;

          var blendedR = rIn + (outR - rIn) * amount;
          var blendedG = gIn + (outG - gIn) * amount;
          var blendedB = bIn + (outB - bIn) * amount;

          px.R = (byte)Math.Clamp((int)Math.Round(blendedR * 255f), 0, 255);
          px.G = (byte)Math.Clamp((int)Math.Round(blendedG * 255f), 0, 255);
          px.B = (byte)Math.Clamp((int)Math.Round(blendedB * 255f), 0, 255);
        }
      }
    });
  }

  private static void Sample(float[] table, int n, int r, int g, int b, out float outR, out float outG, out float outB) {
    var idx = ((b * n + g) * n + r) * 3;
    outR = table[idx];
    outG = table[idx + 1];
    outB = table[idx + 2];
  }
}
