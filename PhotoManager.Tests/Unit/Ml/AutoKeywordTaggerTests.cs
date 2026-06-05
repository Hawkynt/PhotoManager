using Hawkynt.PhotoManager.Core.Ml;

namespace Hawkynt.PhotoManager.Tests.Unit.Ml;

[TestFixture]
public class AutoKeywordTaggerTests {
  [Test]
  public void Tag_OrthogonalOneHotVectors_ScoresPerfectMatchAtOneOthersAtZero() {
    // Three orthogonal unit vectors → cosine(self) = 1, cosine(other) = 0.
    var v0 = new float[] { 1, 0, 0 };
    var v1 = new float[] { 0, 1, 0 };
    var v2 = new float[] { 0, 0, 1 };

    var matches = AutoKeywordTagger.Tag(
      imageEmbedding: v1,
      vocabEmbeddings: new[] { v0, v1, v2 },
      words: new[] { "alpha", "beta", "gamma" },
      topK: 5,
      minSimilarity: -1f
    );

    Assert.Multiple(() => {
      Assert.That(matches.Count, Is.EqualTo(3));
      Assert.That(matches[0].Word, Is.EqualTo("beta"));
      Assert.That(matches[0].Score, Is.EqualTo(1f).Within(1e-6));
      Assert.That(matches[1].Score, Is.EqualTo(0f).Within(1e-6));
      Assert.That(matches[2].Score, Is.EqualTo(0f).Within(1e-6));
    });
  }

  [Test]
  public void Tag_TopKCapRespected() {
    var v = new float[] { 1, 0 };
    var vocab = new[] {
      new float[] { 1, 0 },
      new float[] { 0.95f, 0.31f },
      new float[] { 0.9f, 0.43f },
      new float[] { 0.8f, 0.6f },
      new float[] { 0, 1 }
    };
    var words = new[] { "a", "b", "c", "d", "e" };

    var matches = AutoKeywordTagger.Tag(v, vocab, words, topK: 2, minSimilarity: -1f);

    Assert.That(matches.Count, Is.EqualTo(2));
    Assert.That(matches[0].Word, Is.EqualTo("a"));
    Assert.That(matches[1].Word, Is.EqualTo("b"));
  }

  [Test]
  public void Tag_MinSimilarityFilters() {
    var v = new float[] { 1, 0 };
    var vocab = new[] {
      new float[] { 1, 0 },          // 1.0
      new float[] { 0.5f, 0.5f },    // 0.5
      new float[] { 0, 1 }           // 0.0
    };
    var words = new[] { "high", "mid", "low" };

    var matches = AutoKeywordTagger.Tag(v, vocab, words, topK: 10, minSimilarity: 0.45f);

    Assert.Multiple(() => {
      Assert.That(matches.Count, Is.EqualTo(2));
      Assert.That(matches.Any(m => m.Word == "low"), Is.False);
    });
  }

  [Test]
  public void Tag_EmptyVocabulary_ReturnsEmpty() {
    var v = new float[] { 1, 0 };
    var matches = AutoKeywordTagger.Tag(
      imageEmbedding: v,
      vocabEmbeddings: Array.Empty<float[]>(),
      words: Array.Empty<string>(),
      topK: 5,
      minSimilarity: 0f
    );

    Assert.That(matches, Is.Empty);
  }

  [Test]
  public void Tag_DimensionMismatch_RowsAreSkipped() {
    var v = new float[] { 1, 0, 0 };
    var vocab = new[] {
      new float[] { 1, 0 },             // wrong dim
      new float[] { 0, 0, 1 },          // ok
      new float[] { 1, 0, 0 }           // ok, perfect match
    };
    var words = new[] { "bad", "ok1", "ok2" };

    var matches = AutoKeywordTagger.Tag(v, vocab, words, topK: 5, minSimilarity: -1f);

    Assert.Multiple(() => {
      Assert.That(matches.Count, Is.EqualTo(2));
      Assert.That(matches[0].Word, Is.EqualTo("ok2"));
      Assert.That(matches.Any(m => m.Word == "bad"), Is.False);
    });
  }

