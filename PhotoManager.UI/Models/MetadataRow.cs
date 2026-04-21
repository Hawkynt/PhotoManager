namespace PhotoManager.UI.Models;

/// <summary>
/// One row in the tinted metadata view. IsWinner drives the green highlight
/// (the date source the importer chose); IsMissing drives the salmon highlight
/// for "not found"/"not detected" values.
/// </summary>
public sealed record MetadataRow(string Name, string Value, bool IsWinner, bool IsMissing);
