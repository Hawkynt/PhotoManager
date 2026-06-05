using System.CommandLine;
using Hawkynt.PhotoManager.Core;
using Hawkynt.PhotoManager.Core.Detection;
using Hawkynt.PhotoManager.Core.Geocoding;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.CLI;

/// <summary>
/// Builds the <c>metadata</c> sub-command tree: show, set-gps, set-rating,
/// set-label, add-keyword, remove-keyword. All writes go to an XMP sidecar
/// next to the image file; the original is never modified.
/// </summary>
internal static class MetadataCommands {
  public static Command Build() {
    var metadata = new Command("metadata", "Read or edit photo metadata via XMP sidecars");

    metadata.AddCommand(BuildShow());
    metadata.AddCommand(BuildSetGps());
    metadata.AddCommand(BuildSetDirection());
    metadata.AddCommand(BuildSetTarget());
    metadata.AddCommand(BuildResolve());
    metadata.AddCommand(BuildSetRating());
    metadata.AddCommand(BuildSetLabel());
    metadata.AddCommand(BuildAddKeyword());
    metadata.AddCommand(BuildRemoveKeyword());
    metadata.AddCommand(BuildDetect());

    return metadata;
  }

  private static Command BuildSetDirection() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var degreesArg = InvariantDoubleArg("degrees", "Compass heading 0..360° (0 = north, 90 = east)");
    var magneticOpt = new Option<bool>("--magnetic", "Heading is relative to magnetic north (default: true north)");

    var cmd = new Command("set-direction", "Write the camera's heading into GPSImgDirection") { fileArg, degreesArg, magneticOpt };

    cmd.SetHandler(async (file, degrees, magnetic) => {
      EnsureFile(file);
      if (degrees is < 0 or > 360) {
        Console.Error.WriteLine($"Error: degrees must be 0..360, got {degrees}.");
        Environment.Exit(1);
      }

      var direction = new ImageDirection(degrees, magnetic ? DirectionReference.Magnetic : DirectionReference.True);
      var writer = new CompositeMetadataWriter();
      var sidecar = await writer.ApplyAsync(file, new MetadataEdit { ImageDirection = direction });
      Console.WriteLine($"Wrote direction {degrees:0.##}° {direction.ReferenceTag} → {sidecar.FullName}");
    }, fileArg, degreesArg, magneticOpt);

