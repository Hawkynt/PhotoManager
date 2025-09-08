using PhotoManager.Core.Models;

namespace PhotoManager.Core.Interfaces;

public interface IDateTimeParser {
  IAsyncEnumerable<DateTime> ParseDateFromFileName(FileToImport fileToImport, ImportSettings settings);
  string[] GetSupportedDateFormats();
}
