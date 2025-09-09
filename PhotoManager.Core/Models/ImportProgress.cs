namespace PhotoManager.Core.Models;

public record ImportProgress {
  public int CurrentFile { get; init; }
  public int TotalFiles { get; init; }
  public string? CurrentFileName { get; init; } // TODO: semantic type: FileInfo?
  // TODO: estimated time remaining?
  // TODO: float is enough precision for percentage?
  public double PercentComplete => this.TotalFiles > 0 ? (double)this.CurrentFile / this.TotalFiles * 100 : 0;
}
