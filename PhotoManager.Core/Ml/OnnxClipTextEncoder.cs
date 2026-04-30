using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoManager.Core.Ml;

/// <summary>
/// CLIP / SigLIP text encoder. Encodes vocabulary words once and caches the
/// resulting matrix on disk under
/// <c>AppDataPaths.SubDirectory("ml-cache")/vocab-{model-hash}-{vocab-hash}.bin</c>
/// so subsequent runs skip re-embedding.
///
/// Production CLIP / SigLIP text encoders expect a tokenizer-specific input
/// shape (BPE for CLIP, SentencePiece for SigLIP). To keep this slice
/// dependency-light, the encoder exposes <see cref="ITokenizer"/> as a hook so
/// callers can plug in the right tokenizer for their chosen model. The
/// built-in <see cref="WhitespaceByteTokenizer"/> is a placeholder that's
/// good enough to round-trip cache logic in tests, but a real model needs a
/// real tokenizer to produce useful text embeddings.
/// </summary>
public sealed class OnnxClipTextEncoder : IDisposable {
  public const string DefaultModelFileName = "siglip-text.onnx";
  public const int DefaultMaxTokens = 64;

  public interface ITokenizer {
    /// <summary>Returns int64 token ids padded / truncated to <paramref name="maxTokens"/>.</summary>
    long[] Encode(string text, int maxTokens);

    /// <summary>Stable identifier — included in the cache filename.</summary>
    string Identifier { get; }
  }

  private readonly Lazy<InferenceSession?> _session;
  private readonly ITokenizer _tokenizer;
  private readonly int _maxTokens;
  private readonly string _modelHash;

  public OnnxClipTextEncoder(
    FileInfo? modelFile = null,
    ITokenizer? tokenizer = null,
    int maxTokens = DefaultMaxTokens
  ) {
    var path = modelFile ?? AppDataPaths.ModelFile(DefaultModelFileName);
    this._tokenizer = tokenizer ?? new WhitespaceByteTokenizer();
    this._maxTokens = maxTokens;
    this._modelHash = ComputeModelHash(path);
    this._session = new Lazy<InferenceSession?>(() => TryOpenSession(path));
  }

  public bool IsAvailable => this._session.Value != null;

  /// <summary>
  /// Embeds every word in <paramref name="words"/>, returning one L2-normalised
  /// vector per word. Returns null when the model isn't loaded.
  /// </summary>
  public IReadOnlyList<float[]>? EmbedAll(IReadOnlyList<string> words) {
    ArgumentNullException.ThrowIfNull(words);
    var session = this._session.Value;
    if (session == null)
      return null;

    var rows = new float[words.Count][];
    for (var i = 0; i < words.Count; i++)
      rows[i] = this.RunInference(session, words[i]);
    return rows;
  }

  /// <summary>
  /// Embeds <paramref name="words"/> with a disk cache. The cache key combines
  /// the model file's hash and the vocabulary hash so any change invalidates.
  /// </summary>
  public IReadOnlyList<float[]>? EmbedAllCached(IReadOnlyList<string> words) {
    ArgumentNullException.ThrowIfNull(words);
    if (!this.IsAvailable)
      return null;

    var cacheFile = this.ResolveCacheFile(words);
    var cached = AutoKeywordTagger.TryReadCache(cacheFile, words.Count);
    if (cached != null)
      return cached;

    var fresh = this.EmbedAll(words);
    if (fresh != null && fresh.Count > 0) {
      try {
        AutoKeywordTagger.WriteCache(cacheFile, fresh);
      } catch {
        // Cache write failures aren't fatal — embedding still works in-memory.
      }
    }
    return fresh;
  }

  public FileInfo ResolveCacheFile(IReadOnlyList<string> words) {
    var vocabHash = AutoKeywordVocabulary.ComputeHash(words);
    var name = $"vocab-{this._modelHash}-{this._tokenizer.Identifier}-{vocabHash}.bin";
    return new FileInfo(Path.Combine(AppDataPaths.SubDirectory("ml-cache").FullName, name));
  }

  private float[] RunInference(InferenceSession session, string word) {
    var tokens = this._tokenizer.Encode(word, this._maxTokens);
    var inputName = session.InputMetadata.Keys.First();
    var input = new DenseTensor<long>(tokens, [1, this._maxTokens]);

    using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
    var output = results.First().AsTensor<float>().ToArray();
    return AutoKeywordTagger.L2Normalize(output);
  }

  private static InferenceSession? TryOpenSession(FileInfo modelFile) {
    try {
      if (!modelFile.Exists)
        return null;
      return new InferenceSession(modelFile.FullName);
    } catch {
      return null;
    }
  }

  private static string ComputeModelHash(FileInfo file) {
    if (!file.Exists)
      return "nomodel";
    try {
      using var sha = System.Security.Cryptography.SHA1.Create();
      using var fs = File.OpenRead(file.FullName);
      var hash = sha.ComputeHash(fs);
      return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    } catch {
      return "nomodel";
    }
  }

  public void Dispose() {
    if (this._session.IsValueCreated)
      this._session.Value?.Dispose();
  }

  /// <summary>
  /// Placeholder tokenizer: lower-cases, splits on whitespace, encodes each
  /// resulting token as the sum of its UTF-8 bytes mod 32000 (a typical
  /// vocabulary size). This is NOT a real BPE / SentencePiece tokenizer; it
  /// exists so the encoder is wireable end-to-end without binding to a
  /// specific tokenizer library. Callers running a real CLIP / SigLIP model
  /// must inject a matching tokenizer.
  /// </summary>
  public sealed class WhitespaceByteTokenizer : ITokenizer {
    public string Identifier => "wsbyte";

    public long[] Encode(string text, int maxTokens) {
      var ids = new long[maxTokens];
      if (string.IsNullOrWhiteSpace(text))
        return ids;

      var lower = text.ToLowerInvariant();
      var parts = lower.Split([' ', '\t', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
      var write = 0;
      foreach (var part in parts) {
        if (write >= maxTokens)
          break;
        long acc = 0;
        var bytes = System.Text.Encoding.UTF8.GetBytes(part);
        foreach (var b in bytes)
          acc = (acc * 257 + b) & 0xFFFF;
        ids[write++] = acc % 32000;
      }
      return ids;
    }
  }
}
