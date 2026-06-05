using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Geocoding;

/// <summary>
/// A user-curated map favourite — typically a place they revisit (e.g. a
/// venue, a landmark) that they want to geotag photos with in one click.
/// Holds the GPS pin plus an optional radius hint and the place fields
/// (city / state / country / etc.) so applying the bookmark also writes
/// the location text into the photo's XMP metadata.
///
/// Identity is by <see cref="Id"/> (a stable Guid created when the bookmark
/// is first added) so the user can rename without losing the entry.
/// </summary>
public sealed record MapBookmark {
  public string Id { get; init; } = Guid.NewGuid().ToString("N");
  public string Name { get; init; } = string.Empty;
  public double Latitude { get; init; }
  public double Longitude { get; init; }

  /// <summary>Optional informational radius in metres — used today only for display.</summary>
  public double? RadiusMeters { get; init; }

  public string? Location { get; init; }
  public string? City { get; init; }
  public string? State { get; init; }
  public string? Country { get; init; }
  public string? CountryCode { get; init; }

  public GpsCoordinate ToGps() => new(this.Latitude, this.Longitude);
}
