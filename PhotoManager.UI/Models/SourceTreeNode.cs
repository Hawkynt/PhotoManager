using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PhotoManager.UI.Models;

/// <summary>
/// UI-framework-agnostic tree node representing either a root source path
/// (with a recursive flag) or one of its non-recursive children.
/// </summary>
public sealed class SourceTreeNode : INotifyPropertyChanged {
  private bool _isChecked = true;
  private bool _isRecursive;
  private string _displayName;

  public SourceTreeNode(DirectoryInfo path, bool isRecursive, bool isRoot) {
    this.Path = path;
    this._isRecursive = isRecursive;
    this.IsRoot = isRoot;
    this._displayName = BuildDisplayName(path, isRecursive, isRoot);
  }

  public DirectoryInfo Path { get; }
  public bool IsRoot { get; }
  public ObservableCollection<SourceTreeNode> Children { get; } = new();

  public bool IsRecursive {
    get => this._isRecursive;
    set {
      if (this._isRecursive == value)
        return;

      this._isRecursive = value;
      this.DisplayName = BuildDisplayName(this.Path, value, this.IsRoot);
      this.OnPropertyChanged();
    }
  }

  public bool IsChecked {
    get => this._isChecked;
    set {
      if (this._isChecked == value)
        return;

      this._isChecked = value;
      this.OnPropertyChanged();
    }
  }

  public string DisplayName {
    get => this._displayName;
    private set {
      if (this._displayName == value)
        return;

      this._displayName = value;
      this.OnPropertyChanged();
    }
  }

  public string FullPathTooltip => this.Path.FullName;

  public event PropertyChangedEventHandler? PropertyChanged;
  private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

  private static string BuildDisplayName(DirectoryInfo path, bool recursive, bool isRoot) {
    var baseName = System.IO.Path.GetFileName(path.FullName);
    if (string.IsNullOrEmpty(baseName))
      baseName = path.FullName;

    if (!isRoot)
      return baseName;

    return baseName + (recursive ? " (Recursive)" : " (Non-recursive)");
  }
}
