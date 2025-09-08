namespace PhotoManager.Core.Models;

public record ImportResult {
  public int TotalFiles { get; init; }
  public int SuccessfullyProcessed { get; init; }
  public int Failed { get; init; }
  public int Skipped { get; init; }
  public List<ImportFileResult> FileResults { get; init; } = [];
  public TimeSpan ElapsedTime { get; init; }
}

public record ImportFileResult {
  public string SourcePath { get; init; } = string.Empty;
  public string? DestinationPath { get; init; }
  public bool Success { get; init; }
  public string? ErrorMessage { get; init; }
  public DateTime? DetectedDate { get; init; }
}
