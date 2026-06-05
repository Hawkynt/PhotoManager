namespace Hawkynt.PhotoManager.Core.Video;

/// <summary>
/// Tunables for <see cref="VideoFrameExtractor"/>. Defaults match the
/// "panorama sweep" workflow: 2 fps gives plenty of overlap for the
/// stitcher without exploding disk usage.
/// </summary>
public sealed record VideoExtractOptions {
  public double Fps { get; init; } = 2.0;
  public int? MaxLongEdge { get; init; }
  public int JpegQuality { get; init; } = 2;
  public TimeSpan? StartTime { get; init; }
  public TimeSpan? EndTime { get; init; }
}
