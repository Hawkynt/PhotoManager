using PhotoManager.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhotoManager.Core.Segmentation;

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

    if (IsDDColor(fileName)) {
      using var dd = new OnnxColorizerDDColor(modelFile);
      return dd.IsAvailable ? dd.Colorize(source, strength, chromaBoost, ct) : null;
    }

    // chromaBoost is a Lab-only concept; DeOldify produces RGB directly so it ignores it.
    using var de = new OnnxColorizer(modelFile);
    return de.IsAvailable ? de.Colorize(source, strength, ct) : null;
  }

  /// <summary>True iff <paramref name="fileName"/> looks like a DDColor model. Filename prefix is the dispatch key (no ONNX-graph inspection needed).</summary>
  public static bool IsDDColor(string? fileName)
    => !string.IsNullOrWhiteSpace(fileName)
       && fileName!.StartsWith("ddcolor", StringComparison.OrdinalIgnoreCase);
}
