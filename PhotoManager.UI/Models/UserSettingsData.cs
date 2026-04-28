using PhotoManager.Core.Enums;

namespace PhotoManager.UI.Models;

public record TreeViewPathData {
  public string Path { get; init; } = string.Empty;
  public bool Recursive { get; init; } = true;
  public bool Checked { get; init; } = true;
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
}

public record UserSettingsData {
  public string LastSourceDirectory { get; init; } = string.Empty;
  public string LastDestinationDirectory { get; init; } = string.Empty;
  public DuplicateHandling DuplicateHandling { get; init; } = DuplicateHandling.Smart;
  public bool Recursive { get; init; } = true;
  public bool PreserveOriginals { get; init; } = false;
  public List<TreeViewPathData> TreeViewPaths { get; init; } = new();
  public List<SavedSearchData> SavedSearches { get; init; } = new();
}
