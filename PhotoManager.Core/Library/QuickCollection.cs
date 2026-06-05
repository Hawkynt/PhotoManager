namespace Hawkynt.PhotoManager.Core.Library;

/// <summary>
/// Lightroom-style "Quick Collection" — a per-session unsorted bucket of file
/// paths the user is curating. Backed by a <see cref="HashSet{String}"/> of
/// absolute file paths so membership survives across grid refreshes. Per-session
/// only — no persistence to disk; clears when the app closes.
/// </summary>
public sealed class QuickCollection {
  private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Number of files currently in the quick collection.</summary>
  public int Count => this._paths.Count;

  /// <summary>Add a file to the collection. Idempotent — duplicate adds are no-ops.</summary>
  public void Add(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    this._paths.Add(file.FullName);
  }

  /// <summary>Remove a file from the collection. No-op if the file is not a member.</summary>
  public void Remove(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    this._paths.Remove(file.FullName);
  }

  /// <summary>
  /// Toggle the file's membership. Returns <c>true</c> if the file was added,
  /// <c>false</c> if it was removed.
  /// </summary>
  public bool Toggle(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    if (this._paths.Contains(file.FullName)) {
      this._paths.Remove(file.FullName);
      return false;
    }
    this._paths.Add(file.FullName);
    return true;
  }

  /// <summary>Check whether the file is in the collection.</summary>
  public bool Contains(FileInfo file) => file != null && this._paths.Contains(file.FullName);

  /// <summary>Check whether the absolute path is in the collection.</summary>
  public bool Contains(string fullPath) => !string.IsNullOrEmpty(fullPath) && this._paths.Contains(fullPath);

  /// <summary>Remove all files from the collection.</summary>
  public void Clear() => this._paths.Clear();

  /// <summary>Return a snapshot of the current file paths.</summary>
  public IReadOnlySet<string> GetFiles() => this._paths;
}
