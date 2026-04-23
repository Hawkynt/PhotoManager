using PhotoManager.Core.Metadata;

namespace PhotoManager.Core.Geocoding;

public interface IReverseGeocoder {
  Task<GeocodingResult?> ResolveAsync(GpsCoordinate coordinate, CancellationToken cancellationToken = default);
}
