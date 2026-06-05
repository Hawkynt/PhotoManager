using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Hawkynt.PhotoManager.Core.Enums;
using Hawkynt.PhotoManager.Core.Library;
using Hawkynt.PhotoManager.Core.Models;
using Hawkynt.PhotoManager.Core.Segmentation;

namespace Hawkynt.PhotoManager.UI.Models;

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

  private readonly QuickCollection _quickCollection = new();
  private bool _quickCollectionFilterActive;
  private bool _timelineCollapsed = true;

  /// Per-session bucket of files the user is curating (Lightroom-style quick collection).
  public QuickCollection QuickCollection => this._quickCollection;

  public bool QuickCollectionFilterActive {
    get => this._quickCollectionFilterActive;
    set => this.SetProperty(this.OnPropertyChanged, ref this._quickCollectionFilterActive, value);
  }

  public bool TimelineCollapsed {
    get => this._timelineCollapsed;
    set {
      this.SetProperty(this.OnPropertyChanged, ref this._timelineCollapsed, value);
      this.OnPropertyChanged(nameof(this.TimelineExpanded));
    }
  }

  public bool TimelineExpanded => !this._timelineCollapsed;

  // ---- Status-bar segment properties ----
  private int _totalPhotoCount;
  private int _selectedPhotoCount;
  private string? _activeFilterDescription;
  private string _acceleratorDevice = OnnxAcceleration.LastSelectedDevice;
  private long _cacheSizeBytes;
  private int _preCachePercent = -1;

  public int TotalPhotoCount {
    get => this._totalPhotoCount;
    set {
      this.SetProperty(this.OnPropertyChanged, ref this._totalPhotoCount, value);
      this.OnPropertyChanged(nameof(this.PhotoCountSegment));
    }
  }

  public int SelectedPhotoCount {
    get => this._selectedPhotoCount;
    set {
      this.SetProperty(this.OnPropertyChanged, ref this._selectedPhotoCount, value);
      this.OnPropertyChanged(nameof(this.PhotoCountSegment));
    }
  }

  /// <summary>E.g. "4*+" or "Red" or null when no chip filter is active.</summary>
  public string? ActiveFilterDescription {
    get => this._activeFilterDescription;
    set {
      this.SetProperty(this.OnPropertyChanged, ref this._activeFilterDescription, value);
      this.OnPropertyChanged(nameof(this.HasActiveFilter));
      this.OnPropertyChanged(nameof(this.ActiveFilterSegment));
    }
  }

  public bool HasActiveFilter => !string.IsNullOrEmpty(this._activeFilterDescription);
  public string ActiveFilterSegment => this._activeFilterDescription is { } f ? $"Filter: {f}" : string.Empty;

  public string AcceleratorDevice {
    get => this._acceleratorDevice;
    set => this.SetProperty(this.OnPropertyChanged, ref this._acceleratorDevice, value);
  }

  public long CacheSizeBytes {
    get => this._cacheSizeBytes;
    set {
      this.SetProperty(this.OnPropertyChanged, ref this._cacheSizeBytes, value);
      this.OnPropertyChanged(nameof(this.CacheSizeDisplay));
    }
  }

  public string CacheSizeDisplay => this._cacheSizeBytes switch {
    >= 1024 * 1024 => $"Cache: {this._cacheSizeBytes / (1024 * 1024)} MB",
    >= 1024        => $"Cache: {this._cacheSizeBytes / 1024} KB",
    _              => $"Cache: {this._cacheSizeBytes} B"
  };

  /// <summary>-1 when not pre-caching, 0..100 when active.</summary>
  public int PreCachePercent {
    get => this._preCachePercent;
    set {
      this.SetProperty(this.OnPropertyChanged, ref this._preCachePercent, value);
      this.OnPropertyChanged(nameof(this.IsPreCaching));
      this.OnPropertyChanged(nameof(this.PreCacheDisplay));
    }
  }

  public bool IsPreCaching => this._preCachePercent >= 0 && this._preCachePercent < 100;
  public string PreCacheDisplay => this._preCachePercent >= 0 ? $"Pre-caching: {this._preCachePercent}%" : string.Empty;

  public string PhotoCountSegment => this._selectedPhotoCount > 0
    ? $"{this._selectedPhotoCount:N0} of {this._totalPhotoCount:N0} photos"
    : $"{this._totalPhotoCount:N0} photos";

  /// <summary>Call after changing TotalPhotoCount or SelectedPhotoCount to
  /// refresh the derived display text.</summary>
  public void RefreshPhotoCountSegment() => this.OnPropertyChanged(nameof(this.PhotoCountSegment));

  public event PropertyChangedEventHandler? PropertyChanged;

  protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

  private void SetProperty<T>(Action<string> onChanged, ref T field, T value, [CallerMemberName] string? propertyName = null) {
    if (EqualityComparer<T>.Default.Equals(field, value))
      return;

    field = value;
    onChanged(propertyName ?? string.Empty);
  }
}
