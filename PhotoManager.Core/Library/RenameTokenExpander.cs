using System.Globalization;
using System.Text;
using PhotoManager.Core.Metadata;

namespace PhotoManager.Core.Library;

/// <summary>
/// Renders a metadata-aware filename template (no path component — output
/// is a filename within the same directory). Supports brace-delimited
/// tokens with an optional <c>:format</c> suffix. Unknown tokens pass
/// through verbatim so the user can spot typos in the preview.
///
/// Token values are sanitised — path separators and reserved characters
/// in metadata strings are replaced with <c>_</c> so a city like
/// "Mont-Saint-Michel" stays intact but a stray slash can't accidentally
/// create subdirectories.
///
/// Supported tokens (case-insensitive name):
/// <list type="bullet">
///   <item><description><c>{name}</c> — current filename without extension</description></item>
///   <item><description><c>{ext}</c> — current extension without leading dot</description></item>
///   <item><description><c>{date:fmt}</c> — capture date (default <c>yyyy-MM-dd</c>)</description></item>
///   <item><description><c>{time:fmt}</c> — capture time (default <c>HHmmss</c>)</description></item>
///   <item><description><c>{year} / {month} / {day} / {hour} / {minute} / {second}</c> — capture-date components</description></item>
///   <item><description><c>{rating}</c> — XMP star rating (0–5, or empty)</description></item>
///   <item><description><c>{label}</c> — XMP color label</description></item>
///   <item><description><c>{city} / {country} / {countrycode} / {state} / {location}</c> — place names</description></item>
///   <item><description><c>{title} / {caption} / {creator}</c> — text fields</description></item>
///   <item><description><c>{index:fmt}</c> — sequential counter (1-based, default <c>0</c>)</description></item>
///   <item><description><c>{total:fmt}</c> — total file count</description></item>
/// </list>
/// </summary>
public static class RenameTokenExpander {
  /// <summary>
  /// Expand <paramref name="template"/> to a sanitised filename. The
  /// <paramref name="captureDate"/> is the photo's local capture timestamp
  /// (typically from <c>PhotoTimestampReader</c>); pass null when missing —
  /// date tokens then expand to empty strings.
  /// </summary>
  public static string Expand(
      string template,
      FileInfo file,
      FullMetadata? metadata,
      DateTime? captureDate,
      int index,
      int totalCount) {
    ArgumentNullException.ThrowIfNull(template);
    ArgumentNullException.ThrowIfNull(file);

    var result = new StringBuilder(template.Length + 32);
    var i = 0;
    while (i < template.Length) {
      var open = template.IndexOf('{', i);
      if (open < 0) {
        result.Append(template, i, template.Length - i);
        break;
      }
      result.Append(template, i, open - i);

      var close = template.IndexOf('}', open + 1);
      if (close < 0) {
        // Unmatched '{' — leave the rest of the template verbatim.
        result.Append(template, open, template.Length - open);
        break;
      }

      var token = template.Substring(open + 1, close - open - 1);
      result.Append(ExpandSingleToken(token, file, metadata, captureDate, index, totalCount));
      i = close + 1;
    }
    // Sanitise the final string too so a stray '/' or '\' in the template
    // literal (between tokens) can't accidentally place the file into a
    // subdirectory or fail the rename on Windows.
    return SanitizeForFilename(result.ToString());
  }

  private static string ExpandSingleToken(
      string token,
      FileInfo file,
      FullMetadata? metadata,
      DateTime? captureDate,
      int index,
      int totalCount) {
    var colon = token.IndexOf(':');
    var name = colon >= 0 ? token[..colon] : token;
    var format = colon >= 0 ? token[(colon + 1)..] : null;
    var inv = CultureInfo.InvariantCulture;

    string raw = name.ToLowerInvariant() switch {
      "name"        => Path.GetFileNameWithoutExtension(file.Name),
      "ext"         => file.Extension.TrimStart('.'),
      "date"        => captureDate?.ToString(format ?? "yyyy-MM-dd", inv) ?? "",
      "time"        => captureDate?.ToString(format ?? "HHmmss", inv) ?? "",
      "year"        => captureDate?.Year  .ToString("0000", inv) ?? "",
      "month"       => captureDate?.Month .ToString("00",   inv) ?? "",
      "day"         => captureDate?.Day   .ToString("00",   inv) ?? "",
      "hour"        => captureDate?.Hour  .ToString("00",   inv) ?? "",
      "minute"      => captureDate?.Minute.ToString("00",   inv) ?? "",
      "second"      => captureDate?.Second.ToString("00",   inv) ?? "",
      "rating"      => metadata?.Rating?.ToString(inv) ?? "",
      "label"       => metadata?.ColorLabel ?? "",
      "city"        => metadata?.City        ?? "",
      "country"     => metadata?.Country     ?? "",
      "countrycode" => metadata?.CountryCode ?? "",
      "state"       => metadata?.State       ?? "",
      "location"    => metadata?.Location    ?? "",
      "title"       => metadata?.Title       ?? "",
      "caption"     => metadata?.Caption     ?? "",
      "creator"     => metadata?.Creator     ?? "",
      "index"       => index.ToString(format ?? "0", inv),
      "total"       => totalCount.ToString(format ?? "0", inv),
      _             => "{" + token + "}"  // unknown — let the user see it in the preview
    };

    return raw;
  }

  /// <summary>
  /// Replace characters illegal in filenames (path separators, reserved
  /// chars on Windows) with <c>_</c>. Trims surrounding whitespace.
  /// </summary>
  public static string SanitizeForFilename(string raw) {
    if (string.IsNullOrEmpty(raw))
      return raw;
    var invalid = Path.GetInvalidFileNameChars();
    var sb = new StringBuilder(raw.Length);
    foreach (var c in raw)
      sb.Append(invalid.Contains(c) ? '_' : c);
    return sb.ToString().Trim();
  }
}
