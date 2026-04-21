using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PhotoManager.UI.Models;

public class FileItemModel : INotifyPropertyChanged {
  private string _fileName = string.Empty;
  private string _targetLocation = string.Empty;
  private string _sourcePath = string.Empty;

  public string FileName {
    get => this._fileName;
    set => this.SetProperty(ref this._fileName, value);
  }

  public string TargetLocation {
    get => this._targetLocation;
    set => this.SetProperty(ref this._targetLocation, value);
  }

  public string SourcePath {
    get => this._sourcePath;
    set => this.SetProperty(ref this._sourcePath, value);
  }

  [Browsable(false)]
  public FileInfo? FileInfo { get; set; }

  public event PropertyChangedEventHandler? PropertyChanged;

  private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null) {
    if (EqualityComparer<T>.Default.Equals(field, value))
      return;

    field = value;
    this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
  }
}
