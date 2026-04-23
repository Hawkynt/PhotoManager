using PhotoManager.Core.Interfaces;
using PhotoManager.Core.Metadata;
using PhotoManager.Core.Regions;

namespace PhotoManager.Core.Faces;

/// <summary>
/// A single face region found on disk during a library scan, paired with
/// the file it came from. The full <see cref="TaggedRegion"/> is retained
/// so callers can propagate names, re-render boxes, or write updated
/// metadata back through the existing writer pipeline.
/// </summary>
public sealed record ScannedFace(FileInfo File, TaggedRegion Region) {
  public string? Name => this.Region.Label;
  public float[]? Embedding => this.Region.Embedding;
  public bool HasEmbedding => this.Region.Embedding is { Length: > 0 };
}

/// <summary>
/// Walks a directory tree, reads each supported image's metadata via
/// <see cref="IMetadataReader"/>, and yields every face region. Picasa-style
/// clustering builds on top of this — the scanner is IO-bound, the cluster
/// index in <see cref="FaceClusterIndex"/> is the CPU-bound step.
///
/// Deliberately does not persist anything. The architectural rule is
/// "XMP + sidecar are the truth"; rebuild the in-memory index on demand.
/// </summary>
public sealed class LibraryFaceScanner {
  private readonly IMetadataReader _reader;
  private readonly ISupportedFormatsService _formats;

  public LibraryFaceScanner(IMetadataReader reader, ISupportedFormatsService formats) {
    ArgumentNullException.ThrowIfNull(reader);
    ArgumentNullException.ThrowIfNull(formats);
    this._reader = reader;
    this._formats = formats;
  }

  /// <summary>
  /// Scans <paramref name="root"/> recursively (by default) and reports every
  /// face-category region. <paramref name="progress"/> receives the currently
  /// processing file; <paramref name="onlyEmbedded"/> filters to faces that
  /// carry a persisted embedding (the ones clustering can use).
  /// </summary>
  public async Task<IReadOnlyList<ScannedFace>> ScanAsync(
    DirectoryInfo root,
    bool recursive = true,
    bool onlyEmbedded = false,
    IProgress<FileInfo>? progress = null,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(root);
    if (!root.Exists)
      return Array.Empty<ScannedFace>();

    var extensions = (await this._formats.GetSupportedExtensionsWithoutWildcardsAsync())
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
    var results = new List<ScannedFace>();

    foreach (var file in root.EnumerateFiles("*", option)) {
      cancellationToken.ThrowIfCancellationRequested();
      if (!extensions.Contains(file.Extension))
        continue;

      progress?.Report(file);

      FullMetadata metadata;
      try {
        metadata = await this._reader.ReadAsync(file, cancellationToken);
      } catch (OperationCanceledException) {
        throw;
      } catch {
        // Skip unreadable file — a bad metadata block shouldn't abort the whole scan.
        continue;
      }

      foreach (var region in metadata.Regions) {
        if (region.Category != RegionCategory.Person)
          continue;
        if (onlyEmbedded && (region.Embedding is null || region.Embedding.Length == 0))
          continue;
        results.Add(new ScannedFace(file, region));
      }
    }

    return results;
  }
}
