using PhotoManager.Core.Enums;
using PhotoManager.Core.Models;

namespace PhotoManager.Core.Interfaces;

public interface IFileOrganizer {
  Task<FileInfo> GenerateTargetPath(FileToImport file, DateTime dateTime, ImportSettings settings);
  Task<bool> MoveFileAsync(FileInfo sourcePath, FileInfo targetPath, bool overwrite = false);
  Task<bool> CopyFileAsync(FileInfo sourcePath, FileInfo targetPath, bool overwrite = false);
  Task<(FileOperationResult result, FileInfo? targetPath, string? message)> ProcessFileAsync(FileToImport file, DateTime dateTime, ImportSettings settings);
  Task<bool> AreFilesIdenticalAsync(FileInfo filePath1, FileInfo filePath2);
}
