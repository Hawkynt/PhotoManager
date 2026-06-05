using System.Text.Json;

namespace Hawkynt.PhotoManager.Core.Geocoding;

/// <summary>
/// JSON-backed store for <see cref="MapBookmark"/>s. Single file, one array,
/// loaded on demand and rewritten atomically on every mutation. The default
/// file lives under <see cref="AppDataPaths"/> so it survives reinstalls but
/// stays out of the photo library — bookmarks are user preferences, not
/// per-photo metadata. Tests inject their own <see cref="FileInfo"/>.
///
/// Tolerates a missing file (returns empty), a corrupt file (returns empty,
/// next save overwrites), and out-of-process edits (each call re-reads).
/// No locking — Photo Manager is single-process.
/// </summary>
public sealed class MapBookmarkStore {
  private const string DefaultFileName = "map-bookmarks.json";

  private static readonly JsonSerializerOptions JsonOptions = new() {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
  };

  private readonly FileInfo _file;

  public MapBookmarkStore() : this(DefaultFile()) { }

  public MapBookmarkStore(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    this._file = file;
  }

  public static FileInfo DefaultFile() =>
    new(Path.Combine(AppDataPaths.Root().FullName, DefaultFileName));

  /// <summary>Loads bookmarks from disk. Empty list when no file exists or the file is unreadable.</summary>
  public IReadOnlyList<MapBookmark> Load() {
    this._file.Refresh();
    if (!this._file.Exists)
      return Array.Empty<MapBookmark>();

    try {
      var json = File.ReadAllText(this._file.FullName);
      if (string.IsNullOrWhiteSpace(json))
        return Array.Empty<MapBookmark>();

      var parsed = JsonSerializer.Deserialize<List<MapBookmark>>(json, JsonOptions);
      if (parsed is null)
        return Array.Empty<MapBookmark>();

      // Strip empty / malformed entries so the UI never has to defend against
      // them. Bookmark names are required; coordinates must be plausible.
      return parsed
        .Where(b => !string.IsNullOrWhiteSpace(b.Name))
        .Where(b => b.Latitude is >= -90.0 and <= 90.0)
        .Where(b => b.Longitude is >= -180.0 and <= 180.0)
        .ToList();
    } catch {
      return Array.Empty<MapBookmark>();
    }
  }

  /// <summary>Replaces the on-disk list with <paramref name="bookmarks"/>.</summary>
  public void Save(IEnumerable<MapBookmark> bookmarks) {
    ArgumentNullException.ThrowIfNull(bookmarks);

    var list = bookmarks.ToList();
    if (this._file.Directory is { } dir && !dir.Exists)
      dir.Create();

    var json = JsonSerializer.Serialize(list, JsonOptions);

    // Write to a temp file and replace, so a crash mid-write can't leave a
    // half-formed JSON document where the bookmarks file used to be.
    var tempPath = this._file.FullName + ".tmp";
    File.WriteAllText(tempPath, json);
    if (File.Exists(this._file.FullName))
      File.Replace(tempPath, this._file.FullName, destinationBackupFileName: null);
    else
      File.Move(tempPath, this._file.FullName);
  }
}
