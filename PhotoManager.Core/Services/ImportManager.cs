using System.Diagnostics;
using PhotoManager.Core.Enums;
using PhotoManager.Core.Interfaces;
using PhotoManager.Core.Models;
using PhotoManager.Core.Utilities;

namespace PhotoManager.Core.Services;

public class ImportManager(IDateTimeParser? dateTimeParser = null, IFileOrganizer? fileOrganizer = null, ISupportedFormatsService? supportedFormatsService = null)
  : IImportManager {
  private readonly IDateTimeParser _dateTimeParser = dateTimeParser ?? new DateTimeParser();
  private readonly IFileOrganizer _fileOrganizer = fileOrganizer ?? new FileOrganizer();
  private readonly ISupportedFormatsService _supportedFormatsService = supportedFormatsService ?? new SupportedFormatsService();

  public async IAsyncEnumerable<FileToImport> EnumerateDirectory(DirectoryInfo root, bool recursive) {
    ThrowHelpers.ThrowIfDirectoryNotExists(root);

    var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

    foreach (var file in root.EnumerateFiles("*", searchOption).OrderBy(i => i.Name)) {
      if (this._supportedFormatsService.IsExtensionSupported(file.Extension)) {
        yield return new FileToImport(file);
        await Task.Yield();
      }
    }
  }

  public async IAsyncEnumerable<(DateTimeSource, DateTime)> EnumerateDateTimes(FileToImport fileToImport) {
    yield return (DateTimeSource.FileCreatedAt, await fileToImport.GetFileCreatedAt());
    yield return (DateTimeSource.FileModifiedAt, await fileToImport.GetFileLastWrittenAt());

    await foreach (var date in fileToImport.GetExifIfd0DateAsync())
      yield return (DateTimeSource.ExifIfd0, date);

    await foreach (var date in fileToImport.GetExifSubIfdDateAsync())
      yield return (DateTimeSource.ExifSubIfd, date);

    await foreach (var date in fileToImport.GetGpsDateAsync())
      yield return (DateTimeSource.Gps, date);

    var settings = new ImportSettings(); // Use default settings for now
    await foreach (var date in this._dateTimeParser.ParseDateFromFileName(fileToImport, settings))
      yield return (DateTimeSource.FileName, date);
  }

  public async Task<DateTime?> GetMostLogicalCreationDateAsync(FileToImport fileToImport) {
    var settings = new ImportSettings();
    var currentDateTime = DateTime.UtcNow;

    DateTime? gpsDate = null;
    DateTime? exifDate = null;

    var dates = new List<(DateTimeSource, DateTime)>();
    await foreach (var (source, dateTime) in this.EnumerateDateTimes(fileToImport))
      if (!settings.DefaultDatesToIgnore.Contains(dateTime) &&
          dateTime >= settings.MinimumValidDate &&
          dateTime <= currentDateTime) {
        
        switch (source) {
          case DateTimeSource.Gps when gpsDate == null || dateTime > gpsDate.Value:
            gpsDate = dateTime;
            break;
          case DateTimeSource.ExifSubIfd when (exifDate == null || dateTime > exifDate.Value):
            exifDate = dateTime;
            break;
        }

        dates.Add((source, dateTime));
      }

    // Special handling for GPS vs EXIF dates
    if (gpsDate.HasValue && exifDate.HasValue) {
      if (exifDate.Value > gpsDate.Value && (exifDate.Value - gpsDate.Value).TotalHours <= 1)
        return exifDate;

      return gpsDate;
    }

    // Sort by reliability
    var sortedDates = dates
      .OrderByDescending(d => GetSourceReliability(d.Item1))
      .ThenByDescending(d => d.Item2)
      .Select(d => d.Item2)
      .ToList();

    return sortedDates.FirstOrDefault();
  }

  public async Task<(DateTime? Date, DateTimeSource Source)> GetMostLogicalCreationDateWithSourceAsync(FileToImport fileToImport) {
    ArgumentNullException.ThrowIfNull(fileToImport);
    ThrowHelpers.ThrowIfFileNotExists(fileToImport.Source);
    var settings = new ImportSettings();
    var currentDateTime = DateTime.UtcNow;

    DateTime? gpsDate = null;
    DateTime? exifDate = null;
    var gpsSource = DateTimeSource.Unknown;
    var exifSource = DateTimeSource.Unknown;

    var dates = new List<(DateTimeSource, DateTime)>();
    await foreach (var (source, dateTime) in this.EnumerateDateTimes(fileToImport))
      if (!settings.DefaultDatesToIgnore.Contains(dateTime) &&
          dateTime >= settings.MinimumValidDate &&
          dateTime <= currentDateTime) {
        
        switch (source) {
          case DateTimeSource.Gps when gpsDate == null || dateTime > gpsDate.Value:
            gpsDate = dateTime;
            gpsSource = source;
            break;
          case DateTimeSource.ExifSubIfd when (exifDate == null || dateTime > exifDate.Value):
            exifDate = dateTime;
            exifSource = source;
            break;
        }

        dates.Add((source, dateTime));
      }

    // Special handling for GPS vs EXIF dates
    if (gpsDate.HasValue && exifDate.HasValue) {
      if (exifDate.Value > gpsDate.Value && (exifDate.Value - gpsDate.Value).TotalHours <= 1)
        return (exifDate, exifSource);
      return (gpsDate, gpsSource);
    }

    // Sort by reliability and get the best date with its source
    var bestDate = dates
      .OrderByDescending(d => GetSourceReliability(d.Item1))
      .ThenByDescending(d => d.Item2)
      .FirstOrDefault();

    return bestDate != default ? (bestDate.Item2, bestDate.Item1) : (null, DateTimeSource.Unknown);
  }

  public async Task<ImportResult> ProcessDirectoryAsync(
    ImportSettings settings,
    IProgress<ImportProgress>? progress = null,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(settings);
    ThrowHelpers.ThrowIfDirectoryNotExists(settings.SourceDirectory);
    var stopwatch = Stopwatch.StartNew();
    var results = new List<ImportFileResult>();
    var totalFiles = 0;
    var processed = 0;
    var succeeded = 0;
    var failed = 0;
    var skipped = 0;

    // Count total files first
    await foreach (var _ in this.EnumerateDirectory(settings.SourceDirectory, settings.Recursive).WithCancellation(cancellationToken))
      ++totalFiles;

    await foreach (var fileToImport in this.EnumerateDirectory(settings.SourceDirectory, settings.Recursive).WithCancellation(cancellationToken)) {
      cancellationToken.ThrowIfCancellationRequested();

      ++processed;
      progress?.Report(new ImportProgress {
        CurrentFile = processed,
        TotalFiles = totalFiles,
        CurrentFileName = fileToImport.FileName
      });

      var result = await this.ProcessSingleFile(fileToImport, settings);
      results.Add(result);

      if (result.Success)
        ++succeeded;
      else if (result.ErrorMessage?.Contains("skip", StringComparison.OrdinalIgnoreCase) == true ||
               result.ErrorMessage?.Contains("identical", StringComparison.OrdinalIgnoreCase) == true)
        ++skipped;
      else
        ++failed;
    }

    stopwatch.Stop();

    return new ImportResult {
      TotalFiles = totalFiles,
      SuccessfullyProcessed = succeeded,
      Failed = failed,
      Skipped = skipped,
      FileResults = results,
      ElapsedTime = stopwatch.Elapsed
    };
  }

  public async Task<ImportFileResult> ProcessSingleFile(FileToImport fileToImport, ImportSettings settings) {
    try {
      var mostProbableDate = await this.GetMostLogicalCreationDateAsync(fileToImport);
      if (!mostProbableDate.HasValue)
        return ImportFileResult.FromException(fileToImport.Source, "Could not determine date");

      var (result, targetPath, message) = await this._fileOrganizer.ProcessFileAsync(fileToImport, mostProbableDate.Value, settings);

      return new (
        fileToImport.Source,
        targetPath,
        result is FileOperationResult.Success or FileOperationResult.DuplicateRemoved,
        result == FileOperationResult.Failed ? message : null,
        mostProbableDate
      );
    } catch (Exception ex) {
      return ImportFileResult.FromException(fileToImport.Source, ex.Message);
    }
  }

  private static byte GetSourceReliability(DateTimeSource source) => source switch {
    DateTimeSource.Gps => 50,
    DateTimeSource.ExifSubIfd => 40,
    DateTimeSource.ExifIfd0 => 30,
    DateTimeSource.FileName => 20,
    DateTimeSource.FileModifiedAt => 10,
    DateTimeSource.FileCreatedAt => 1,
    _ => 0
  };
}
