using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Geocoding;

/// <summary>
/// Resolves an elevation (meters above sea level) for a given GPS coordinate.
/// Backed by an online DEM (e.g. <see cref="OpenTopoElevationService"/>) but
/// the interface is deliberately abstract so callers can plug a cached, an
/// offline, or a test service transparently.
/// </summary>
public interface IElevationService {
  Task<double?> GetAltitudeMetersAsync(GpsCoordinate coordinate, CancellationToken cancellationToken = default);
}
