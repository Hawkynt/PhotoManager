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

  /// <summary>
  /// Lower-cased concatenation of every searchable field for this file
  /// (filename, folder, keywords, people, locations, title, caption…). The
  /// MainWindow search bar does substring contains against this string, so
  /// a single cheap string-scan per row powers the multi-token AND search
  /// without re-reading XMP per keystroke. Populated by the background
  /// metadata-reading pass after the scan.
  /// </summary>
  [Browsable(false)]
  public string SearchIndex { get; set; } = string.Empty;

  public event PropertyChangedEventHandler? PropertyChanged;

  private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null) {
    if (EqualityComparer<T>.Default.Equals(field, value))
      return;

    field = value;
    this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
  }
}