    return cmd;
  }

  private static Command BuildSetTarget() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var latArg = InvariantDoubleArg("latitude", "Target latitude in decimal degrees");
    var lonArg = InvariantDoubleArg("longitude", "Target longitude in decimal degrees");

    var cmd = new Command("set-target", "Write the subject's GPS coordinates into GPSDestLatitude/Longitude") { fileArg, latArg, lonArg };

    cmd.SetHandler(async (file, lat, lon) => {
      EnsureFile(file);
      var target = new GpsCoordinate(lat, lon);
      if (!target.IsValid) {
        Console.Error.WriteLine($"Error: coordinate ({lat}, {lon}) is out of range.");
        Environment.Exit(1);
      }

      var writer = new CompositeMetadataWriter();
      var sidecar = await writer.ApplyAsync(file, new MetadataEdit { TargetGps = target });
      Console.WriteLine($"Wrote target {lat}, {lon} → {sidecar.FullName}");
    }, fileArg, latArg, lonArg);

    return cmd;
  }

  private static Command BuildResolve() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var overwriteOpt = new Option<bool>("--overwrite", "Overwrite existing location fields (default: fill only blanks)");

    var cmd = new Command("resolve", "Reverse-geocode the photo's GPS and populate Location/City/State/Country") { fileArg, overwriteOpt };

    cmd.SetHandler(async (file, overwrite) => {
      EnsureFile(file);

      var reader = new MetadataReader();
      var existing = await reader.ReadAsync(file);

      if (existing.Gps is not { } gps) {
        Console.Error.WriteLine("Error: file has no GPS coordinates; set them with `metadata set-gps` first.");
        Environment.Exit(1);
        return;
      }

      using var geocoder = new NominatimReverseGeocoder();
      var result = await geocoder.ResolveAsync(gps);
      if (result is not { HasAny: true }) {
        Console.Error.WriteLine("No address resolved for this coordinate.");
        Environment.Exit(1);
        return;
      }

      var edit = new MetadataEdit {
        Location    = PickField(existing.Location,    result.Location,    overwrite),
        City        = PickField(existing.City,        result.City,        overwrite),
        State       = PickField(existing.State,       result.State,       overwrite),
        Country     = PickField(existing.Country,     result.Country,     overwrite),
        CountryCode = PickField(existing.CountryCode, result.CountryCode, overwrite)
      };

      var writer = new CompositeMetadataWriter();
      var sidecar = await writer.ApplyAsync(file, edit);
      Console.WriteLine($"Resolved:");
      if (!string.IsNullOrEmpty(result.Location))    Console.WriteLine($"  Location:    {result.Location}");
      if (!string.IsNullOrEmpty(result.City))        Console.WriteLine($"  City:        {result.City}");
      if (!string.IsNullOrEmpty(result.State))       Console.WriteLine($"  State:       {result.State}");
      if (!string.IsNullOrEmpty(result.Country))     Console.WriteLine($"  Country:     {result.Country}");
      if (!string.IsNullOrEmpty(result.CountryCode)) Console.WriteLine($"  CountryCode: {result.CountryCode}");
      Console.WriteLine($"Written to {sidecar.FullName}");
    }, fileArg, overwriteOpt);

    return cmd;
  }

  private static Optional<string?> PickField(string? existing, string? resolved, bool overwrite) {
    if (string.IsNullOrEmpty(resolved))
      return default;  // leave the field alone
    if (!overwrite && !string.IsNullOrEmpty(existing))
      return default;  // don't clobber user-set values
    return Optional<string?>.Set(resolved);
  }

  private static Command BuildDetect() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var cmd = new Command("detect", "Run the detection pipeline and merge results into the sidecar's keywords") { fileArg };

    cmd.SetHandler(async file => {
      EnsureFile(file);

      using var yolo = new YoloObjectDetector();
      var detector = new CompositeDetector(yolo, new PathDerivedDetector());
      var service = new DetectionService(detector, new MetadataReader(), new CompositeMetadataWriter());

      var result = await service.DetectAndWriteKeywordsAsync(file);

      if (!yolo.IsAvailable)
        Console.WriteLine($"(YOLO model not found at {AppDataPaths.ModelFile(YoloObjectDetector.DefaultModelFileName).FullName} — using path-derived fallback.)");


      if (result.Labels.Count == 0) {
        Console.WriteLine("No labels detected for this file.");
        return;
      }

      Console.WriteLine($"Detected {result.Labels.Count} label(s):");
      foreach (var label in result.Labels)
        Console.WriteLine($"  - {label.Name} ({label.Confidence:0.00}, {label.Kind})");
      Console.WriteLine($"Auto-keywords merged into {SidecarPath.For(file).FullName}");
    }, fileArg);

    return cmd;
  }

  private static Command BuildShow() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var cmd = new Command("show", "Print the effective metadata (EXIF + sidecar overlay)") { fileArg };

    cmd.SetHandler(async file => {
      EnsureFile(file);
      var reader = new MetadataReader();
      var md = await reader.ReadAsync(file);

      Console.WriteLine($"File: {file.FullName}");
      Console.WriteLine($"Sidecar: {SidecarPath.For(file).FullName} {(SidecarPath.For(file).Exists ? "(present)" : "(none)")}");
      Console.WriteLine();
      Console.WriteLine($"GPS:         {FormatGps(md.Gps)}");
      Console.WriteLine($"Direction:   {FormatDirection(md.ImageDirection)}");
      Console.WriteLine($"Target:      {FormatGps(md.TargetGps)}");
      Console.WriteLine($"Location:    {md.Location ?? "(unset)"}");
      Console.WriteLine($"City:        {md.City ?? "(unset)"}");
      Console.WriteLine($"State:       {md.State ?? "(unset)"}");
      Console.WriteLine($"Country:     {md.Country ?? "(unset)"}");
      Console.WriteLine($"CountryCode: {md.CountryCode ?? "(unset)"}");
      Console.WriteLine($"Rating:      {md.Rating?.ToString() ?? "(unset)"}");
      Console.WriteLine($"Color label: {md.ColorLabel ?? "(unset)"}");
      Console.WriteLine($"Title:       {md.Title ?? "(unset)"}");
      Console.WriteLine($"Caption:     {md.Caption ?? "(unset)"}");
      Console.WriteLine($"Keywords:    {(md.Keywords.Count == 0 ? "(none)" : string.Join(", ", md.Keywords))}");
    }, fileArg);

    return cmd;
  }

  private static Command BuildSetGps() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var latArg = InvariantDoubleArg("latitude", "Latitude in decimal degrees (-90..90)");
    var lonArg = InvariantDoubleArg("longitude", "Longitude in decimal degrees (-180..180)");
    var altOpt = InvariantDoubleOption("--altitude", "Altitude in meters (optional, negative for below sea level)");

    var cmd = new Command("set-gps", "Write GPS coordinates into the sidecar") {
      fileArg, latArg, lonArg, altOpt
    };

    cmd.SetHandler(async (file, lat, lon, alt) => {
      EnsureFile(file);
      var coord = new GpsCoordinate(lat, lon, alt);
      if (!coord.IsValid) {
        Console.Error.WriteLine($"Error: coordinate ({lat}, {lon}) is out of range.");
        Environment.Exit(1);
      }

      var writer = new CompositeMetadataWriter();
      var sidecar = await writer.ApplyAsync(file, new MetadataEdit { Gps = coord });
      Console.WriteLine($"Wrote GPS {lat}, {lon}{(alt.HasValue ? $", {alt}m" : "")} → {sidecar.FullName}");
    }, fileArg, latArg, lonArg, altOpt);

    return cmd;
  }

  private static Command BuildSetRating() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var ratingArg = new Argument<int>("rating", "Rating (-1 = rejected, 0 = unrated, 1-5 stars)");

    var cmd = new Command("set-rating", "Write a star rating into the sidecar") { fileArg, ratingArg };

    cmd.SetHandler(async (file, rating) => {
      EnsureFile(file);
      if (rating is < -1 or > 5) {
        Console.Error.WriteLine($"Error: rating must be -1..5, got {rating}.");
        Environment.Exit(1);
      }

      var writer = new CompositeMetadataWriter();
      var sidecar = await writer.ApplyAsync(file, new MetadataEdit { Rating = rating });
      Console.WriteLine($"Wrote rating {rating} → {sidecar.FullName}");
    }, fileArg, ratingArg);

    return cmd;
  }

  private static Command BuildSetLabel() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var labelArg = new Argument<string>("label", "Color label (Red|Yellow|Green|Blue|Purple or any string; use \"\" to clear)");

    var cmd = new Command("set-label", "Write a color label into the sidecar") { fileArg, labelArg };

    cmd.SetHandler(async (file, label) => {
      EnsureFile(file);
      var writer = new CompositeMetadataWriter();
      var patch = string.IsNullOrEmpty(label)
        ? new MetadataEdit { ColorLabel = Optional<string?>.Set(null) }
        : new MetadataEdit { ColorLabel = label };

      var sidecar = await writer.ApplyAsync(file, patch);
      Console.WriteLine($"Wrote label '{label}' → {sidecar.FullName}");
    }, fileArg, labelArg);

    return cmd;
  }

  private static Command BuildAddKeyword() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var keywordArg = new Argument<string>("keyword", "Keyword to add");

    var cmd = new Command("add-keyword", "Append a keyword to the sidecar (deduplicates)") { fileArg, keywordArg };

    cmd.SetHandler(async (file, keyword) => {
      EnsureFile(file);
      if (string.IsNullOrWhiteSpace(keyword)) {
        Console.Error.WriteLine("Error: keyword cannot be empty.");
        Environment.Exit(1);
      }

      var reader = new MetadataReader();
      var current = await reader.ReadAsync(file);
      var merged = current.Keywords.Concat(new[] { keyword })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

      var writer = new CompositeMetadataWriter();
      var sidecar = await writer.ApplyAsync(file, new MetadataEdit {
        Keywords = Optional<IReadOnlyList<string>>.Set(merged)
      });
      Console.WriteLine($"Keywords now: {string.Join(", ", merged)} → {sidecar.FullName}");
    }, fileArg, keywordArg);

    return cmd;
  }

  private static Command BuildRemoveKeyword() {
    var fileArg = new Argument<FileInfo>("file", "Path to the image file");
    var keywordArg = new Argument<string>("keyword", "Keyword to remove");

    var cmd = new Command("remove-keyword", "Drop a keyword from the sidecar") { fileArg, keywordArg };

    cmd.SetHandler(async (file, keyword) => {
      EnsureFile(file);

      var reader = new MetadataReader();
      var current = await reader.ReadAsync(file);
      var filtered = current.Keywords
        .Where(k => !string.Equals(k, keyword, StringComparison.OrdinalIgnoreCase))
        .ToArray();

      var writer = new CompositeMetadataWriter();
      var sidecar = await writer.ApplyAsync(file, new MetadataEdit {
        Keywords = Optional<IReadOnlyList<string>>.Set(filtered)
      });
      Console.WriteLine($"Keywords now: {(filtered.Length == 0 ? "(none)" : string.Join(", ", filtered))} → {sidecar.FullName}");
    }, fileArg, keywordArg);

    return cmd;
  }

  private static void EnsureFile(FileInfo file) {
    if (file.Exists)
      return;

    Console.Error.WriteLine($"Error: file '{file.FullName}' does not exist.");
    Environment.Exit(1);
  }

  private static Argument<double> InvariantDoubleArg(string name, string description) {
    var arg = new Argument<double>(name, description);
    arg.AddValidator(result => {
      var token = result.Tokens[0].Value;
      if (!double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        result.ErrorMessage = $"'{token}' is not a valid decimal number (use '.' as the decimal separator).";
    });
    arg.SetDefaultValueFactory(() => 0.0);
    // Replace System.CommandLine's default parser with one that forces invariant culture.
    arg = new Argument<double>(name, result => {
      var token = result.Tokens[0].Value;
      return double.Parse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
    }, isDefault: false, description);
    return arg;
  }

  private static Option<double?> InvariantDoubleOption(string name, string description) {
    return new Option<double?>(name, result => {
      if (result.Tokens.Count == 0)
        return null;
      var token = result.Tokens[0].Value;
      return double.Parse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
    }, isDefault: true, description);
  }

  private static string FormatDirection(ImageDirection? direction) {
    if (direction is not { } d)
      return "(unset)";
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    return string.Create(inv, $"{d.Degrees:0.##}° {d.ReferenceTag}");
  }

  private static string FormatGps(GpsCoordinate? gps) {
    if (gps is not { } g)
      return "(unset)";

    var inv = System.Globalization.CultureInfo.InvariantCulture;
    return g.AltitudeMeters is { } alt
      ? string.Create(inv, $"{g.Latitude:0.######}, {g.Longitude:0.######}, {alt:0.##}m")
      : string.Create(inv, $"{g.Latitude:0.######}, {g.Longitude:0.######}");
  }
}
