namespace PhotoManager.Services;

public class ImportManager {
    public enum DateTimeSource:byte {
        Unknown,
        FileCreatedAt,
        FileModifiedAt,
        Gps,
        ExifIfd0,
        ExifSubIfd,
        FileName,
    }

    private readonly DateTime _minDate =new DateTime(1990, 1, 1); // Threshold for bogus dates

    public async IAsyncEnumerable<FileToImport> EnumerateDirectory(DirectoryInfo root, bool recursive) {
        if (root == null || !root.Exists)
            yield break; // Or throw an exception if appropriate

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (var file in root.EnumerateFiles("*", searchOption).OrderBy(i=>i.Name)) {
            yield return new FileToImport(file);

            // In an async method, this makes sure we asynchronously yield control back to the caller,
            // which can be useful if there are many files to avoid blocking on a single large operation.
            await Task.Yield();
        }
    }

    public async IAsyncEnumerable<(DateTimeSource,DateTime)> EnumerateDateTimes(FileToImport fileToImport) {
        yield return (DateTimeSource.FileCreatedAt, await fileToImport.GetFileCreatedAt());
        yield return (DateTimeSource.FileModifiedAt, await fileToImport.GetFileLastWrittenAt());
        await foreach (var date in fileToImport.GetExifIfd0DateAsync())
            yield return (DateTimeSource.ExifIfd0, date);
        
        await foreach (var date in fileToImport.GetExifSubIfdDateAsync())
            yield return (DateTimeSource.ExifSubIfd, date);
        
        await foreach (var date in fileToImport.GetGpsDateAsync())
            yield return (DateTimeSource.Gps, date);
        
        await foreach (var date in ParseDateFromFileName(fileToImport))
            yield return (DateTimeSource.FileName, date);
    }

    public async Task<DateTime?> GetMostLogicalCreationDateAsync(FileToImport fileToImport) {
        var currentDateTime = DateTime.UtcNow;
        var minDate = this._minDate;
        var defaultDates = new HashSet<DateTime> { new DateTime(1970, 1, 1), new DateTime(1980, 1, 1) };

        DateTime? gpsDate = null;
        DateTime? exifDate = null;

        var dates = new List<(DateTimeSource, DateTime)>();
        await foreach (var (source, dateTime) in EnumerateDateTimes(fileToImport)) {
            if (!defaultDates.Contains(dateTime) && dateTime >= minDate && dateTime <= currentDateTime)            {
                if (source == DateTimeSource.Gps && (gpsDate==null || dateTime>gpsDate.Value))
                    gpsDate = dateTime;
                else if (source == DateTimeSource.ExifSubIfd && (exifDate == null || dateTime > exifDate.Value))
                    exifDate = dateTime;

                dates.Add((source, dateTime));
            }
        }

        // If GPS date is available and it's newer than EXIF date, or if EXIF date is within one hour newer than GPS, use EXIF date.
        if (gpsDate.HasValue && exifDate.HasValue) {
            if (exifDate.Value > gpsDate.Value && (exifDate.Value - gpsDate.Value).TotalHours <= 1)
                return exifDate;

            return gpsDate;
        }

        static byte _GetSourceReliability(DateTimeSource source) {
            switch (source) {
                case DateTimeSource.Gps:
                    return 50;
                case DateTimeSource.ExifSubIfd:
                    return 40;
                case DateTimeSource.ExifIfd0:
                    return 30;
                case DateTimeSource.FileCreatedAt:
                    return 1;
                case DateTimeSource.FileName:
                    return 20;
                case DateTimeSource.FileModifiedAt:
                    return 10;
                default:
                    return 0;
            }
        }

        // Sort remaining dates by their reliability, then by date
        var sortedDates = dates
            .OrderByDescending(d => _GetSourceReliability(d.Item1))
            .ThenByDescending(d => d.Item2)
            .Select(d => d.Item2)
            .ToList()
            ;

        return sortedDates.FirstOrDefault();
    }
    

    private async IAsyncEnumerable<DateTime> ParseDateFromFileName(FileToImport fileToImport) {
        var fileName=fileToImport.FileName;
        var createdAt=await fileToImport.GetFileCreatedAt();
        var lastWrittenAt=await fileToImport.GetFileLastWrittenAt();
        
        var defaultValues=createdAt>=this._minDate?createdAt:lastWrittenAt;
        if(lastWrittenAt<defaultValues && lastWrittenAt>=this._minDate)
            defaultValues=lastWrittenAt;
        
        var dateTimeFormats = new[]{
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
            "dd_MM_yy",
            // More formats can be added as needed
        };

         var regexPatterns = dateTimeFormats.Select(f=>(format:f,regex:FormatToRegex(f)));

        foreach (var pattern in regexPatterns.OrderByDescending(i=>i.format.Length))
        foreach (Match match in new Regex(pattern.regex).Matches(fileName)){
            var year = match.Groups["year"].Success ? int.Parse(match.Groups["year"].Value) : defaultValues.Year;
            var month = match.Groups["month"].Success ? int.Parse(match.Groups["month"].Value) : defaultValues.Month;
            var day = match.Groups["day"].Success ? int.Parse(match.Groups["day"].Value) : defaultValues.Day;
            var hour = match.Groups["hour"].Success ? int.Parse(match.Groups["hour"].Value) : defaultValues.Hour;
            var minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value) : defaultValues.Minute;
            var second = match.Groups["second"].Success ? int.Parse(match.Groups["second"].Value) : defaultValues.Second;

            if (match.Groups["year"].Length == 2) // Adjust for two-digit year
            {
                year += year < 50 ? 2000 : 1900; // Adjust based on a pivot year, e.g., 50 for 1950-2049
            }

            DateTime? result = null;
            try            {
                result = new DateTime(year, month, day, hour, minute, second);
            }            catch (ArgumentOutOfRangeException)            {
                // Handle invalid date-time combinations
            }
            if(result!=null)
                yield return result.Value;
        }
            
        static string FormatToRegex(string format)        {
            var tokenIndex = 0;
            string GenerateUniqueToken() {
                string token;
                for(;;)
                    if(!format.Contains(token=$"\0{tokenIndex++}\0"))
                        return token;
            }

            // Define placeholders and corresponding regex patterns
            var placeholders = new Dictionary<string, string>            {
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

            // Replace placeholders with unique tokens
            var placeholderToToken = new Dictionary<string, string>();
            foreach (var placeholder in placeholders)            {
                var token = GenerateUniqueToken();
                placeholderToToken.Add(token,placeholder.Value);
                format = format.Replace(placeholder.Key, token);
            }

            // Replace optional date and time separators
            format = Regex.Replace(format, @"[-_./\\:]", @"[-_./\\:]*");
            
            foreach (var kvp in placeholderToToken)
                format = format.Replace(kvp.Key, kvp.Value);
            
            return $"^.*?{format}.*?$";
        }

    }

}