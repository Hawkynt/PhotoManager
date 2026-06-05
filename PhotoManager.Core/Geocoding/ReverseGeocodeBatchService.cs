using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.Core.Geocoding;

/// <summary>
/// Outcome of reverse-geocoding one file. The UI uses this to summarise the
/// run ("Resolved 12 of 15; 2 had no GPS, 1 lookup failed") and to decide
/// which rows in the grid need their search index rebuilt.
/// </summary>
public sealed record ReverseGeocodeBatchEntry(
  FileInfo File,
  ReverseGeocodeBatchOutcome Outcome,
  GeocodingResult? Result = null,
  string? ErrorMessage = null
);

public enum ReverseGeocodeBatchOutcome {
  Resolved,         // geocoder returned a result and we wrote it
  AlreadyTagged,    // file already has location text — skipped per options
  NoGps,            // photo has no GPS to look up
  NoMatch,          // geocoder returned nothing for the coordinates
  Error             // exception during read / lookup / write
}

public sealed record ReverseGeocodeBatchOptions {
  /// <summary>
  /// When true, skip files that already have any place text. Matches what
  /// the per-photo button does: <c>PickResolveField</c> only fills holes.
  /// </summary>
  public bool OnlyFillEmptyFields { get; init; } = true;

  /// <summary>
  /// When true, don't actually write — just return what would happen.
  /// Useful for the preview step in the dialog before the user commits.
  /// </summary>
  public bool DryRun { get; init; }
}

/// <summary>
/// Iterates a list of files, reads each one's metadata, asks the geocoder
/// for an address, and writes the result back via the metadata writer.
/// Sequential by design — Nominatim's 1 req/sec rate limit is enforced
/// inside the geocoder itself, so parallelising here would just queue.
/// </summary>
public sealed class ReverseGeocodeBatchService {
  private readonly IReverseGeocoder _geocoder;
  private readonly IMetadataReader _reader;
  private readonly IMetadataWriter _writer;

  public ReverseGeocodeBatchService(IReverseGeocoder geocoder, IMetadataReader reader, IMetadataWriter writer) {
    ArgumentNullException.ThrowIfNull(geocoder);
    ArgumentNullException.ThrowIfNull(reader);
    ArgumentNullException.ThrowIfNull(writer);
    this._geocoder = geocoder;
    this._reader = reader;
    this._writer = writer;
  }

  public async Task<IReadOnlyList<ReverseGeocodeBatchEntry>> RunAsync(
    IReadOnlyList<FileInfo> files,
    ReverseGeocodeBatchOptions? options = null,
    IProgress<FileInfo>? progress = null,
    CancellationToken cancellationToken = default
  ) {
    ArgumentNullException.ThrowIfNull(files);
    options ??= new ReverseGeocodeBatchOptions();
    var results = new List<ReverseGeocodeBatchEntry>(files.Count);

    foreach (var file in files) {
      cancellationToken.ThrowIfCancellationRequested();
      progress?.Report(file);

      try {
        var metadata = await this._reader.ReadAsync(file, cancellationToken);
        if (metadata.Gps is not { IsValid: true } gps) {
          results.Add(new ReverseGeocodeBatchEntry(file, ReverseGeocodeBatchOutcome.NoGps));
          continue;
        }

        var result = await this._geocoder.ResolveAsync(gps, cancellationToken);
        if (result is not { HasAny: true }) {
          results.Add(new ReverseGeocodeBatchEntry(file, ReverseGeocodeBatchOutcome.NoMatch, result));
          continue;
        }

        var edit = BuildEdit(metadata, result, options.OnlyFillEmptyFields);
        var willWrite = HasAnyField(edit);
        if (!willWrite) {
          results.Add(new ReverseGeocodeBatchEntry(file, ReverseGeocodeBatchOutcome.AlreadyTagged, result));
          continue;
        }

        if (!options.DryRun)
          await this._writer.ApplyAsync(file, edit, cancellationToken);

        results.Add(new ReverseGeocodeBatchEntry(file, ReverseGeocodeBatchOutcome.Resolved, result));
      } catch (OperationCanceledException) {
        throw;
      } catch (Exception ex) {
        results.Add(new ReverseGeocodeBatchEntry(file, ReverseGeocodeBatchOutcome.Error, ErrorMessage: ex.Message));
      }
    }

    return results;
  }

  /// <summary>
  /// Maps a geocoder result onto a <see cref="MetadataEdit"/>, honouring the
  /// "only fill empty fields" flag exactly the way the per-photo Resolve
  /// Address button does. Public so the dialog can preview the patch.
  /// </summary>
  public static MetadataEdit BuildEdit(FullMetadata existing, GeocodingResult result, bool onlyFillEmptyFields) {
    return new MetadataEdit {
      Location    = PickField(existing.Location,    result.Location,    onlyFillEmptyFields),
      City        = PickField(existing.City,        result.City,        onlyFillEmptyFields),
      State       = PickField(existing.State,       result.State,       onlyFillEmptyFields),
      Country     = PickField(existing.Country,     result.Country,     onlyFillEmptyFields),
      CountryCode = PickField(existing.CountryCode, result.CountryCode, onlyFillEmptyFields)
    };
  }

  private static Optional<string?> PickField(string? existing, string? resolved, bool onlyFillEmptyFields) {
    if (string.IsNullOrEmpty(resolved))
      return default;
    if (onlyFillEmptyFields && !string.IsNullOrEmpty(existing))
      return default;
    return Optional<string?>.Set(resolved);
  }

  private static bool HasAnyField(MetadataEdit edit) =>
    edit.Location.HasValue || edit.City.HasValue || edit.State.HasValue
    || edit.Country.HasValue || edit.CountryCode.HasValue;
}
