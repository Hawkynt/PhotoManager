using System.Reflection;

namespace PhotoManager.Core.Ml;

/// <summary>
/// Default vocabulary loaded from the embedded <c>AutoKeywordVocabulary.txt</c>
/// resource. The list is organised in source-control as one word per line so we
/// don't ship a giant compile-time string literal — the .txt is generated at
/// scaffold time and embedded via <c>&lt;EmbeddedResource&gt;</c>.
///
/// Buckets covered: subjects, places, scenes, weather/light, seasons/time,
/// objects (incl. colour×object cross-terms), activities and aesthetics. Total
/// is intentionally kept under ~800 words so vocabulary embedding stays fast on
/// first run and stable for cache invalidation.
/// </summary>
public static class AutoKeywordVocabulary {
  private const string ResourceName = "PhotoManager.Core.Ml.AutoKeywordVocabulary.txt";

  private static readonly Lazy<IReadOnlyList<string>> _default = new(LoadEmbedded);

  public static IReadOnlyList<string> Default => _default.Value;

  /// <summary>
  /// Reads <paramref name="file"/> one word per line, skipping blank lines and
  /// lines starting with <c>#</c> so users can drop comments into a custom
  /// vocabulary. Whitespace is trimmed and the result is lower-cased.
  /// </summary>
  public static IReadOnlyList<string> LoadFromFile(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    if (!file.Exists)
      return Array.Empty<string>();

    var lines = File.ReadAllLines(file.FullName);
    return ParseLines(lines);
  }

  internal static IReadOnlyList<string> ParseLines(IEnumerable<string> lines) {
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var result = new List<string>();
    foreach (var raw in lines) {
      if (raw is null)
        continue;
      var trimmed = raw.Trim();
      if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        continue;
      var lower = trimmed.ToLowerInvariant();
      if (seen.Add(lower))
        result.Add(lower);
    }
    return result;
  }

  private static IReadOnlyList<string> LoadEmbedded() {
    var asm = typeof(AutoKeywordVocabulary).Assembly;
    using var stream = asm.GetManifestResourceStream(ResourceName);
    if (stream == null)
      return Array.Empty<string>();

    using var reader = new StreamReader(stream);
    var lines = new List<string>();
    while (reader.ReadLine() is { } line)
      lines.Add(line);
    return ParseLines(lines);
  }

  /// <summary>
  /// Stable hash of the vocabulary contents — used as part of the cache file
  /// name so a vocabulary change invalidates the cached embeddings.
  /// </summary>
  public static string ComputeHash(IReadOnlyList<string> words) {
    ArgumentNullException.ThrowIfNull(words);
    using var sha = System.Security.Cryptography.SHA1.Create();
    foreach (var w in words) {
      var bytes = System.Text.Encoding.UTF8.GetBytes(w + "\n");
      sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }
    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    var hash = sha.Hash!;
    return Convert.ToHexString(hash, 0, Math.Min(8, hash.Length)).ToLowerInvariant();
  }
}
