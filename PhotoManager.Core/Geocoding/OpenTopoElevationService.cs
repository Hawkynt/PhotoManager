using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Geocoding;

/// <summary>
/// Elevation lookup backed by the free OpenTopoData service
/// (<c>https://api.opentopodata.org/v1/&lt;dataset&gt;</c>). No API key, rate-limited
/// to 1 request/second — we honor that locally. Results cached in memory
/// by lat/lon rounded to 5 decimals (~1 m resolution).
///
/// Default dataset is <c>srtm30m</c> (NASA SRTM 1-arcsec, global except polar);
/// callers can switch via <see cref="Dataset"/> if they need <c>eu-dem25m</c>
/// or another higher-resolution regional DEM.
/// </summary>
public sealed class OpenTopoElevationService : IElevationService, IDisposable {
  private const string DefaultEndpointBase = "https://api.opentopodata.org/v1/";
  private const string DefaultDataset = "srtm30m";
  private const string DefaultUserAgent = "PhotoManager/1.0 (https://github.com/Hawkynt/PhotoManager)";
  private const int CoordinateRoundDigits = 5;
  private const int CacheCapacity = 512;
  private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(1100);

  private readonly HttpClient _httpClient;
  private readonly string _endpointBase;
  private readonly bool _ownsHttpClient;
  private readonly Dictionary<(double, double), double?> _cache = new();
  private readonly Queue<(double, double)> _cacheOrder = new();
  private readonly SemaphoreSlim _requestGate = new(1, 1);
  private readonly object _cacheLock = new();

  private DateTime _lastRequestUtc = DateTime.MinValue;

  public OpenTopoElevationService(
    HttpClient? httpClient = null,
    string endpointBase = DefaultEndpointBase,
    string userAgent = DefaultUserAgent
  ) {
    this._ownsHttpClient = httpClient == null;
    this._httpClient = httpClient ?? new HttpClient();
    this._endpointBase = endpointBase.EndsWith('/') ? endpointBase : endpointBase + "/";

    if (!this._httpClient.DefaultRequestHeaders.UserAgent.Any())
      this._httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
  }

  public string Dataset { get; init; } = DefaultDataset;

  public async Task<double?> GetAltitudeMetersAsync(GpsCoordinate coordinate, CancellationToken cancellationToken = default) {
    if (!coordinate.IsValid)
      return null;

    var key = RoundKey(coordinate);
    if (this.TryGetCached(key, out var cached))
      return cached;

    double? elevation = null;
    try {
      elevation = await this.QueryAsync(coordinate, cancellationToken);
    } catch (OperationCanceledException) {
      throw;
    } catch {
      // Swallow and cache null — we don't want repeated hammering on transient failure.
    }

    this.PutCached(key, elevation);
    return elevation;
  }

  private async Task<double?> QueryAsync(GpsCoordinate coordinate, CancellationToken cancellationToken) {
    await this._requestGate.WaitAsync(cancellationToken);
    try {
      var waitFor = MinRequestInterval - (DateTime.UtcNow - this._lastRequestUtc);
      if (waitFor > TimeSpan.Zero)
        await Task.Delay(waitFor, cancellationToken);

      var inv = CultureInfo.InvariantCulture;
      var url = $"{this._endpointBase}{this.Dataset}?locations={coordinate.Latitude.ToString("0.######", inv)},{coordinate.Longitude.ToString("0.######", inv)}";

      using var response = await this._httpClient.GetAsync(url, cancellationToken);
      this._lastRequestUtc = DateTime.UtcNow;

      if (!response.IsSuccessStatusCode)
        return null;

      var payload = await response.Content.ReadFromJsonAsync<Response>(cancellationToken: cancellationToken);
      if (payload?.Results is not { Count: > 0 } results)
        return null;
      return results[0].Elevation;
    } finally {
      this._requestGate.Release();
    }
  }

  private static (double, double) RoundKey(GpsCoordinate c) => (
    Math.Round(c.Latitude, CoordinateRoundDigits),
    Math.Round(c.Longitude, CoordinateRoundDigits)
  );

  private bool TryGetCached((double, double) key, out double? value) {
    lock (this._cacheLock)
      return this._cache.TryGetValue(key, out value);
  }

  private void PutCached((double, double) key, double? value) {
    lock (this._cacheLock) {
      if (this._cache.ContainsKey(key)) {
        this._cache[key] = value;
        return;
      }
      this._cache[key] = value;
      this._cacheOrder.Enqueue(key);
      while (this._cacheOrder.Count > CacheCapacity) {
        var oldest = this._cacheOrder.Dequeue();
        this._cache.Remove(oldest);
      }
    }
  }

  public void Dispose() {
    if (this._ownsHttpClient)
      this._httpClient.Dispose();
    this._requestGate.Dispose();
  }

  internal sealed class Response {
    [JsonPropertyName("results")] public List<Sample>? Results { get; set; }
  }

  internal sealed class Sample {
    [JsonPropertyName("elevation")] public double? Elevation { get; set; }
  }
}
