using PhotoManager.Core.Enums;

namespace PhotoManager.Core.Models;

public record ImportSettings {
  public DirectoryInfo SourceDirectory { get; init; } = null!;
  public DirectoryInfo? DestinationDirectory { get; init; }
  public bool Recursive { get; init; } = true;
  public string DateFormatPattern { get; init; } = "yyyy/yyyyMMdd/HHmmss";
  public bool PreserveOriginals { get; init; } = false;
  public bool DryRun { get; init; } = false;
  public int MaxParallelism { get; init; } = Environment.ProcessorCount;
  public DateTime MinimumValidDate { get; init; } = new(1990, 1, 1);
  public HashSet<DateTime> DefaultDatesToIgnore { get; init; } = [
    new(1970, 1, 1), 
    new(1980, 1, 1)
  ];

  public DuplicateHandling DuplicateHandling { get; init; } = DuplicateHandling.Smart;
}
