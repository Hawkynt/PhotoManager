using System.Collections.ObjectModel;

namespace Hawkynt.PhotoManager.UI.Models;

/// <summary>
/// UI wrapper around <see cref="Hawkynt.PhotoManager.Core.Library.QuickCollection"/>
/// that adds an <see cref="ObservableCollection{FileInfo}"/> for data-binding
/// (e.g. the Develop apply-to-selection workflow feeds from <see cref="Items"/>).
/// The core hash-set lives in the delegate; the observable list is kept in sync
/// for any UI consumers that need change notifications.
/// </summary>
public sealed class QuickCollection {
  private readonly Core.Library.QuickCollection _core = new();
  private readonly ObservableCollection<FileInfo> _items = new();

  public ObservableCollection<FileInfo> Items => this._items;

  public int Count => this._core.Count;

  public bool Contains(FileInfo file) => this._core.Contains(file);

  public bool Contains(string fullPath) => this._core.Contains(fullPath);

  /// <summary>Add the file if not present, remove it if already present. Returns the new membership state.</summary>
  public bool Toggle(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    if (this._core.Contains(file)) {
      this.Remove(file);
      return false;
    }
    this._core.Add(file);
    this._items.Add(file);
    return true;
  }

  public void Add(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    if (this._core.Contains(file))
      return;
    this._core.Add(file);
    this._items.Add(file);
  }

  public void Remove(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    if (!this._core.Contains(file))
      return;
    this._core.Remove(file);
    for (var i = 0; i < this._items.Count; i++) {
      if (string.Equals(this._items[i].FullName, file.FullName, StringComparison.OrdinalIgnoreCase)) {
        this._items.RemoveAt(i);
        return;
      }
    }
  }

  public void Clear() {
    this._core.Clear();
    this._items.Clear();
  }

  public IReadOnlySet<string> GetFiles() => this._core.GetFiles();
}
