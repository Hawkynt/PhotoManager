using PhotoManager.Core.Metadata;

namespace PhotoManager.Core.Geocoding;

/// <summary>
/// Pure-logic builder that converts a <see cref="MapBookmark"/> into the
/// <see cref="MetadataEdit"/> patch we hand the writer. Lives separately
/// from the window so it's trivial to unit-test the field projection
/// without spinning up Avalonia.
///
/// Place fields are only emitted when the bookmark actually carries them —
/// blank strings stay default so existing values on the photo aren't
/// stomped with empty patches. The GPS pin always wins (the whole point of
/// applying a bookmark is to set the coordinates).
/// </summary>
public static class MapBookmarkApplier {
  public static MetadataEdit BuildEdit(MapBookmark bookmark) {
    ArgumentNullException.ThrowIfNull(bookmark);

    return new MetadataEdit {
      Gps         = Optional<GpsCoordinate?>.Set(bookmark.ToGps()),
      Location    = OptionalText(bookmark.Location),
      City        = OptionalText(bookmark.City),
      State       = OptionalText(bookmark.State),
      Country     = OptionalText(bookmark.Country),
      CountryCode = OptionalText(bookmark.CountryCode)
    };
  }

  /// Variant used by the geofence batch flow: when <paramref name="onlyFillEmpty"/>
  /// is true, place fields the photo already has stay untouched so a hand-typed
  /// "Berlin" isn't overwritten by a bookmark's "Berlin, Germany". GPS still
  /// always wins — the whole point of applying a fence.
  public static MetadataEdit BuildEdit(MapBookmark bookmark, FullMetadata existing, bool onlyFillEmpty) {
    ArgumentNullException.ThrowIfNull(bookmark);
    ArgumentNullException.ThrowIfNull(existing);

    return new MetadataEdit {
      Gps         = Optional<GpsCoordinate?>.Set(bookmark.ToGps()),
      Location    = ConditionalText(bookmark.Location,    existing.Location,    onlyFillEmpty),
      City        = ConditionalText(bookmark.City,        existing.City,        onlyFillEmpty),
      State       = ConditionalText(bookmark.State,       existing.State,       onlyFillEmpty),
      Country     = ConditionalText(bookmark.Country,     existing.Country,     onlyFillEmpty),
      CountryCode = ConditionalText(bookmark.CountryCode, existing.CountryCode, onlyFillEmpty)
    };
  }

  private static Optional<string?> OptionalText(string? value) =>
    string.IsNullOrWhiteSpace(value) ? default : Optional<string?>.Set(value);

  private static Optional<string?> ConditionalText(string? bookmarkValue, string? existingValue, bool onlyFillEmpty) {
    if (string.IsNullOrWhiteSpace(bookmarkValue))
      return default;
    if (onlyFillEmpty && !string.IsNullOrWhiteSpace(existingValue))
      return default;
    return Optional<string?>.Set(bookmarkValue);
  }
}
