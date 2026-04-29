using PhotoManager.Core.Enums;

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

public record UserSettingsData {
  public string LastSourceDirectory { get; init; } = string.Empty;
  public string LastDestinationDirectory { get; init; } = string.Empty;
  public DuplicateHandling DuplicateHandling { get; init; } = DuplicateHandling.Smart;
  public bool Recursive { get; init; } = true;
  public bool PreserveOriginals { get; init; } = false;
  public List<TreeViewPathData> TreeViewPaths { get; init; } = new();
  public List<SavedSearchData> SavedSearches { get; init; } = new();
  public ThemeVariantPreference Theme { get; init; } = ThemeVariantPreference.System;
}
