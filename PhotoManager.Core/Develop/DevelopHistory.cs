namespace PhotoManager.Core.Develop;

/// <summary>
/// One entry on the develop edit-history stack. Each Save snapshots the
/// in-flight <see cref="DevelopSettings"/> here before overwriting the
/// current settings, so users can roll back to any prior state.
/// </summary>
public sealed record DevelopSnapshot(DateTime TimestampUtc, string? Label, DevelopSettings Settings);

/// <summary>
/// Pure helpers for the develop history stack — kept free of file I/O so
/// the model layer stays trivially testable. The stack is stored newest-first;
/// callers persist the list as XMP via <see cref="DevelopMetadataStore"/>.
/// </summary>
public static class DevelopHistory {
  public const int DefaultMaxDepth = 50;

  /// <summary>
  /// Push <paramref name="settings"/> onto the front of the stack with a UTC
  /// timestamp + optional label, capping the result to <paramref name="maxDepth"/>
  /// (oldest entries fall off the end). The input list is not mutated.
  /// </summary>
  public static IReadOnlyList<DevelopSnapshot> Push(
      IReadOnlyList<DevelopSnapshot> existing,
      DevelopSettings settings,
      string? label = null,
      int maxDepth = DefaultMaxDepth) {
    ArgumentNullException.ThrowIfNull(existing);
    ArgumentNullException.ThrowIfNull(settings);
    if (maxDepth < 1)
      maxDepth = 1;

    var entry = new DevelopSnapshot(DateTime.UtcNow, label, settings);
    var combined = new List<DevelopSnapshot>(Math.Min(existing.Count + 1, maxDepth)) { entry };
    for (var i = 0; i < existing.Count && combined.Count < maxDepth; i++)
      combined.Add(existing[i]);
    return combined;
  }

  /// <summary>
  /// Return every snapshot in the stack, newest first. Returns an empty
  /// list when the history is empty. The returned list is a fresh copy.
  /// </summary>
  public static IReadOnlyList<DevelopSnapshot> GetAll(IReadOnlyList<DevelopSnapshot> history) {
    ArgumentNullException.ThrowIfNull(history);
    return history.ToList();
  }

  /// <summary>
  /// Roll back to the snapshot at <paramref name="index"/> (0 = most
  /// recent). Returns the <see cref="DevelopSettings"/> stored in that
  /// entry. Throws <see cref="ArgumentOutOfRangeException"/> if the
  /// index is out of bounds.
  /// </summary>
  public static DevelopSettings RollbackTo(IReadOnlyList<DevelopSnapshot> history, int index) {
    ArgumentNullException.ThrowIfNull(history);
    if (index < 0 || index >= history.Count)
      throw new ArgumentOutOfRangeException(nameof(index), index,
        $"Index must be 0..{history.Count - 1} but was {index}.");
    return history[index].Settings;
  }
}
