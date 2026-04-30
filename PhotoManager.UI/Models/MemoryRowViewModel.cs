using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using PhotoManager.Core.Metadata;

namespace PhotoManager.UI.Models;

/// <summary>
/// One tile in the Memories window grid. Pairs a file + its capture date /
/// location string with a lazily-loaded thumbnail. Thumbnails come from
/// <c>RegionThumbnailExtractor.CropAsync</c> with a full-frame box so the
/// existing RAW/JPEG decode path is reused for free.
/// </summary>
public sealed class MemoryRowViewModel : INotifyPropertyChanged {
  private Bitmap? _thumbnail;

  public MemoryRowViewModel(FileInfo file, FullMetadata metadata) {
    this.File = file;
    this.Metadata = metadata;
  }

  public FileInfo File { get; }
  public FullMetadata Metadata { get; }
  public string FileName => this.File.Name;

  /// <summary>Capture date of the photo, formatted "yyyy-MM-dd HH:mm" — blank if unknown.</summary>
  public string DateText => this.Metadata.DateCreated is { } d ? d.ToString("yyyy-MM-dd HH:mm") : string.Empty;

  /// <summary>
  /// Best-available location string: "Location, City, Country" with empties
  /// dropped. Empty string when no location data is present.
  /// </summary>
  public string LocationText {
    get {
      var parts = new[] { this.Metadata.Location, this.Metadata.City, this.Metadata.Country }
        .Where(s => !string.IsNullOrWhiteSpace(s));
      return string.Join(", ", parts);
    }
  }

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
