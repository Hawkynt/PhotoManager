using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using PhotoManager.Core.Library;

namespace PhotoManager.UI.Models;

/// <summary>
/// One result tile in the library-search window. Async-loads the thumbnail
/// so a 1000-hit query doesn't block the UI thread while ImageSharp decodes
/// each photo.
/// </summary>
public sealed class SearchHitViewModel : INotifyPropertyChanged {
  private Bitmap? _thumbnail;

  public SearchHitViewModel(SearchHit hit) {
    this.Hit = hit;
  }

  public SearchHit Hit { get; }
  public string FileName => this.Hit.File.Name;
  public string DirectoryName => this.Hit.File.DirectoryName ?? string.Empty;

  public string Caption {
    get {
      var md = this.Hit.Metadata;
      var parts = new List<string>();
      if (md.Rating is { } rating && rating > 0)
        parts.Add(new string('★', Math.Min(rating, 5)));
      if (!string.IsNullOrWhiteSpace(md.City))
        parts.Add(md.City!);
      else if (!string.IsNullOrWhiteSpace(md.Country))
        parts.Add(md.Country!);
      if (md.Keywords.Count > 0)
        parts.Add(string.Join(", ", md.Keywords.Take(3)));
      return parts.Count == 0 ? string.Empty : string.Join("  ·  ", parts);
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