  [Test]
  public void Cache_RoundTripsBytesIdentically() {
    var rows = new[] {
      new float[] { 0.1f, -0.2f, 0.3f, 0.4f },
      new float[] { -0.5f, 0.6f, -0.7f, 0.8f },
      new float[] { 0.0f, 0.0f, 1.0f, 0.0f }
    };
    var cacheFile = new FileInfo(Path.Combine(Path.GetTempPath(), "vocab-cache-" + Guid.NewGuid().ToString("N") + ".bin"));
    try {
      AutoKeywordTagger.WriteCache(cacheFile, rows);
      var loaded = AutoKeywordTagger.TryReadCache(cacheFile, expectedCount: 3);

      Assert.That(loaded, Is.Not.Null);
      Assert.That(loaded!.Count, Is.EqualTo(3));
      for (var i = 0; i < rows.Length; i++)
        Assert.That(loaded[i], Is.EqualTo(rows[i]).AsCollection);
    } finally {
      if (cacheFile.Exists)
        cacheFile.Delete();
    }
  }

  [Test]
  public void Cache_RowCountMismatch_ReturnsNull() {
    var rows = new[] { new float[] { 1, 2, 3 } };
    var cacheFile = new FileInfo(Path.Combine(Path.GetTempPath(), "vocab-cache-mismatch-" + Guid.NewGuid().ToString("N") + ".bin"));
    try {
      AutoKeywordTagger.WriteCache(cacheFile, rows);
      var loaded = AutoKeywordTagger.TryReadCache(cacheFile, expectedCount: 5);
      Assert.That(loaded, Is.Null);
    } finally {
      if (cacheFile.Exists)
        cacheFile.Delete();
    }
  }

  [Test]
  public void Vocabulary_DefaultLoadsHundredsOfWords() {
    var words = AutoKeywordVocabulary.Default;
    Assert.That(words.Count, Is.GreaterThan(500));
    Assert.That(words.Count, Is.LessThan(900));
    Assert.That(words.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(words.Count),
      "vocabulary entries must be unique (case-insensitive)");
  }

  [Test]
  public void Vocabulary_ParseLines_SkipsCommentsAndBlanks() {
    var parsed = AutoKeywordVocabulary.ParseLines(new[] {
      "# header",
      "",
      "  alpha  ",
      "Beta",
      "beta",          // dupe — case-insensitive
      "# trailing",
      "gamma"
    });

    Assert.That(parsed, Is.EqualTo(new[] { "alpha", "beta", "gamma" }).AsCollection);
  }

  [Test]
  public void Vocabulary_ComputeHash_IsDeterministic() {
    var a = AutoKeywordVocabulary.ComputeHash(new[] { "a", "b", "c" });
    var b = AutoKeywordVocabulary.ComputeHash(new[] { "a", "b", "c" });
    var c = AutoKeywordVocabulary.ComputeHash(new[] { "a", "b", "d" });
    Assert.Multiple(() => {
      Assert.That(a, Is.EqualTo(b));
      Assert.That(a, Is.Not.EqualTo(c));
    });
  }

  [Test]
  public void OnnxClipImageEncoder_NoModel_IsNotAvailable() {
    var missing = new FileInfo(Path.Combine(Path.GetTempPath(), "nonexistent-clip-vision-" + Guid.NewGuid().ToString("N") + ".onnx"));
    using var enc = new OnnxClipImageEncoder(missing);
    Assert.That(enc.IsAvailable, Is.False);
    Assert.That(enc.Embed(missing), Is.Null);
  }

  [Test]
  public void OnnxClipTextEncoder_NoModel_IsNotAvailable() {
    var missing = new FileInfo(Path.Combine(Path.GetTempPath(), "nonexistent-clip-text-" + Guid.NewGuid().ToString("N") + ".onnx"));
    using var enc = new OnnxClipTextEncoder(missing);
    Assert.That(enc.IsAvailable, Is.False);
    Assert.That(enc.EmbedAll(new[] { "test" }), Is.Null);
    Assert.That(enc.EmbedAllCached(new[] { "test" }), Is.Null);
  }
}
