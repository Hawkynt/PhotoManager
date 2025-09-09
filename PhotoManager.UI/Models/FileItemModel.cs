using System.ComponentModel;

namespace PhotoManager.UI.Models;

public class FileItemModel {
  public string FileName { get; set; } = string.Empty;
  public string TargetLocation { get; set; } = string.Empty;
  public string SourcePath { get; set; } = string.Empty;

  [Browsable(false)]
  public FileInfo? FileInfo { get; set; }
  
}