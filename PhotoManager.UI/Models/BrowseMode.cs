namespace PhotoManager.UI.Models;

/// <summary>
/// Where the main-window file grid pulls its rows from.
/// <list type="bullet">
///   <item><see cref="Sources"/> — the checked paths in the source-tree pane (default behaviour).</item>
///   <item><see cref="Target"/> — recursive walk of the configured destination directory, so the user can keep editing metadata after files have been organised.</item>
/// </list>
/// </summary>
public enum BrowseMode {
  Sources,
  Target
}
