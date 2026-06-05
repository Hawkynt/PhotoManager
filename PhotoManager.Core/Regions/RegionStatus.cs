namespace Hawkynt.PhotoManager.Core.Regions;

/// <summary>
/// Lifecycle state of a tagged region.
///   - <see cref="Proposed"/>: a detector (YOLO, face detector, etc.) suggested
///     this region but the user hasn't reviewed it yet. Proposed regions are
///     saved to the sidecar so the review queue survives app restart.
///   - <see cref="Accepted"/>: the user confirmed the region. Its label goes
///     into keywords for search.
/// Discard deletes the region from the sidecar rather than marking it Rejected,
/// so the history of rejections isn't carried around forever.
/// </summary>
public enum RegionStatus {
  Proposed,
  Accepted
}
