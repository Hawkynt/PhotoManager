namespace PhotoManager.Core.Library;

/// <summary>
/// One node in the user's hierarchical keyword tree. Children is mutable so
/// the UI can drag-and-drop / add / remove subtrees in place; the tree itself
/// is persisted as JSON in <c>UserSettingsData.KeywordTreeRoots</c>. On write
/// to a photo's XMP a selected leaf flattens to its full ancestor chain so
/// other tools see standard <c>dc:subject</c> keywords.
/// </summary>
public sealed record KeywordNode {
  public string Name { get; set; } = string.Empty;
  public List<KeywordNode> Children { get; set; } = new();

  public KeywordNode() { }

  public KeywordNode(string name) {
    this.Name = name;
  }

  public KeywordNode(string name, IEnumerable<KeywordNode> children) {
    this.Name = name;
    this.Children = children.ToList();
  }
}
