using PhotoManager.Core.Metadata;

namespace PhotoManager.UI.Views;

/// <summary>
/// One row in the batch-rename preview grid. Held as a class (not a record)
/// because the grid mutates <see cref="NewName"/> and <see cref="Status"/>
/// in place when the user edits the template.
/// </summary>
public sealed class RenameRow {
  public FileInfo File { get; init; } = null!;
  public FullMetadata? Metadata { get; set; }
  public DateTime? CaptureDate { get; init; }
  public string CurrentName { get; set; } = "";
  public string NewName { get; set; } = "";
  public string Status { get; set; } = "";
}
