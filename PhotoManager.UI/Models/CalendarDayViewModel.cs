using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace PhotoManager.UI.Models;

/// <summary>
/// One cell in the calendar view's month grid. Holds the date the cell
/// represents, every photo taken on that day (already collected by the
/// caller), and a lazy thumbnail of the first photo.
/// </summary>
public sealed class CalendarDayViewModel : INotifyPropertyChanged {
  private Bitmap? _thumbnail;

  public CalendarDayViewModel(DateTime date, IReadOnlyList<FileItemModel> photos, bool isInMonth) {
    this.Date = date;
    this.Photos = photos;
    this.IsInMonth = isInMonth;
  }

  public DateTime Date { get; }
  public IReadOnlyList<FileItemModel> Photos { get; }
  public bool IsInMonth { get; }

  public string DayNumber => this.Date.Day.ToString();
  public int Count => this.Photos.Count;
  public bool HasPhotos => this.Count > 0;
  public string CountText => this.Count == 0
    ? string.Empty
    : (this.Count == 1 ? "1 photo" : $"{this.Count} photos");

  public Bitmap? Thumbnail {
    get => this._thumbnail;
    set {
      if (ReferenceEquals(this._thumbnail, value))
        return;
      this._thumbnail = value;
      this.OnPropertyChanged();
    }
  }

  public event PropertyChangedEventHandler? PropertyChanged;
  private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
