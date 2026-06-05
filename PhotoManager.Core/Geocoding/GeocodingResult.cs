namespace Hawkynt.PhotoManager.Core.Geocoding;

/// <summary>
/// A reverse-geocoded place for a GPS coordinate. Fields map roughly to the
/// XMP location schema (<c>Iptc4xmpCore:Location</c> + <c>photoshop:City/State/Country</c>)
/// so the result can flow straight into a <see cref="Metadata.MetadataEdit"/>.
/// Any field may be null — geocoders sometimes only resolve a subset.
/// </summary>
public sealed record GeocodingResult(
  string? Location,
  string? City,
  string? State,
  string? Country,
  string? CountryCode
) {
  public bool HasAny =>
    !string.IsNullOrEmpty(this.Location) ||
    !string.IsNullOrEmpty(this.City) ||
    !string.IsNullOrEmpty(this.State) ||
    !string.IsNullOrEmpty(this.Country) ||
    !string.IsNullOrEmpty(this.CountryCode);
}
