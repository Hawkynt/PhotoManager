namespace PhotoManager.UI.Views;

/// <summary>One row in the batch-date-shift preview grid.</summary>
public sealed class DateShiftRow {
  public FileInfo File { get; init; } = null!;
  public string FileName { get; set; } = "";
  public string Current { get; set; } = "";
  public string Shifted { get; set; } = "";
  public string Status { get; set; } = "";
}
