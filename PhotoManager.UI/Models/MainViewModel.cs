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
  private bool _hasSelectedFile;
  private BrowseMode _browseMode = BrowseMode.Sources;
  private List<TreeViewPathData> _treeViewPaths = new();
  private List<SavedSearchData> _savedSearches = new();
  private ThemeVariantPreference _theme = ThemeVariantPreference.System;

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

  /// <summary>
  /// Whether the grid scans the checked source paths (default) or walks
  /// the configured destination directory recursively. Lets the user keep
  /// editing metadata after files have been organised into the library.
  /// </summary>
  public BrowseMode BrowseMode {
    get => this._browseMode;
    set {
      this.SetProperty(this.OnPropertyChanged, ref this._browseMode, value);
      // Mode toggles three derived bindings — emit them all so XAML
      // visibility/IsEnabled bindings update without explicit hooks.
      this.OnPropertyChanged(nameof(this.IsSourceMode));
      this.OnPropertyChanged(nameof(this.IsTargetMode));
    }
  }

  /// <summary>True when <see cref="BrowseMode"/> is <see cref="BrowseMode.Sources"/>. Convenience for XAML <c>IsVisible</c> bindings.</summary>
  public bool IsSourceMode => this._browseMode == BrowseMode.Sources;

  /// <summary>True when <see cref="BrowseMode"/> is <see cref="BrowseMode.Target"/>.</summary>
  public bool IsTargetMode => this._browseMode == BrowseMode.Target;

  /// <summary>
  /// True iff a file row is currently selected in the grid and its metadata
  /// has been loaded. Bound by edit-related buttons and inputs on the main
  /// window so the user can't click Save or Triangulate when there's nothing
  /// to save against.
  /// </summary>
  public bool HasSelectedFile {
    get => this._hasSelectedFile;
    set => this.SetProperty(this.OnPropertyChanged, ref this._hasSelectedFile, value);
  }

  public List<TreeViewPathData> TreeViewPaths {
    get => this._treeViewPaths;
    set => this.SetProperty(this.OnPropertyChanged, ref this._treeViewPaths, value);
  }

  public List<SavedSearchData> SavedSearches {
    get => this._savedSearches;
    set => this.SetProperty(this.OnPropertyChanged, ref this._savedSearches, value);
  }

  /// Persisted Light/Dark/System preference. Mapped to
  /// Application.Current.RequestedThemeVariant at apply time.
  public ThemeVariantPreference Theme {
    get => this._theme;
    set => this.SetProperty(this.OnPropertyChanged, ref this._theme, value);
  }

  public event PropertyChangedEventHandler? PropertyChanged;

  protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

  private void SetProperty<T>(Action<string> onChanged, ref T field, T value, [CallerMemberName] string? propertyName = null) {
    if (EqualityComparer<T>.Default.Equals(field, value))
      return;

    field = value;
    onChanged(propertyName ?? string.Empty);
  }
}
