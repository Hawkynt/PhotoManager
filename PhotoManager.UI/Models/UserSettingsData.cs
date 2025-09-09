using PhotoManager.Core.Enums;

namespace PhotoManager.UI.Models;

public record TreeViewPathData {
  public string Path { get; init; } = string.Empty;
  public bool Recursive { get; init; } = true;
  public bool Checked { get; init; } = true;
}

public record UserSettingsData {
  public string LastSourceDirectory { get; init; } = string.Empty;
  public string LastDestinationDirectory { get; init; } = string.Empty;
  public DuplicateHandling DuplicateHandling { get; init; } = DuplicateHandling.Smart;
  public bool Recursive { get; init; } = true;
  public bool PreserveOriginals { get; init; } = false;
  public List<TreeViewPathData> TreeViewPaths { get; init; } = new();
}