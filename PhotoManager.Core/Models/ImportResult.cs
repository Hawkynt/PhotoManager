namespace PhotoManager.Core.Models;

public record ImportResult {
  public int TotalFiles { get; init; }
  public int SuccessfullyProcessed { get; init; }
  public int Failed { get; init; }
  public int Skipped { get; init; }
  public List<ImportFileResult> FileResults { get; init; } = [];
  public TimeSpan ElapsedTime { get; init; }
}