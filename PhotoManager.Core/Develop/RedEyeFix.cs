using PhotoManager.Core.Detection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Develop;

/// <summary>
/// Desaturates a normalised bounding box to neutralise flash-induced
/// red-eye. The box is expected to come from <see cref="RedEyeFinder.Find"/>;
/// only pixels that look red inside the box get desaturated, so a too-large
/// box won't bleach the surrounding face.
/// </summary>
public static class RedEyeFix {
  public static void Apply(Image<Rgba32> image, NormalizedBoundingBox box) {
    ArgumentNullException.ThrowIfNull(image);
    var w = image.Width;
    var h = image.Height;
    if (w <= 0 || h <= 0)
      return;

    var x0 = Math.Clamp((int)Math.Round(box.X * w), 0, w - 1);
    var y0 = Math.Clamp((int)Math.Round(box.Y * h), 0, h - 1);
    var x1 = Math.Clamp((int)Math.Round((box.X + box.Width) * w), x0 + 1, w);
    var y1 = Math.Clamp((int)Math.Round((box.Y + box.Height) * h), y0 + 1, h);

    image.ProcessPixelRows(accessor => {
      for (var y = y0; y < y1; y++) {
        var row = accessor.GetRowSpan(y);
        for (var x = x0; x < x1; x++) {
          var px = row[x];
          var maxGB = Math.Max(px.G, px.B);
          if (px.R <= 100 || px.R <= maxGB * 1.3)
            continue;
          // Replace red with luminance-equivalent grey, then darken slightly
          // so we don't end up with bright white pupils.
          var lum = (byte)(0.299 * px.G + 0.587 * px.G + 0.114 * px.B * 0.5);
          var grey = lum < 8 ? (byte)8 : lum;
          row[x] = new Rgba32(grey, grey, grey, px.A);
        }
      }
    });
  }
}
