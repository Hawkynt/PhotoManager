namespace PhotoManager.Core.Ml;

/// <summary>
/// Pure-logic cosine-similarity ranker. Given an image embedding and a set of
/// vocabulary embeddings, returns the top-K vocabulary words by similarity,
/// filtered by a minimum-similarity threshold.
///
/// All embeddings are assumed to be L2-normalised by the caller (the ONNX
/// encoders normalise their output). With unit vectors, cosine similarity
/// reduces to a plain dot product.
/// </summary>
public static class AutoKeywordTagger {
  /// <summary>
  /// Ranks <paramref name="words"/> by cosine similarity between
  /// <paramref name="imageEmbedding"/> and each <paramref name="vocabEmbeddings"/>.
  /// Returns the top <paramref name="topK"/> matches whose score is at or above
  /// <paramref name="minSimilarity"/>, in descending similarity order.
  /// </summary>
  public static IReadOnlyList<(string Word, float Score)> Tag(
    float[] imageEmbedding,
    IReadOnlyList<float[]> vocabEmbeddings,
    IReadOnlyList<string> words,
    int topK = 5,
    float minSimilarity = 0.22f
  ) {
    ArgumentNullException.ThrowIfNull(imageEmbedding);
    ArgumentNullException.ThrowIfNull(vocabEmbeddings);
    ArgumentNullException.ThrowIfNull(words);

    if (vocabEmbeddings.Count == 0 || words.Count == 0 || imageEmbedding.Length == 0)
      return Array.Empty<(string, float)>();

    var count = Math.Min(vocabEmbeddings.Count, words.Count);
    var scored = new List<(string Word, float Score)>(count);
    for (var i = 0; i < count; i++) {
      var v = vocabEmbeddings[i];
      if (v == null || v.Length != imageEmbedding.Length)
        continue;
      var score = Dot(imageEmbedding, v);
      if (score < minSimilarity)
        continue;
      scored.Add((words[i], score));
    }

    scored.Sort((a, b) => b.Score.CompareTo(a.Score));
    if (topK > 0 && scored.Count > topK)
      scored.RemoveRange(topK, scored.Count - topK);
    return scored;
  }

  /// <summary>L2-normalises <paramref name="vector"/> in place-style (returns a fresh array).</summary>
  public static float[] L2Normalize(float[] vector) {
    ArgumentNullException.ThrowIfNull(vector);
    double sumOfSquares = 0;
    for (var i = 0; i < vector.Length; i++)
      sumOfSquares += vector[i] * vector[i];

    var magnitude = Math.Sqrt(sumOfSquares);
    if (magnitude <= 1e-9)
      return vector;

    var result = new float[vector.Length];
    for (var i = 0; i < vector.Length; i++)
      result[i] = (float)(vector[i] / magnitude);
    return result;
  }

  private static float Dot(float[] a, float[] b) {
    double sum = 0;
    for (var i = 0; i < a.Length; i++)
      sum += a[i] * b[i];
    return (float)sum;
  }

  /// <summary>
  /// Writes a vocabulary-embedding cache: 4-byte magic, int32 count, int32 dim,
  /// float32 matrix in row-major order. Returns true on success.
  /// </summary>
  public static void WriteCache(FileInfo target, IReadOnlyList<float[]> embeddings) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(embeddings);

    target.Directory?.Create();
    using var fs = File.Create(target.FullName);
    using var bw = new BinaryWriter(fs);
    bw.Write(0x434C4956);  // 'VILC' = "Vocab embedding cache"
    bw.Write(embeddings.Count);
    var dim = embeddings.Count > 0 ? embeddings[0].Length : 0;
    bw.Write(dim);
    foreach (var row in embeddings) {
      if (row.Length != dim)
        throw new InvalidOperationException("Cache rows must all share the same dimensionality.");
      for (var i = 0; i < dim; i++)
        bw.Write(row[i]);
    }
  }

  /// <summary>
  /// Reads a cache written by <see cref="WriteCache"/>. Returns null if the
  /// file is missing, malformed, or has a row count that disagrees with
  /// <paramref name="expectedCount"/>.
  /// </summary>
  public static IReadOnlyList<float[]>? TryReadCache(FileInfo source, int expectedCount) {
    ArgumentNullException.ThrowIfNull(source);
    if (!source.Exists)
      return null;

    try {
      using var fs = File.OpenRead(source.FullName);
      using var br = new BinaryReader(fs);
      var magic = br.ReadInt32();
      if (magic != 0x434C4956)
        return null;
      var count = br.ReadInt32();
      var dim = br.ReadInt32();
      if (count != expectedCount || count <= 0 || dim <= 0)
        return null;

      var rows = new float[count][];
      for (var i = 0; i < count; i++) {
        var row = new float[dim];
        for (var j = 0; j < dim; j++)
          row[j] = br.ReadSingle();
        rows[i] = row;
      }
      return rows;
    } catch {
      return null;
    }
  }
}
