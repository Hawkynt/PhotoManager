namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Pure layout helper for the Compare Grid N-pane view. Given a photo count
/// (2-9), returns the ideal (columns, rows) for a uniform grid arrangement.
/// </summary>
public static class CompareGridLayout {
  /// <summary>
  /// Compute the grid dimensions for <paramref name="photoCount"/> photos.
  /// <list type="bullet">
  ///   <item>2 photos: 2 columns, 1 row</item>
  ///   <item>3-4 photos: 2 columns, 2 rows</item>
  ///   <item>5-6 photos: 2 columns, 3 rows</item>
  ///   <item>7-9 photos: 3 columns, 3 rows</item>
  /// </list>
  /// </summary>
  /// <returns>A tuple of (columns, rows).</returns>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when <paramref name="photoCount"/> is less than 2 or greater than 9.
  /// </exception>
  public static (int Columns, int Rows) ComputeGridSize(int photoCount) {
    if (photoCount < 2 || photoCount > 9)
      throw new ArgumentOutOfRangeException(nameof(photoCount), photoCount, "Photo count must be between 2 and 9.");

    return photoCount switch {
      2     => (2, 1),
      3 or 4 => (2, 2),
      5 or 6 => (2, 3),
      _     => (3, 3)  // 7, 8, 9
    };
  }
}
