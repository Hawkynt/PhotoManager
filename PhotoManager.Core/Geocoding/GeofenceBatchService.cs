using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Geocoding;

/// <summary>
/// Walks a list of files, looks up each photo's GPS, and applies the first
/// matching <see cref="MapBookmark"/>'s place fields via
/// <see cref="MapBookmarkApplier"/>. Mirrors the dry-run + cancellable +
/// progress-reporting shape of the reverse-geocode batch flow so the UI
/// can host both with the same plumbing.
/// </summary>
public sealed class GeofenceBatchService(
  IMetadataReader reader,
  IMetadataWriter writer
) {
  public sealed record FileOutcome(
    FileInfo File,
    IReadOnlyList<MapBookmark> Matches,
    bool WouldWrite,
    string? Error
  );

  public sealed record Progress(int Processed, int Total, FileOutcome? Latest);

  public sealed record Options(bool DryRun = false, bool OnlyFillEmpty = true);

  public async Task<IReadOnlyList<FileOutcome>> RunAsync(
    IReadOnlyList<FileInfo> files,
    IReadOnlyList<MapBookmark> bookmarks,
    Options options,
    IProgress<Progress>? progress = null,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(files);
    ArgumentNullException.ThrowIfNull(bookmarks);
    ArgumentNullException.ThrowIfNull(options);

    var outcomes = new List<FileOutcome>(files.Count);
    var total = files.Count;
    var done = 0;

    foreach (var file in files) {
      cancellationToken.ThrowIfCancellationRequested();

      FileOutcome outcome;
      try {
        if (!file.Exists) {
          outcome = new FileOutcome(file, Array.Empty<MapBookmark>(), WouldWrite: false, Error: "File not found");
        } else {
          var metadata = await reader.ReadAsync(file, cancellationToken);
          if (metadata.Gps is not { IsValid: true } gps) {
            outcome = new FileOutcome(file, Array.Empty<MapBookmark>(), WouldWrite: false, Error: null);
          } else {
            var matches = GeofenceMatcher.MatchAll(bookmarks, gps);
            if (matches.Count == 0) {
              outcome = new FileOutcome(file, matches, WouldWrite: false, Error: null);
            } else {
              var edit = MapBookmarkApplier.BuildEdit(matches[0], metadata, options.OnlyFillEmpty);
              var wouldWrite = HasAnyValue(edit);
              if (wouldWrite && !options.DryRun)
                await writer.ApplyAsync(file, edit, cancellationToken);
              outcome = new FileOutcome(file, matches, wouldWrite, Error: null);
            }
          }
        }
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        outcome = new FileOutcome(file, Array.Empty<MapBookmark>(), WouldWrite: false, Error: ex.Message);
      }

      outcomes.Add(outcome);
      done++;
      progress?.Report(new Progress(done, total, outcome));
    }

    return outcomes;
  }

  private static bool HasAnyValue(MetadataEdit edit) =>
    edit.Location.HasValue ||
    edit.City.HasValue ||
    edit.State.HasValue ||
    edit.Country.HasValue ||
    edit.CountryCode.HasValue ||
    edit.Keywords.HasValue;
}
