using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Geocoding;

public interface IReverseGeocoder {
  Task<GeocodingResult?> ResolveAsync(GpsCoordinate coordinate, CancellationToken cancellationToken = default);
}
