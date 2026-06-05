using System.ComponentModel;
using System.Runtime.CompilerServices;
using Hawkynt.PhotoManager.Core.Library;

namespace Hawkynt.PhotoManager.UI.Models;

/// <summary>
/// One detected burst tile in the BurstStacksWindow. Wraps a <see cref="BurstGroup"/>
/// with a select flag and an editable burst-id keyword that gets written to
/// every member when the user clicks "Apply".
/// </summary>
public sealed class BurstGroupViewModel : INotifyPropertyChanged {
  private bool _isSelected;
  private string _burstId;

  public BurstGroupViewModel(BurstGroup group) {
    this.Group = group;
    this._burstId = group.SuggestedName;
  }

  public BurstGroup Group { get; }
  public int Count => this.Group.Members.Count;
  public string CountText => this.Count == 1 ? "1 photo" : $"{this.Count} photos";
  public string RangeText => this.Group.From == this.Group.To
    ? this.Group.From.ToString("yyyy-MM-dd HH:mm:ss")
    : $"{this.Group.From:yyyy-MM-dd HH:mm:ss} → {this.Group.To:HH:mm:ss}";
  public string FilesText => string.Join(", ", this.Group.Members.Select(m => m.Name));
  public string SuggestedName => this.Group.SuggestedName;

  public bool IsSelected {
    get => this._isSelected;
    set {
      if (this._isSelected == value)
        return;
      this._isSelected = value;
      this.OnPropertyChanged();
    }
  }

  public string BurstId {
    get => this._burstId;
    set {
      if (this._burstId == value)
        return;
      this._burstId = value ?? string.Empty;
      this.OnPropertyChanged();
    }
  }

  public event PropertyChangedEventHandler? PropertyChanged;
  private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
