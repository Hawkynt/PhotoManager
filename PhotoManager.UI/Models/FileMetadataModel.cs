using System.ComponentModel;

namespace PhotoManager.UI.Models;

public class FileMetadataModel {
  [DisplayName("File Name")]
  [Description("Name of the selected file")]
  public string? FileName { get; set; }

  [DisplayName("File Path")]
  [Description("Full path to the file on disk")]
  public string? FilePath { get; set; }

  [DisplayName("File Size")]
  [Description("Size of the file in bytes, formatted for display")]
  public string? FileSize { get; set; }

  [DisplayName("Date Created")]
  [Description("Date and time when the file was created on the filesystem")]
  public string? DateCreated { get; set; }

  [DisplayName("Date Modified")]
  [Description("Date and time when the file was last modified")]
  public string? DateModified { get; set; }

  [DisplayName("Filename Date")]
  [Description("Date extracted from the filename using pattern recognition")]
  public string? FilenameDate { get; set; }

  [DisplayName("EXIF Original Date")]
  [Description("Original date from EXIF metadata (when photo was taken)")]
  public string? ExifOriginalDate { get; set; }

  [DisplayName("EXIF Modified Date")]
  [Description("Modified date from EXIF metadata")]
  public string? ExifModifiedDate { get; set; }

  [DisplayName("GPS Date")]
  [Description("Date from GPS metadata if available")]
  public string? GpsDate { get; set; }
}