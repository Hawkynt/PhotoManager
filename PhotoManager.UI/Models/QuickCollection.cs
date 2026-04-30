using System.Collections.ObjectModel;

namespace PhotoManager.UI.Models;

/// <summary>
/// Lightroom-style "Quick Collection" — a per-session bucket of files the
/// user is curating. Cleared on app close (no persistence). The UI exposes:
/// a B-key toggle to add/remove the focused file, a star badge on grid
/// tiles, a filter mode that shows only members, and Tools menu actions
/// to feed the bucket into existing workflows (batch rename, develop
/// apply-to-selection).
/// </summary>
public sealed class QuickCollection {
  private readonly ObservableCollection<FileInfo> _items = new();
  private readonly HashSet<string> _membership = new(StringComparer.OrdinalIgnoreCase);

  public ObservableCollection<FileInfo> Items => this._items;

  public int Count => this._items.Count;

  public bool Contains(FileInfo file) => file != null && this._membership.Contains(file.FullName);

  public bool Contains(string fullPath) => !string.IsNullOrEmpty(fullPath) && this._membership.Contains(fullPath);

  /// <summary>Add the file if not present, remove it if already present. Returns the new membership state.</summary>
  public bool Toggle(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    if (this._membership.Contains(file.FullName)) {
      this.Remove(file);
      return false;
    }
    this._membership.Add(file.FullName);
    this._items.Add(file);
    return true;
  }

  public void Add(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    if (this._membership.Add(file.FullName))
      this._items.Add(file);
  }

  public void Remove(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    if (!this._membership.Remove(file.FullName))
      return;
    for (var i = 0; i < this._items.Count; i++) {
      if (string.Equals(this._items[i].FullName, file.FullName, StringComparison.OrdinalIgnoreCase)) {
        this._items.RemoveAt(i);
        return;
      }
    }
  }

  public void Clear() {
    this._items.Clear();
    this._membership.Clear();
  }
}
