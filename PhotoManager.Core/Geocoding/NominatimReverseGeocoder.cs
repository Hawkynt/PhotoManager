using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoManager.Core.Metadata;

namespace PhotoManager.Core.Geocoding;

/// <summary>
/// Reverse geocoder backed by OpenStreetMap's Nominatim service
/// (<c>https://nominatim.openstreetmap.org/reverse</c>). No API key,
/// free-to-use, subject to a 1-request-per-second rate limit that we
/// enforce locally. Results are cached in memory by rounded lat/lon so
/// moving around a single location doesn't re-query.
///
/// Per Nominatim's usage policy we identify ourselves with a User-Agent
/// that names the application — callers can override it if PhotoManager
/// is being embedded somewhere else.
/// </summary>
public sealed class NominatimReverseGeocoder : IReverseGeocoder, IDisposable {
  private const string DefaultEndpoint = "https://nominatim.openstreetmap.org/reverse";
  private const string DefaultUserAgent = "PhotoManager/1.0 (https://github.com/Hawkynt/PhotoManager)";
  private const int CoordinateRoundDigits = 4;  // ~11 m resolution at the equator — close enough to dedupe nearby clicks
  private const int CacheCapacity = 256;
  private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(1100);

  private readonly HttpClient _httpClient;
  private readonly string _endpoint;
  private readonly bool _ownsHttpClient;
  private readonly Dictionary<(double, double), GeocodingResult?> _cache = new();
  private readonly Queue<(double, double)> _cacheOrder = new();
  private readonly SemaphoreSlim _requestGate = new(1, 1);
  private readonly object _cacheLock = new();

  private DateTime _lastRequestUtc = DateTime.MinValue;

  public NominatimReverseGeocoder(
    HttpClient? httpClient = null,
    string endpoint = DefaultEndpoint,
    string userAgent = DefaultUserAgent
  ) {
    this._ownsHttpClient = httpClient == null;
    this._httpClient = httpClient ?? new HttpClient();
    this._endpoint = endpoint;

    if (!this._httpClient.DefaultRequestHeaders.UserAgent.Any())
      this._httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
  }

  public async Task<GeocodingResult?> ResolveAsync(GpsCoordinate coordinate, CancellationToken cancellationToken = default) {
    if (!coordinate.IsValid)
      return null;

    var key = RoundKey(coordinate);
    if (this.TryGetCached(key, out var cached))
      return cached;

    GeocodingResult? result = null;
    try {
      result = await this.QueryNominatimAsync(coordinate, cancellationToken);
    } catch (OperationCanceledException) {
      throw;
    } catch {
      // Network/parse failure — store null in the cache so we don't hammer
      // the server on repeated failures for the same coordinate.
    }

    this.PutCached(key, result);
    return result;
  }

  private async Task<GeocodingResult?> QueryNominatimAsync(GpsCoordinate coordinate, CancellationToken cancellationToken) {
    await this._requestGate.WaitAsync(cancellationToken);
    try {
      var waitFor = MinRequestInterval - (DateTime.UtcNow - this._lastRequestUtc);
      if (waitFor > TimeSpan.Zero)
        await Task.Delay(waitFor, cancellationToken);

      var inv = CultureInfo.InvariantCulture;
      var url = $"{this._endpoint}?format=jsonv2&lat={coordinate.Latitude.ToString("0.######", inv)}&lon={coordinate.Longitude.ToString("0.######", inv)}&zoom=18&accept-language=en";

      using var response = await this._httpClient.GetAsync(url, cancellationToken);
      this._lastRequestUtc = DateTime.UtcNow;

      if (!response.IsSuccessStatusCode)
        return null;

      var payload = await response.Content.ReadFromJsonAsync<NominatimResponse>(cancellationToken: cancellationToken);
      return payload == null ? null : Map(payload);
    } finally {
      this._requestGate.Release();
    }
  }

  internal static GeocodingResult Map(NominatimResponse r) {
    var a = r.Address;
    if (a == null)
      return new GeocodingResult(r.DisplayName, null, null, null, null);

    // Nominatim's address keys vary by place type; fall back through the
    // plausible options so cities / towns / villages all surface.
    var street = FirstNonEmpty(a.Road, a.Pedestrian, a.Path, a.Neighbourhood, a.Suburb);
    // Append the house number when available so the Location field reads
    // "Hauptstraße 42" instead of just "Hauptstraße". Order follows the
    // country's convention — US/UK/CA/AU/NZ/IE put the number first
    // ("123 Main Street"); everywhere else uses street-then-number. For
    // unknown countries we default to street-first since that's the more
    // common pattern worldwide.
    var location = ComposeStreetWithNumber(street, a.HouseNumber, a.CountryCode);
    var city = FirstNonEmpty(a.City, a.Town, a.Village, a.Hamlet, a.Municipality);
    var state = FirstNonEmpty(a.State, a.Region, a.County);
    var country = a.Country;
    var countryCode = string.IsNullOrEmpty(a.CountryCode) ? null : a.CountryCode.ToUpperInvariant();

    return new GeocodingResult(location, city, state, country, countryCode);
  }

  private static string? ComposeStreetWithNumber(string? street, string? houseNumber, string? countryCode) {
    if (string.IsNullOrWhiteSpace(street))
      return null;
    if (string.IsNullOrWhiteSpace(houseNumber))
      return street;

    var isNumberFirst = countryCode?.ToUpperInvariant() is "US" or "GB" or "CA" or "AU" or "NZ" or "IE";
    return isNumberFirst ? $"{houseNumber} {street}" : $"{street} {houseNumber}";
  }

  private static string? FirstNonEmpty(params string?[] candidates) {
    foreach (var c in candidates) {
      if (!string.IsNullOrWhiteSpace(c))
        return c;
    }
    return null;
  }

  private static (double, double) RoundKey(GpsCoordinate c) => (
    Math.Round(c.Latitude, CoordinateRoundDigits),
    Math.Round(c.Longitude, CoordinateRoundDigits)
  );

  private bool TryGetCached((double, double) key, out GeocodingResult? value) {
    lock (this._cacheLock) {
      return this._cache.TryGetValue(key, out value);
    }
  }

  private void PutCached((double, double) key, GeocodingResult? value) {
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

  /// <summary>
  /// JSON mapping for Nominatim's jsonv2 response. Only the fields we
  /// actually use are bound — others are ignored.
  /// </summary>
  internal sealed class NominatimResponse {
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("address")] public NominatimAddress? Address { get; set; }
  }

  internal sealed class NominatimAddress {
    [JsonPropertyName("house_number")] public string? HouseNumber { get; set; }
    [JsonPropertyName("road")] public string? Road { get; set; }
    [JsonPropertyName("pedestrian")] public string? Pedestrian { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("neighbourhood")] public string? Neighbourhood { get; set; }
    [JsonPropertyName("suburb")] public string? Suburb { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("town")] public string? Town { get; set; }
    [JsonPropertyName("village")] public string? Village { get; set; }
    [JsonPropertyName("hamlet")] public string? Hamlet { get; set; }
    [JsonPropertyName("municipality")] public string? Municipality { get; set; }
    [JsonPropertyName("county")] public string? County { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("region")] public string? Region { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
  }
}
