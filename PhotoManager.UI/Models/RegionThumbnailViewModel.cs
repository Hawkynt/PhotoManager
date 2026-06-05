using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Hawkynt.PhotoManager.Core.Regions;

namespace Hawkynt.PhotoManager.UI.Models;

/// <summary>
/// One tile in the region thumbnail strip. Pairs a <see cref="RegionRowViewModel"/>
/// with its extracted crop. The crop is fetched asynchronously so a big
/// image with many regions doesn't block the UI thread during selection.
/// </summary>
public sealed class RegionThumbnailViewModel : INotifyPropertyChanged {
  private Bitmap? _thumbnail;

  public RegionThumbnailViewModel(RegionRowViewModel row) {
    this.Row = row;
  }

  public RegionRowViewModel Row { get; }

  public int Index => this.Row.Index;
  public string CategoryText => this.Row.CategoryText;
  public Avalonia.Media.IBrush CategoryColor => this.Row.CategoryColor;
  public bool IsProposed => this.Row.IsProposed;
  public string? Label {
    get => this.Row.Label;
    set => this.Row.Label = value;
  }

  /// <summary>Formatted label for the tile — falls back to "(unnamed)" for blank labels.</summary>
  public string DisplayLabel => string.IsNullOrWhiteSpace(this.Row.Label) ? "(unnamed)" : this.Row.Label!;

  /// <summary>
  /// Accept is only offered when the proposal carries a label — there's
  /// nothing to "accept" on an unnamed face. Unnamed proposals still show
  /// Dismiss + Tag so the user can name (which accepts) or remove them.
  /// </summary>
  public bool ShowAccept => this.Row.IsProposed && !string.IsNullOrWhiteSpace(this.Row.Label);

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
