namespace PhotoManager.UI.Models;

/// <summary>
/// Per-file row in the auto-keyword scan results grid. <see cref="Keywords"/>
/// is a comma-separated <c>word (score)</c> string for compact display.
/// </summary>
public sealed class AutoKeywordRow {
  public required FileInfo File { get; init; }
  public required string FileName { get; init; }
  public required string Keywords { get; init; }
  public required IReadOnlyList<string> RawKeywords { get; init; }
  public string Status { get; init; } = string.Empty;
}
