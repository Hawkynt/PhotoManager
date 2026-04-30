using PhotoManager.Core.Enums;
using PhotoManager.Core.Library;

namespace PhotoManager.UI.Models;

public record TreeViewPathData {
  public string Path { get; init; } = string.Empty;
  public bool Recursive { get; init; } = true;
  public bool Checked { get; init; } = true;
}

/// <summary>
/// Cycling state for the pick/reject filter chip.
///   <see cref="ShowAll"/> = no filtering on flags (default)
///   <see cref="HideRejects"/> = drop rejects, keep everything else
///   <see cref="PicksOnly"/> = only Picks
///   <see cref="RejectsOnly"/> = only Rejects (cull review)
/// </summary>
public enum PickRejectFilterMode {
  ShowAll = 0,
  HideRejects = 1,
  PicksOnly = 2,
  RejectsOnly = 3
}

/// <summary>
/// A bookmarked filter for the main-window grid: text query + chip state.
/// Persisted between sessions so common filters ("4★ green from Berlin")
/// are one click away.
/// </summary>
public record SavedSearchData {
  public string Name { get; init; } = string.Empty;
  public string Text { get; init; } = string.Empty;
  public int? MinStars { get; init; }
  public string? ColorLabel { get; init; }
  public PickRejectFilterMode PickFilter { get; init; } = PickRejectFilterMode.ShowAll;
}

/// <summary>
/// Persisted theme preference. Mirrors Avalonia's <c>ThemeVariant</c> set
/// without referencing it from the persistence layer so the JSON stays
/// simple and portable; mapping happens in <c>ThemeApplier</c>.
/// </summary>
public enum ThemeVariantPreference {
  System = 0,
  Light = 1,
  Dark = 2
}

/// <summary>
/// Most-recently-used folder lists for the File menu sub-menus. Two separate
/// lists so users can pull back a source folder without losing the matching
/// output, and vice versa. Trimmed to a small fixed depth to keep the menu
/// short and the JSON tidy.
/// </summary>
public record RecentFoldersData {
  public List<string> SourceFolders { get; init; } = new();
  public List<string> OutputFolders { get; init; } = new();
}

public record UserSettingsData {
  public string LastSourceDirectory { get; init; } = string.Empty;
  public string LastDestinationDirectory { get; init; } = string.Empty;
  public DuplicateHandling DuplicateHandling { get; init; } = DuplicateHandling.Smart;
  public bool Recursive { get; init; } = true;
  public bool PreserveOriginals { get; init; } = false;
  public List<TreeViewPathData> TreeViewPaths { get; init; } = new();
  public List<SavedSearchData> SavedSearches { get; init; } = new();
  public ThemeVariantPreference Theme { get; init; } = ThemeVariantPreference.System;

  public RecentFoldersData RecentFolders { get; init; } = new();

  /// <summary>Default rename template applied when the batch-rename window opens with no recent template.</summary>
  public string DefaultRenameTemplate { get; init; } = "{date:yyyy-MM-dd}_{name}";

  /// <summary>How many entries to keep in each recent-folders list (and Most-Recent dialogs).</summary>
  public int RecentFoldersDepth { get; init; } = 5;

  /// <summary>Future hook: kick off face detection automatically after a scan completes.</summary>
  public bool AutoDetectFacesOnScan { get; init; } = false;

  /// <summary>Future hook: kick off object detection automatically after a scan completes.</summary>
  public bool AutoDetectObjectsOnScan { get; init; } = false;

  /// <summary>Toggle for the city/country reverse-geocoder lookup.</summary>
  public bool ReverseGeocoderEnabled { get; init; } = true;

  /// <summary>Toggle for the GPS-elevation lookup service.</summary>
  public bool ElevationLookupEnabled { get; init; } = true;

  /// <summary>Soft cap on outbound geocoder requests per second so we stay friendly to free APIs.</summary>
  public int GeocoderRateLimitPerSecond { get; init; } = 1;

  /// <summary>
  /// Composable smart-album rules. Each rule has a name + a list of typed
  /// clauses (rating, keyword, location, GPS box, …) AND/OR-combined.
  /// Round-trips polymorphically via JsonDerivedType on RuleClause.
  /// </summary>
  public List<SmartAlbumRule> SmartAlbums { get; init; } = new();

  /// <summary>Hierarchical keyword tree; flattens to flat dc:subject keywords on write.</summary>
  public List<KeywordNode> KeywordTreeRoots { get; init; } = new();

  /// <summary>
  /// Filename of a <c>DevelopTemplateStore</c> preset auto-applied to every
  /// newly-imported file that has no embedded <c>pm:developSettings</c>.
  /// Null = no auto-preset.
  /// </summary>
  public string? DefaultDevelopPreset { get; init; }

  /// <summary>
  /// When true, sweep newly-scanned files against the saved map bookmarks
  /// and merge each in-radius bookmark's place fields into the file's metadata.
  /// </summary>
  public bool GeofenceAutoTagOnScan { get; init; } = false;
}
