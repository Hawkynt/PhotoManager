using PhotoManager.Core.Enums;
using PhotoManager.Core.Models;

namespace PhotoManager.Core.Interfaces;

public interface IFileOrganizer {
  Task<string> GenerateTargetPath(FileToImport file, DateTime dateTime, ImportSettings settings);
  Task<bool> MoveFileAsync(string sourcePath, string targetPath, bool overwrite = false);
  Task<bool> CopyFileAsync(string sourcePath, string targetPath, bool overwrite = false);
  Task<(FileOperationResult result, string? targetPath, string? message)> ProcessFileAsync(FileToImport file, DateTime dateTime, ImportSettings settings);
  Task<bool> AreFilesIdenticalAsync(string filePath1, string filePath2);
}
