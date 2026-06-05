using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Hawkynt.PhotoManager.Core.Faces;

namespace Hawkynt.PhotoManager.UI.Models;

/// <summary>
/// One cluster tile in the face gallery. Holds the underlying
/// <see cref="FaceCluster"/>, the thumbnail of the cluster's representative
/// face (cropped from the first member), and the editable name. Used by
/// <c>FaceGalleryWindow</c>.
/// </summary>
public sealed class FaceClusterViewModel : INotifyPropertyChanged {
  private Bitmap? _thumbnail;
  private string _name;
  private bool _isSelected;

  public FaceClusterViewModel(FaceCluster cluster) {
    this.Cluster = cluster;
    this._name = cluster.Label ?? string.Empty;
    this.MemberThumbnails = new ObservableCollection<FaceMemberThumbnailViewModel>();
  }

  public FaceCluster Cluster { get; }

  public string DisplayName => this.Cluster.DisplayName;
  public int Count => this.Cluster.Count;
  public string CountText => $"{this.Count} face(s)";
  public bool IsNamed => this.Cluster.IsNamed;

  public string Name {
    get => this._name;
    set {
      if (this._name == value)
        return;
      this._name = value ?? string.Empty;
      this.OnPropertyChanged();
    }
  }

  public bool IsSelected {
    get => this._isSelected;
    set {
      if (this._isSelected == value)
        return;
      this._isSelected = value;
      this.OnPropertyChanged();
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

  public ObservableCollection<FaceMemberThumbnailViewModel> MemberThumbnails { get; }

  public event PropertyChangedEventHandler? PropertyChanged;
  private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class FaceMemberThumbnailViewModel : INotifyPropertyChanged {
  private Bitmap? _thumbnail;
  private bool _isSelected;

  public FaceMemberThumbnailViewModel(ScannedFace face) {
    this.Face = face;
  }

  public ScannedFace Face { get; }
  public string FileName => this.Face.File.Name;

  public Bitmap? Thumbnail {
    get => this._thumbnail;
    set {
      if (ReferenceEquals(this._thumbnail, value))
        return;
      this._thumbnail = value;
      this.OnPropertyChanged();
    }
  }

  public bool IsSelected {
    get => this._isSelected;
    set {
      if (this._isSelected == value)
        return;
      this._isSelected = value;
      this.OnPropertyChanged();
    }
  }

  public event PropertyChangedEventHandler? PropertyChanged;
  private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
