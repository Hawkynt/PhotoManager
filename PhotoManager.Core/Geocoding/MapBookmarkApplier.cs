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

  private static Optional<string?> OptionalText(string? value) =>
    string.IsNullOrWhiteSpace(value) ? default : Optional<string?>.Set(value);
}
