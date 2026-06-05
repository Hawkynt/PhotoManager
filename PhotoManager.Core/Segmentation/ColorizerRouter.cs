using Hawkynt.PhotoManager.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Hawkynt.PhotoManager.Core.Segmentation;

/// <summary>
/// Picks the right colorize engine for a given model filename. The two
/// architectures we support today have incompatible I/O contracts:
///
/// * <see cref="OnnxColorizer"/> — DeOldify (RGB-in, RGB-out, ImageNet
///   normalised). Mild output but bundled as the default since 2024.
/// * <see cref="OnnxColorizerDDColor"/> — DDColor (Lab-grey-RGB-in,
///   ab-out). State-of-the-art (ECCV 2023); preserves source luminance
///   exactly because the network never touches the L channel.
///
/// The colorize picker UI doesn't change — users see one dropdown with
/// every registered colorizer. This router dispatches to the correct
/// engine purely on filename prefix so the develop and restoration
/// pipelines don't need parallel switch statements.
/// </summary>
public static class ColorizerRouter {
  /// <summary>
  /// Run whichever colorizer matches <paramref name="modelFileName"/>.
  /// Returns null if the corresponding model isn't installed (caller
  /// then leaves the source unchanged, no error). Strength linearly
  /// blends source ↔ colorised in both engines.
  /// </summary>
  public static Image<Rgba32>? Colorize(Image<Rgba32> source, string? modelFileName, double strength, double chromaBoost = 1.6, CancellationToken ct = default) {
    // The UI passes null when the user picked the dropdown's default
    // (first) entry. That entry is the registry's first colorizer —
    // currently DDColor paper-tiny — NOT DeOldify Artistic. The previous
    // implementation hard-coded the OnnxColorizer (DeOldify) default
    // here, so every "default-pick" UI invocation silently ran DeOldify
    // even when the user explicitly chose a DDColor variant; the
    // dispatch then routed to DeOldify regardless of intent because the
    // filename prefix didn't say "ddcolor".
    var fileName = string.IsNullOrWhiteSpace(modelFileName)
      ? (ModelRegistry.Colorizers.Count > 0
          ? ModelRegistry.Colorizers[0].FileName
          : OnnxColorizer.DefaultModelFileName)
      : modelFileName!;
    var modelFile = AppDataPaths.ModelFile(fileName);

    // Force the source to R=G=B per pixel (Rec.709 luminance) BEFORE
    // dispatching to the model. Upstream pipeline stages (LaMa
    // inpaint, FBCNN, despeckle's per-channel filter) can introduce
    // sub-byte R/G/B differences that confuse the colorizer's input
    // distribution — DDColor paper-tiny in particular collapsed to
    // near-zero chroma when fed slightly-coloured "B&W" input. This
    // grayscale step makes the colorizer's input deterministic
    // regardless of what came before.
    using var graySource = ToLuminance(source);

    if (IsDDColor(fileName)) {
      using var dd = new OnnxColorizerDDColor(modelFile);
      return dd.IsAvailable ? dd.Colorize(graySource, strength, chromaBoost, ct) : null;
    }

    // chromaBoost is a Lab-only concept; DeOldify produces RGB directly so it ignores it.
    using var de = new OnnxColorizer(modelFile);
    return de.IsAvailable ? de.Colorize(graySource, strength, ct) : null;
  }

  /// <summary>Return a freshly allocated copy of <paramref name="source"/>
  /// with every pixel set to (Y, Y, Y, A) where Y is Rec.709 luminance.
  /// Decouples the colorizer's input from any subtle R/G/B differences
  /// introduced by upstream pipeline stages, so it predicts the same
  /// chroma whether or not LaMa / despeckle / etc. ran beforehand.
  /// </summary>
  internal static Image<Rgba32> ToLuminance(Image<Rgba32> source) {
    var output = source.Clone();
    output.ProcessPixelRows(a => {
      for (var y = 0; y < a.Height; y++) {
        var row = a.GetRowSpan(y);
        for (var x = 0; x < row.Length; x++) {
          var p = row[x];
          var luma = (byte)Math.Clamp(
            (int)Math.Round(0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B),
            0, 255);
          row[x] = new Rgba32(luma, luma, luma, p.A);
        }
      }
    });
    return output;
  }

  /// <summary>True iff <paramref name="fileName"/> looks like a DDColor model. Filename prefix is the dispatch key (no ONNX-graph inspection needed).</summary>
  public static bool IsDDColor(string? fileName)
    => !string.IsNullOrWhiteSpace(fileName)
       && fileName!.StartsWith("ddcolor", StringComparison.OrdinalIgnoreCase);
}
