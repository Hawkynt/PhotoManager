using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Hawkynt.PhotoManager.UI.Models;

/// One row of the HDR-merge file list. ExposureSeconds is editable so the
/// user can supply a value when EXIF is missing; ExposureText / EvOffsetText
/// are derived display strings that follow the seconds setter.
public sealed class HdrBracketRow : INotifyPropertyChanged {
  private string _fileName = string.Empty;
  private double _exposureSeconds;
  private string _exposureText = string.Empty;
  private string _evOffsetText = string.Empty;

  public required FileInfo FileInfo { get; init; }

  public double ExposureSeconds {
    get => this._exposureSeconds;
    set {
      if (Math.Abs(this._exposureSeconds - value) < 1e-9)
        return;
      this._exposureSeconds = value;
      this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExposureSeconds)));
      this.ExposureText = FormatExposure(value);
    }
  }

  public string FileName {
    get => this._fileName;
    set => this.SetProperty(ref this._fileName, value);
  }

  public string ExposureText {
    get => this._exposureText;
    set => this.SetProperty(ref this._exposureText, value);
  }

  public string EvOffsetText {
    get => this._evOffsetText;
    set => this.SetProperty(ref this._evOffsetText, value);
  }

  public static string FormatExposure(double seconds) {
    if (seconds <= 0)
      return "(set manually)";
    if (seconds >= 1)
      return string.Create(CultureInfo.InvariantCulture, $"{seconds:0.##}s");
    return string.Create(CultureInfo.InvariantCulture, $"1/{1.0 / seconds:0}s");
  }

  public static string FormatEvOffset(double seconds, double medianSeconds) {
    if (seconds <= 0 || medianSeconds <= 0)
      return string.Empty;
    var ev = Math.Log2(seconds / medianSeconds);
    var sign = ev > 0 ? "+" : string.Empty;
    return string.Create(CultureInfo.InvariantCulture, $"{sign}{ev:0.0} EV");
  }

  public event PropertyChangedEventHandler? PropertyChanged;

  private void SetProperty<T>(ref T storage, T value, [CallerMemberName] string? name = null) {
    if (EqualityComparer<T>.Default.Equals(storage, value))
      return;
    storage = value;
    this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
  }
}
