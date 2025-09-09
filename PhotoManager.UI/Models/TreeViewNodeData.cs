namespace PhotoManager.UI.Models;

public class TreeViewNodeData(DirectoryInfo path, bool recursive = true) {
  public DirectoryInfo Path { get; set; } = path;
  public bool Recursive { get; set; } = recursive;
}