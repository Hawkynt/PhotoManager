using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoManager.Core.Faces;

/// <summary>
/// Persistent mapping from person name → one or more reference embedding
/// vectors. Stored as a single JSON file in the app-data directory (per the
/// no-local-DB principle: this is app-scoped state, not library truth — the
/// file can be deleted without losing per-photo face tags in XMP sidecars).
///
/// Match is by cosine similarity against any reference embedding for the
/// person; threshold is caller-configurable (0.5-0.6 typical for ArcFace,
/// tighter for MobileFaceNet).
/// </summary>
public sealed class PeopleRegistry {
  private const string DefaultFileName = "people.json";

  private readonly FileInfo _file;
  private Dictionary<string, List<float[]>> _people;

  public PeopleRegistry(FileInfo? file = null) {
    this._file = file ?? new FileInfo(Path.Combine(AppDataPaths.Root().FullName, DefaultFileName));
    this._people = LoadFromDisk(this._file);
  }

  public IEnumerable<string> KnownNames => this._people.Keys;

  public int EmbeddingCount(string name)
    => this._people.TryGetValue(name, out var list) ? list.Count : 0;

  public void AddReference(string name, float[] embedding) {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    ArgumentNullException.ThrowIfNull(embedding);
    if (embedding.Length == 0)
      throw new ArgumentException("Embedding must have at least one dimension.", nameof(embedding));

    if (!this._people.TryGetValue(name, out var list)) {
      list = new List<float[]>();
      this._people[name] = list;
    }

    list.Add((float[])embedding.Clone());
    this.Save();
  }

  public void Remove(string name) {
    if (this._people.Remove(name))
      this.Save();
  }

  /// <summary>
  /// Returns the registered name whose closest reference has the highest
  /// cosine similarity against <paramref name="embedding"/>, provided that
  /// similarity meets <paramref name="minSimilarity"/>. Returns null if no
  /// registered person is close enough.
  /// </summary>
  public string? FindMatch(float[] embedding, float minSimilarity = 0.55f) {
    ArgumentNullException.ThrowIfNull(embedding);

    string? best = null;
    var bestSimilarity = minSimilarity;

    foreach (var (name, references) in this._people) {
      foreach (var reference in references) {
        var sim = CosineSimilarity(embedding, reference);
        if (sim <= bestSimilarity)
          continue;
        bestSimilarity = sim;
        best = name;
      }
    }

    return best;
  }

  public void Save() {
    this._file.Directory?.Create();
    var snapshot = this._people.ToDictionary(
      kv => kv.Key,
      kv => kv.Value.ToArray()
    );
    var json = JsonSerializer.Serialize(snapshot, JsonOptions);
    File.WriteAllText(this._file.FullName, json);
  }

  internal static float CosineSimilarity(float[] a, float[] b) {
    if (a.Length != b.Length)
      throw new ArgumentException($"Embedding dimension mismatch: {a.Length} vs {b.Length}.");

    double dot = 0, normA = 0, normB = 0;
    for (var i = 0; i < a.Length; i++) {
      dot += a[i] * b[i];
      normA += a[i] * a[i];
      normB += b[i] * b[i];
    }

    if (normA <= 0 || normB <= 0)
      return 0f;

    return (float)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)));
  }

  private static Dictionary<string, List<float[]>> LoadFromDisk(FileInfo file) {
    // Use live path-based check rather than FileInfo.Exists which is cached
    // from the FileInfo's construction time and may be stale.
    if (!File.Exists(file.FullName))
      return new Dictionary<string, List<float[]>>(StringComparer.Ordinal);

    try {
      var json = File.ReadAllText(file.FullName);
      var parsed = JsonSerializer.Deserialize<Dictionary<string, float[][]>>(json, JsonOptions);
      if (parsed == null)
        return new Dictionary<string, List<float[]>>(StringComparer.Ordinal);

      return parsed.ToDictionary(
        kv => kv.Key,
        kv => kv.Value.ToList(),
        StringComparer.Ordinal
      );
    } catch {
      // Corrupted file — start fresh rather than propagate. Face tags in XMP
      // are the source of truth; the registry rebuilds as the user re-tags.
      return new Dictionary<string, List<float[]>>(StringComparer.Ordinal);
    }
  }

  private static readonly JsonSerializerOptions JsonOptions = new() {
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };
}
