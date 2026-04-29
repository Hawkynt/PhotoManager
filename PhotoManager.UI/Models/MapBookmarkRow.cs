using System.ComponentModel;
using System.Runtime.CompilerServices;
using PhotoManager.Core.Geocoding;

namespace PhotoManager.UI.Models;

/// <summary>
/// Editable row view-model for the map bookmarks dialog. Wraps a
/// <see cref="MapBookmark"/> so the DataGrid can two-way bind against it
/// without us re-creating the underlying record on every keystroke.
/// </summary>
public sealed class MapBookmarkRow : INotifyPropertyChanged {
  private string _id = Guid.NewGuid().ToString("N");
  private string _name = string.Empty;
  private double _latitude;
  private double _longitude;
  private double? _radiusMeters;
  private string? _location;
  private string? _city;
  private string? _state;
  private string? _country;
  private string? _countryCode;

  public string Id {
    get => this._id;
    set => this.SetProperty(ref this._id, value);
  }

  public string Name {
    get => this._name;
    set => this.SetProperty(ref this._name, value);
  }

  public double Latitude {
    get => this._latitude;
    set => this.SetProperty(ref this._latitude, value);
  }

  public double Longitude {
    get => this._longitude;
    set => this.SetProperty(ref this._longitude, value);
  }

  public double? RadiusMeters {
    get => this._radiusMeters;
    set => this.SetProperty(ref this._radiusMeters, value);
  }

  public string? Location {
    get => this._location;
    set => this.SetProperty(ref this._location, value);
  }

  public string? City {
    get => this._city;
    set => this.SetProperty(ref this._city, value);
  }

  public string? State {
    get => this._state;
    set => this.SetProperty(ref this._state, value);
  }

  public string? Country {
    get => this._country;
    set => this.SetProperty(ref this._country, value);
  }

  public string? CountryCode {
    get => this._countryCode;
    set => this.SetProperty(ref this._countryCode, value);
  }

  public static MapBookmarkRow FromBookmark(MapBookmark b) => new() {
    Id           = b.Id,
    Name         = b.Name,
    Latitude     = b.Latitude,
    Longitude    = b.Longitude,
    RadiusMeters = b.RadiusMeters,
    Location     = b.Location,
    City         = b.City,
    State        = b.State,
    Country      = b.Country,
    CountryCode  = b.CountryCode
  };

  public MapBookmark ToBookmark() => new() {
    Id           = string.IsNullOrWhiteSpace(this.Id) ? Guid.NewGuid().ToString("N") : this.Id,
    Name         = (this.Name ?? string.Empty).Trim(),
    Latitude     = this.Latitude,
    Longitude    = this.Longitude,
    RadiusMeters = this.RadiusMeters,
    Location     = NullIfBlank(this.Location),
    City         = NullIfBlank(this.City),
    State        = NullIfBlank(this.State),
    Country      = NullIfBlank(this.Country),
    CountryCode  = NullIfBlank(this.CountryCode)
  };

  private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

  public event PropertyChangedEventHandler? PropertyChanged;

  private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null) {
    if (EqualityComparer<T>.Default.Equals(field, value))
      return;
    field = value;
    this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
  }
}
