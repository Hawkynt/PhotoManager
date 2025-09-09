using System.ComponentModel;
using System.Runtime.CompilerServices;
using PhotoManager.Core.Enums;
using PhotoManager.Core.Models;

namespace PhotoManager.UI.Models;

public class MainViewModel : INotifyPropertyChanged {
  private string _sourceDirectory = string.Empty;
  private string _destinationDirectory = string.Empty;
  private bool _isProcessing;
  private int _progressValue;
  private string _statusMessage = "Ready";
  private ImportResult? _lastResult;
  private DuplicateHandling _duplicateHandling = DuplicateHandling.Smart;
  private bool _recursive = true;
  private bool _preserveOriginals = false;
  private List<TreeViewPathData> _treeViewPaths = new();

  public string SourceDirectory {
    get => this._sourceDirectory;
    set => this.SetProperty(this.OnPropertyChanged, ref this._sourceDirectory, value);
  }

  public string DestinationDirectory {
    get => this._destinationDirectory;
    set => this.SetProperty(this.OnPropertyChanged, ref this._destinationDirectory, value);
  }

  public bool IsProcessing {
    get => this._isProcessing;
    set => this.SetProperty(this.OnPropertyChanged, ref this._isProcessing, value);
  }

  public int ProgressValue {
    get => this._progressValue;
    set => this.SetProperty(this.OnPropertyChanged, ref this._progressValue, value);
  }

  public string StatusMessage {
    get => this._statusMessage;
    set => this.SetProperty(this.OnPropertyChanged, ref this._statusMessage, value);
  }

  public ImportResult? LastResult {
    get => this._lastResult;
    set => this.SetProperty(this.OnPropertyChanged, ref this._lastResult, value);
  }

  public DuplicateHandling DuplicateHandling {
    get => this._duplicateHandling;
    set => this.SetProperty(this.OnPropertyChanged, ref this._duplicateHandling, value);
  }

  public bool Recursive {
    get => this._recursive;
    set => this.SetProperty(this.OnPropertyChanged, ref this._recursive, value);
  }

  public bool PreserveOriginals {
    get => this._preserveOriginals;
    set => this.SetProperty(this.OnPropertyChanged, ref this._preserveOriginals, value);
  }

  public List<TreeViewPathData> TreeViewPaths {
    get => this._treeViewPaths;
    set => this.SetProperty(this.OnPropertyChanged, ref this._treeViewPaths, value);
  }

  public event PropertyChangedEventHandler? PropertyChanged;

  protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

}
