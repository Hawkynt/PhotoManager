using PhotoManager.Core.Interfaces;
using PhotoManager.Core.Models;

namespace PhotoManager.Core.Services;

public class DateTimeParser : IDateTimeParser {
  private readonly string[] _dateTimeFormats = [
    "yyyyMMddHHmmss",
    "yyyyMMddHHmm",
    "yyyyMMdd HHmmss",
    "yyyyMMdd HHmm",
    "yyyyMMdd_HHmmss",
    "yyyyMMdd_HHmm",
    "yyMMddHHmmss",
    "yyMMddHHmm",
    "yyMMdd HHmmss",
    "yyMMdd HHmm",
    "yyMMdd_HHmmss",
    "yyMMdd_HHmm",
    "yyyy-MM-dd-HH-mm-ss",
    "yyyy-MM-dd HH-mm-ss",
    "yyyy-MM-dd-HH-mm",
    "yyyy-MM-dd HH-mm",
    "yy-MM-dd-HH-mm-ss",
    "yy-MM-dd HH-mm-ss",
    "yy-MM-dd-HH-mm",
    "yy-MM-dd HH-mm",
    "yyyy_MM_dd_HH_mm_ss",
    "yyyy_MM_dd_HH_mm",
    "yyyy_MM_dd HH_mm_ss",
    "yyyy_MM_dd HH_mm",
    "yy_MM_dd_HH_mm_ss",
    "yy_MM_dd_HH_mm",
    "yy_MM_dd HH_mm_ss",
    "yy_MM_dd HH_mm",
    "yyyyMMdd",
    "yyyy-MM-dd",
    "yyyy_MM_dd",
    "yyMMdd",
    "yy-MM-dd",
    "yy_MM_dd",
    "ddMMyyyy",
    "dd-MM-yyyy",
    "dd_MM_yyyy",
    "ddMMyy",
    "dd-MM-yy",
    "dd_MM_yy"
  ];

  public string[] GetSupportedDateFormats() => this._dateTimeFormats;

  public async IAsyncEnumerable<DateTime> ParseDateFromFileName(FileToImport fileToImport, ImportSettings settings) {
    var fileName = fileToImport.FileName;
    var createdAt = await fileToImport.GetFileCreatedAt();
    var lastWrittenAt = await fileToImport.GetFileLastWrittenAt();

    var defaultValues = createdAt >= settings.MinimumValidDate ? createdAt : lastWrittenAt;
    if (lastWrittenAt < defaultValues && lastWrittenAt >= settings.MinimumValidDate)
      defaultValues = lastWrittenAt;

    var regexPatterns = this._dateTimeFormats.Select(f => (format: f, regex: FormatToRegex(f)));

    foreach (var pattern in regexPatterns.OrderByDescending(i => i.format.Length))
    foreach (Match match in new Regex(pattern.regex).Matches(fileName)) {
      var year = match.Groups["year"].Success ? int.Parse(match.Groups["year"].Value) : defaultValues.Year;
      var month = match.Groups["month"].Success ? int.Parse(match.Groups["month"].Value) : defaultValues.Month;
      var day = match.Groups["day"].Success ? int.Parse(match.Groups["day"].Value) : defaultValues.Day;
      var hour = match.Groups["hour"].Success ? int.Parse(match.Groups["hour"].Value) : defaultValues.Hour;
      var minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value) : defaultValues.Minute;
      var second = match.Groups["second"].Success ? int.Parse(match.Groups["second"].Value) : defaultValues.Second;

      if (match.Groups["year"].Length == 2)
        year += year < 50 ? 2000 : 1900;

      DateTime? result = null;
      try {
        result = new DateTime(year, month, day, hour, minute, second);
      } catch (ArgumentOutOfRangeException) {
        // Invalid date-time combination
      }

      if (result != null)
        yield return result.Value;
    }
  }

  private static string FormatToRegex(string format) {
    var tokenIndex = 0;

    var placeholders = new Dictionary<string, string> {
      { "yyyy", @"(?<year>\d{4})" },
      { "yy", @"(?<year>\d{2})" },
      { "MM", @"(?<month>\d{2})" },
      { "M", @"(?<month>\d{1,2})" },
      { "dd", @"(?<day>\d{2})" },
      { "d", @"(?<day>\d{1,2})" },
      { "HH", @"(?<hour>\d{2})" },
      { "H", @"(?<hour>\d{1,2})" },
      { "mm", @"(?<minute>\d{2})" },
      { "m", @"(?<minute>\d{1,2})" },
      { "ss", @"(?<second>\d{2})" },
      { "s", @"(?<second>\d{1,2})" },
    };

    var placeholderToToken = new Dictionary<string, string>();
    foreach (var placeholder in placeholders) {
      var token = GenerateUniqueToken();
      placeholderToToken.Add(token, placeholder.Value);
      format = format.Replace(placeholder.Key, token);
    }

    format = placeholderToToken.Aggregate(Regex.Replace(format, @"[-_./\\:]", @"[-_./\\:]*"), (current, kvp) => current.Replace(kvp.Key, kvp.Value));
    return $"^.*?{format}.*?$";

    string GenerateUniqueToken() {
      string token;
      for (;;)
        if (!format.Contains(token = $"\0{tokenIndex++}\0"))
          return token;
    }
  }
}
