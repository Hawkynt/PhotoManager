using System.Security.Cryptography;

namespace PhotoManager.Core.Ml;

/// <summary>
/// Disk-based cache for per-image CLIP/SigLIP vision embeddings. When users
/// re-scan the same folder (e.g. after adjusting top-K or min-similarity) the
/// expensive ONNX vision inference is skipped for images that haven't changed.
///
/// Cache directory: <c>AppDataPaths.SubDirectory("ml-cache")</c>
///
/// Key: SHA-256 of (file size as 8 LE bytes + first 64 KB of file content).
/// This is fast — only a small prefix is read — and stable unless the file is
/// re-saved.
///
/// Cache file naming: <c>img-{hashHex}.emb</c> containing the raw float32
/// embedding vector (no header, just <c>dim * 4</c> bytes).
/// </summary>
public sealed class ImageEmbeddingCache {
  private const int PrefixBytes = 64 * 1024;  // 64 KB

  private readonly DirectoryInfo _cacheDir;

  public ImageEmbeddingCache(DirectoryInfo? cacheDir = null) {
    this._cacheDir = cacheDir ?? AppDataPaths.SubDirectory("ml-cache");
    this._cacheDir.Create();
  }

  /// <summary>
  /// Returns a cached embedding for <paramref name="file"/>, or null when no
  /// cache entry exists or the file can't be read.
  /// </summary>
  public float[]? TryGet(FileInfo file) {
    ArgumentNullException.ThrowIfNull(file);
    try {
      var path = this.CachePath(file);
      if (!path.Exists || path.Length < 4)
        return null;

      var bytes = File.ReadAllBytes(path.FullName);
      if (bytes.Length % sizeof(float) != 0)
        return null;

      var dim = bytes.Length / sizeof(float);
      var result = new float[dim];
      Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
      return result;
    } catch {
      return null;
    }
  }

  /// <summary>
  /// Stores <paramref name="embedding"/> to disk for <paramref name="file"/>.
  /// Failures are silently swallowed — the cache is a performance optimisation,
  /// not a correctness requirement.
  /// </summary>
  public void Put(FileInfo file, float[] embedding) {
    ArgumentNullException.ThrowIfNull(file);
    ArgumentNullException.ThrowIfNull(embedding);
    try {
      var path = this.CachePath(file);
      path.Directory?.Create();
      var bytes = new byte[embedding.Length * sizeof(float)];
      Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
      File.WriteAllBytes(path.FullName, bytes);
    } catch {
      // Cache write failures are non-fatal.
    }
  }

  private FileInfo CachePath(FileInfo file) {
    var hash = ComputeContentHash(file);
    return new FileInfo(Path.Combine(this._cacheDir.FullName, $"img-{hash}.emb"));
  }

  /// <summary>
  /// SHA-256 of (file size as 8 LE bytes + first 64 KB of content). Fast and
  /// stable for typical photo files.
  /// </summary>
  internal static string ComputeContentHash(FileInfo file) {
    using var sha = SHA256.Create();
    // Feed the file size first so two files with identical leading bytes but
    // different lengths produce different hashes.
    var sizeBytes = BitConverter.GetBytes(file.Length);
    sha.TransformBlock(sizeBytes, 0, sizeBytes.Length, null, 0);

    try {
      using var fs = File.OpenRead(file.FullName);
      var buffer = new byte[PrefixBytes];
      var read = fs.Read(buffer, 0, buffer.Length);
      sha.TransformFinalBlock(buffer, 0, read);
    } catch {
      sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    }

    return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
  }
}
