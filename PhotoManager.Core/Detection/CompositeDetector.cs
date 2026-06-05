namespace Hawkynt.PhotoManager.Core.Detection;

/// <summary>
/// Runs a primary detector and, if it returns no labels, tries a fallback.
/// Wired so the app prefers the real ONNX-backed object detector when its
/// model is present, but quietly uses the path-derived heuristic otherwise —
/// so the Auto-Detect button is useful out of the box without forcing the
/// user to download model weights.
/// </summary>
public sealed class CompositeDetector : IDetector {
  private readonly IDetector _primary;
  private readonly IDetector _fallback;

  public CompositeDetector(IDetector primary, IDetector fallback) {
    this._primary = primary ?? throw new ArgumentNullException(nameof(primary));
    this._fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
  }

  public async Task<DetectionResult> DetectAsync(FileInfo imageFile, CancellationToken cancellationToken = default) {
    var primary = await this._primary.DetectAsync(imageFile, cancellationToken);
    if (primary.Labels.Count > 0)
      return primary;

    return await this._fallback.DetectAsync(imageFile, cancellationToken);
  }
}
