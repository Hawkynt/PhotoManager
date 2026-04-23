using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using PhotoManager.Core.Regions;

namespace PhotoManager.UI.Models;

/// <summary>
/// One row in the region review panel. Wraps a <see cref="TaggedRegion"/>
/// with UI-flavored bits: brush for the category swatch, a short status
/// label, and bindable <see cref="Label"/> so the user can edit inline.
/// </summary>
public sealed class RegionRowViewModel : INotifyPropertyChanged {
  private string? _label;

  public RegionRowViewModel(int index, TaggedRegion region) {
    this.Index = index;
    this.Region = region;
    this._label = region.Label;
  }

  public int Index { get; }
  public TaggedRegion Region { get; }

  public string? Label {
    get => this._label;
    set {
      if (this._label == value) return;
      this._label = value;
      this.OnPropertyChanged();
    }
  }

  public string CategoryText => this.Region.Category.ToString();
  public IBrush CategoryColor => new SolidColorBrush(Color.Parse(this.Region.Category.ToHexColor()));
  public bool IsProposed => this.Region.Status == RegionStatus.Proposed;

  public event PropertyChangedEventHandler? PropertyChanged;
  private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
