namespace PhotoManager.Core.Enums;

public enum DateTimeSource : byte {
  Unknown,
  FileCreatedAt,
  FileModifiedAt,
  Gps,
  ExifIfd0,
  ExifSubIfd,
  FileName,
}
