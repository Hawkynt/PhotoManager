namespace PhotoManager.Core.Regions;

/// <summary>
/// Broad categories of tagged regions. Drives UI color coding (one color
/// per category) and influences how a label is interpreted (a "Person"
/// label is a person's name; an "Animal" label is "cat", "dog", etc.).
/// Keep the set small — users shouldn't have to pick from dozens.
/// </summary>
public enum RegionCategory {
  Person,
  Animal,
  Item,
  Place,
  Other
}

public static class RegionCategoryExtensions {
  /// <summary>
  /// Stable hex color for each category, intended for UI overlays and
  /// list swatches. Colors are chosen to be distinguishable under
  /// both light and dark backgrounds.
  /// </summary>
  public static string ToHexColor(this RegionCategory category) => category switch {
    RegionCategory.Person => "#2E86DE",   // blue
    RegionCategory.Animal => "#10AC84",   // green
    RegionCategory.Item   => "#EE5A24",   // orange
    RegionCategory.Place  => "#8854D0",   // purple
    RegionCategory.Other  => "#808080",   // gray
    _ => "#808080"
  };
}
