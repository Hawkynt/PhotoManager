namespace PhotoManager.Core.Enums;

public enum FileOperationResult : byte {
  /// <summary>
  /// File operation completed successfully
  /// </summary>
  Success,
  
  /// <summary>
  /// File was skipped (already exists and skip policy applied)
  /// </summary>
  Skipped,
  
  /// <summary>
  /// Source file is identical to target, source was deleted
  /// </summary>
  DuplicateRemoved,
  
  /// <summary>
  /// Operation failed due to an error
  /// </summary>
  Failed
}