using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Hawkynt.PhotoManager.Core.Library;

namespace Hawkynt.PhotoManager.UI.Models;

/// <summary>
/// One file inside a <see cref="DuplicateGroupViewModel"/>: name, full path,
/// pHash distance to the group anchor, and a lazily-loaded thumbnail. The
/// distance is shown alongside the name so the user can spot which files
/// are exact matches (0) versus near-misses (5–6).
/// </summary>
public sealed class DuplicateMemberViewModel : INotifyPropertyChanged {
  private Bitmap? _thumbnail;

  public DuplicateMemberViewModel(HashedFile hashed, int distance) {
    this.HashedFile = hashed;
    this.Distance = distance;
  }

  public HashedFile HashedFile { get; }
  public int Distance { get; }
  public string FileName => this.HashedFile.File.Name;
  public string FullPath => this.HashedFile.File.FullName;
  public string DistanceLabel => this.Distance == 0 ? "anchor" : $"Δ {this.Distance} bits";

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
