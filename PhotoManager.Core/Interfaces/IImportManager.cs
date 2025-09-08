using PhotoManager.Core.Enums;
using PhotoManager.Core.Models;

namespace PhotoManager.Core.Interfaces;

public interface IImportManager {
  IAsyncEnumerable<FileToImport> EnumerateDirectory(DirectoryInfo root, bool recursive);
  IAsyncEnumerable<(DateTimeSource source, DateTime dateTime)> EnumerateDateTimes(FileToImport fileToImport);
  Task<DateTime?> GetMostLogicalCreationDateAsync(FileToImport fileToImport);
  Task<ImportResult> ProcessDirectoryAsync(ImportSettings settings, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default);
}

public record ImportProgress {
  public int CurrentFile { get; init; }
  public int TotalFiles { get; init; }
  public string? CurrentFileName { get; init; }
  public double PercentComplete => this.TotalFiles > 0 ? (double)this.CurrentFile / this.TotalFiles * 100 : 0;
}
