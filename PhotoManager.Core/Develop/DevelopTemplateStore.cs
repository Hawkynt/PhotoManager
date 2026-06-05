using System.Text.Json;

namespace Hawkynt.PhotoManager.Core.Develop;

/// <summary>
/// One reusable develop preset. Just a name + the settings — no preview
/// thumbnail, no auto-update logic. Saved to <c>%AppData%/PhotoManager/develop-templates/</c>
/// as one JSON file per template so users can hand-edit, share, or version-control them.
/// </summary>
public sealed record DevelopTemplate(string Name, DevelopSettings Settings);

/// <summary>
/// Reads / writes <see cref="DevelopTemplate"/>s as JSON files in the app
/// data folder. No database, no synchronization — re-listing reads the
/// directory each call. Good enough at the human scale of "tens of
/// templates" the user will realistically maintain.
/// </summary>
public sealed class DevelopTemplateStore {
  private readonly DirectoryInfo _root;
  private static readonly JsonSerializerOptions JsonOptions = new() {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
  };

  public DevelopTemplateStore() : this(AppDataPaths.SubDirectory("develop-templates")) { }

  public DevelopTemplateStore(DirectoryInfo root) {
    ArgumentNullException.ThrowIfNull(root);
    this._root = root;
    if (!root.Exists)
      root.Create();
  }

  /// <summary>Every template currently on disk, sorted by name (case-insensitive).</summary>
  public IReadOnlyList<DevelopTemplate> List() {
    var files = this._root.EnumerateFiles("*.json").ToList();
    var result = new List<DevelopTemplate>(files.Count);
    foreach (var f in files) {
      try {
        var json = File.ReadAllText(f.FullName);
        var t = JsonSerializer.Deserialize<DevelopTemplate>(json, JsonOptions);
        if (t != null && !string.IsNullOrWhiteSpace(t.Name))
          result.Add(t);
      } catch {
        // Bad / partial JSON — skip rather than fail the whole list.
      }
    }
    result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    return result;
  }

  /// <summary>Writes the template, overwriting any existing entry with the same name.</summary>
  public void Save(DevelopTemplate template) {
    ArgumentNullException.ThrowIfNull(template);
    if (string.IsNullOrWhiteSpace(template.Name))
      throw new ArgumentException("Template name is required.", nameof(template));

    var path = Path.Combine(this._root.FullName, SanitizeFileName(template.Name) + ".json");
    var json = JsonSerializer.Serialize(template, JsonOptions);
    File.WriteAllText(path, json);
  }

  public void Delete(string name) {
    if (string.IsNullOrWhiteSpace(name))
      return;
    var path = Path.Combine(this._root.FullName, SanitizeFileName(name) + ".json");
    if (File.Exists(path))
      File.Delete(path);
  }

  /// <summary>
  /// Map a free-form template name to a safe filename. Replaces invalid
  /// chars and trims length so we don't blow up on emoji-heavy names.
  /// </summary>
  private static string SanitizeFileName(string name) {
    var invalid = Path.GetInvalidFileNameChars();
    var clean = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
    if (clean.Length == 0)
      clean = "template";
    if (clean.Length > 80)
      clean = clean[..80];
    return clean;
  }
}
