using PhotoManager.Core.Enums;
using PhotoManager.Core.Models;

namespace PhotoManager.Core.Interfaces;

public interface IImportManager {
  IAsyncEnumerable<FileToImport> EnumerateDirectory(DirectoryInfo root, bool recursive);
  IAsyncEnumerable<(DateTimeSource source, DateTime dateTime)> EnumerateDateTimes(FileToImport fileToImport);
  Task<DateTime?> GetMostLogicalCreationDateAsync(FileToImport fileToImport);
  Task<(DateTime? Date, DateTimeSource Source)> GetMostLogicalCreationDateWithSourceAsync(FileToImport fileToImport);
  Task<ImportResult> ProcessDirectoryAsync(ImportSettings settings, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default);
}