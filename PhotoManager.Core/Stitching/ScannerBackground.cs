namespace Hawkynt.PhotoManager.Core.Stitching;

/// <summary>
/// Hint for the segmenter about what colour the gaps between pieces / the
/// scanner bed are. A black background is the easy case (white-paper pieces
/// on a flatbed with the lid open); white bed needs an inverted threshold.
/// <see cref="Auto"/> picks based on the median brightness of the four
/// image corners — works for clean scans but can be wrong if a piece
/// touches a corner.
/// </summary>
public enum ScannerBackground {
  Auto,
  Black,
  White
}
