namespace Hawkynt.PhotoManager.Core.Library;

/// <summary>
/// Pure logic for the hierarchical keyword tree. The internal model lets the
/// user organise tags as <c>Travel &gt; Italy &gt; Rome</c>, but on the photo
/// only flat <c>dc:subject</c> values are written so any reader picks them up
/// — selecting "Rome" implicitly also writes "Italy" and "Travel". Lookup is
/// case-insensitive; selections that don't appear in the tree are passed
/// through unchanged so users can still add ad-hoc keywords without touching
/// the tree.
/// </summary>
public static class KeywordHierarchy {
  /// <summary>
  /// Expand a set of selected leaf names into the full keyword set that
  /// should be written to the photo: the selections themselves, plus every
  /// ancestor of every match found in the tree, with duplicates removed
  /// (case-insensitive) but preserving the first-seen casing for each name.
  /// Names that don't exist in the tree are kept as-is.
  /// </summary>
  public static IReadOnlyList<string> Expand(IReadOnlyList<KeywordNode> roots, IEnumerable<string> selectedLeafNames) {
    ArgumentNullException.ThrowIfNull(roots);
    ArgumentNullException.ThrowIfNull(selectedLeafNames);

    var result = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var name in selectedLeafNames) {
      if (string.IsNullOrWhiteSpace(name))
        continue;

      var trimmed = name.Trim();
      var paths = FindAncestorChains(roots, trimmed);
      if (paths.Count == 0) {
        if (seen.Add(trimmed))
          result.Add(trimmed);
        continue;
      }

      foreach (var chain in paths) {
        foreach (var part in chain) {
          if (seen.Add(part))
            result.Add(part);
        }
      }
    }

    return result;
  }

  /// <summary>
  /// Find every node whose <see cref="KeywordNode.Name"/> matches
  /// <paramref name="name"/> (case-insensitive). The same name may appear in
  /// several places in the tree — all matches are returned.
  /// </summary>
  public static IEnumerable<KeywordNode> Find(IReadOnlyList<KeywordNode> roots, string name) {
    ArgumentNullException.ThrowIfNull(roots);
    if (string.IsNullOrWhiteSpace(name))
      yield break;

    var target = name.Trim();
    foreach (var root in roots) {
      foreach (var hit in FindRecursive(root, target))
        yield return hit;
    }
  }

  /// <summary>
  /// Return the full path (root → … → match) for every match. Used by the UI
  /// to render hierarchical keyword display ("Travel/Italy/Rome").
  /// </summary>
  public static IReadOnlyList<IReadOnlyList<string>> FindPaths(IReadOnlyList<KeywordNode> roots, string name) {
    ArgumentNullException.ThrowIfNull(roots);
    if (string.IsNullOrWhiteSpace(name))
      return Array.Empty<IReadOnlyList<string>>();

    return FindAncestorChains(roots, name.Trim());
  }

  private static IEnumerable<KeywordNode> FindRecursive(KeywordNode node, string target) {
    if (string.Equals(node.Name, target, StringComparison.OrdinalIgnoreCase))
      yield return node;
    foreach (var child in node.Children)
      foreach (var hit in FindRecursive(child, target))
        yield return hit;
  }

  private static List<IReadOnlyList<string>> FindAncestorChains(IReadOnlyList<KeywordNode> roots, string target) {
    var matches = new List<IReadOnlyList<string>>();
    var stack = new Stack<string>();
    foreach (var root in roots)
      Walk(root, target, stack, matches);
    return matches;
  }

  private static void Walk(KeywordNode node, string target, Stack<string> stack, List<IReadOnlyList<string>> matches) {
    stack.Push(node.Name);
    if (string.Equals(node.Name, target, StringComparison.OrdinalIgnoreCase)) {
      // Stack.ToArray returns top-first; reverse so the chain reads root → leaf.
      var chain = stack.ToArray();
      Array.Reverse(chain);
      matches.Add(chain);
    }
    foreach (var child in node.Children)
      Walk(child, target, stack, matches);
    stack.Pop();
  }
}
