namespace PhotoManager.Core.Detection;

/// <summary>
/// The set of detections returned from one run of an <see cref="IDetector"/>
/// against a single image.
/// </summary>
public sealed record DetectionResult(IReadOnlyList<DetectionLabel> Labels) {
  public static readonly DetectionResult Empty = new(Array.Empty<DetectionLabel>());

  public IEnumerable<string> DistinctLabelNames()
    => this.Labels
      .Select(l => l.Name)
      .Where(n => !string.IsNullOrWhiteSpace(n))
      .Distinct(StringComparer.OrdinalIgnoreCase);
}
