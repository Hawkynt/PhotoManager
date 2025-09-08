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

  public string SourceDirectory {
    get => this._sourceDirectory;
    set {
      if (this._sourceDirectory != value) {
        this._sourceDirectory = value;
        this.OnPropertyChanged();
      }
    }
  }

  public string DestinationDirectory {
    get => this._destinationDirectory;
    set {
      if (this._destinationDirectory != value) {
        this._destinationDirectory = value;
        this.OnPropertyChanged();
      }
    }
  }

  public bool IsProcessing {
    get => this._isProcessing;
    set {
      if (this._isProcessing != value) {
        this._isProcessing = value;
        this.OnPropertyChanged();
      }
    }
  }

  public int ProgressValue {
    get => this._progressValue;
    set {
      if (this._progressValue != value) {
        this._progressValue = value;
        this.OnPropertyChanged();
      }
    }
  }

  public string StatusMessage {
    get => this._statusMessage;
    set {
      if (this._statusMessage != value) {
        this._statusMessage = value;
        this.OnPropertyChanged();
      }
    }
  }

  public ImportResult? LastResult {
    get => this._lastResult;
    set {
      if (this._lastResult != value) {
        this._lastResult = value;
        this.OnPropertyChanged();
      }
    }
  }

  public DuplicateHandling DuplicateHandling {
    get => this._duplicateHandling;
    set {
      if (this._duplicateHandling != value) {
        this._duplicateHandling = value;
        this.OnPropertyChanged();
      }
    }
  }

  public bool Recursive {
    get => this._recursive;
    set {
      if (this._recursive != value) {
        this._recursive = value;
        this.OnPropertyChanged();
      }
    }
  }

  public bool PreserveOriginals {
    get => this._preserveOriginals;
    set {
      if (this._preserveOriginals != value) {
        this._preserveOriginals = value;
        this.OnPropertyChanged();
      }
    }
  }

  public event PropertyChangedEventHandler? PropertyChanged;

  protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) {
    this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
  }
}
