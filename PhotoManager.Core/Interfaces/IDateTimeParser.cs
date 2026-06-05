using Hawkynt.PhotoManager.Core.Models;

namespace Hawkynt.PhotoManager.Core.Interfaces;

public interface IDateTimeParser {
  IAsyncEnumerable<DateTime> ParseDateFromFileName(FileToImport fileToImport, ImportSettings settings);
  string[] GetSupportedDateFormats();
}
