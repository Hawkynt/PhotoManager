namespace PhotoManager.Core.Models;

public record ImportFileResult(FileInfo SourcePath, FileInfo? DestinationPath, bool Success, string? ErrorMessage, DateTime? DetectedDate) {

  public static ImportFileResult FromException(FileInfo source, string errorMessage) => new(source, null, false, errorMessage, null);

}
