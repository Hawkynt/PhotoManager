using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PhotoManager.Core.Enums;
using PhotoManager.Core.Library;
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
  private List<SmartAlbumRule> _smartAlbums = new();
  private List<KeywordNode> _keywordTreeRoots = new();
  private string? _defaultDevelopPreset;
  private bool _geofenceAutoTagOnScan;
  private ThemeVariantPreference _theme = ThemeVariantPreference.System;
  private string _defaultRenameTemplate = "{date:yyyy-MM-dd}_{name}";
  private int _recentFoldersDepth = 5;
  private bool _autoDetectFacesOnScan;
  private bool _autoDetectObjectsOnScan;
  private bool _reverseGeocoderEnabled = true;
  private bool _elevationLookupEnabled = true;
  private int _geocoderRateLimitPerSecond = 1;
  private OperationProgress? _currentOperation;

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

  /// Composable rule-based filters distinct from <see cref="SavedSearches"/>
  /// (which are flat text/star/label snapshots). Loaded into the search
  /// pipeline when the user picks one from the smart-album dropdown.
  public List<SmartAlbumRule> SmartAlbums {
    get => this._smartAlbums;
    set => this.SetProperty(this.OnPropertyChanged, ref this._smartAlbums, value);
  }

  public List<KeywordNode> KeywordTreeRoots {
    get => this._keywordTreeRoots;
    set => this.SetProperty(this.OnPropertyChanged, ref this._keywordTreeRoots, value);
  }

  public string? DefaultDevelopPreset {
    get => this._defaultDevelopPreset;
    set => this.SetProperty(this.OnPropertyChanged, ref this._defaultDevelopPreset, value);
  }

  public bool GeofenceAutoTagOnScan {
    get => this._geofenceAutoTagOnScan;
    set => this.SetProperty(this.OnPropertyChanged, ref this._geofenceAutoTagOnScan, value);
  }

  /// Persisted Light/Dark/System preference. Mapped to
  /// Application.Current.RequestedThemeVariant at apply time.
  public ThemeVariantPreference Theme {
    get => this._theme;
    set => this.SetProperty(this.OnPropertyChanged, ref this._theme, value);
  }

  public string DefaultRenameTemplate {
    get => this._defaultRenameTemplate;
    set => this.SetProperty(this.OnPropertyChanged, ref this._defaultRenameTemplate, value);
  }

  public int RecentFoldersDepth {
    get => this._recentFoldersDepth;
    set => this.SetProperty(this.OnPropertyChanged, ref this._recentFoldersDepth, value);
  }

  public bool AutoDetectFacesOnScan {
    get => this._autoDetectFacesOnScan;
    set => this.SetProperty(this.OnPropertyChanged, ref this._autoDetectFacesOnScan, value);
  }

  public bool AutoDetectObjectsOnScan {
    get => this._autoDetectObjectsOnScan;
    set => this.SetProperty(this.OnPropertyChanged, ref this._autoDetectObjectsOnScan, value);
  }

  public bool ReverseGeocoderEnabled {
    get => this._reverseGeocoderEnabled;
    set => this.SetProperty(this.OnPropertyChanged, ref this._reverseGeocoderEnabled, value);
  }

  public bool ElevationLookupEnabled {
    get => this._elevationLookupEnabled;
    set => this.SetProperty(this.OnPropertyChanged, ref this._elevationLookupEnabled, value);
  }

  public int GeocoderRateLimitPerSecond {
    get => this._geocoderRateLimitPerSecond;
    set => this.SetProperty(this.OnPropertyChanged, ref this._geocoderRateLimitPerSecond, value);
  }

  /// <summary>
  /// Recently-used source folders (full paths). Most-recent first, capped at
  /// <see cref="RecentFoldersDepth"/>. Bound by the <c>File → Recent sources</c>
  /// menu — the menu rebuilds whenever this collection's contents change.
  /// </summary>
  public ObservableCollection<string> RecentSourceFolders { get; } = new();

  /// <summary>Recently-used output folders. Same rules as <see cref="RecentSourceFolders"/>.</summary>
  public ObservableCollection<string> RecentOutputFolders { get; } = new();

  /// <summary>
  /// The currently-running long operation, or null when idle. The status-bar
  /// progress strip is visible iff this is non-null.
  /// </summary>
  public OperationProgress? CurrentOperation {
    get => this._currentOperation;
    set {
      this.SetProperty(this.OnPropertyChanged, ref this._currentOperation, value);
      this.OnPropertyChanged(nameof(this.HasCurrentOperation));
    }
  }

  public bool HasCurrentOperation => this._currentOperation is not null;

  public event PropertyChangedEventHandler? PropertyChanged;

  protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

  private void SetProperty<T>(Action<string> onChanged, ref T field, T value, [CallerMemberName] string? propertyName = null) {
    if (EqualityComparer<T>.Default.Equals(field, value))
      return;

    field = value;
    onChanged(propertyName ?? string.Empty);
  }
}
