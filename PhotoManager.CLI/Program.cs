using System.CommandLine;
using PhotoManager.Core.Enums;
using PhotoManager.Core.Interfaces;
using PhotoManager.Core.Models;
using PhotoManager.Core.Services;

var rootCommand = new RootCommand("PhotoManager CLI - Organize your photo collection");

// Source directory option
var sourceOption = new Option<DirectoryInfo>(
  aliases: ["--source", "-s"],
  description: "Source directory containing photos to organize") {
  IsRequired = true
};

// Destination directory option
var destinationOption = new Option<DirectoryInfo?>(
  aliases: ["--destination", "-d"],
  description: "Destination directory (optional, organizes in-place if not specified)");

// Recursive option
var recursiveOption = new Option<bool>(
  aliases: ["--recursive", "-r"],
  getDefaultValue: () => true,
  description: "Process subdirectories recursively");

// Pattern option
var patternOption = new Option<string>(
  aliases: ["--pattern", "-p"],
  getDefaultValue: () => "yyyy/yyyyMMdd/HHmmss",
  description: "Custom naming pattern for organized files");

// Dry run option
var dryRunOption = new Option<bool>(
  aliases: ["--dry-run"],
  description: "Preview changes without moving files");

// Verbose option
var verboseOption = new Option<bool>(
  aliases: ["--verbose", "-v"],
  description: "Show detailed output");

// Preserve originals option
var preserveOption = new Option<bool>(
  aliases: ["--preserve"],
  description: "Copy files instead of moving them");

// Duplicate handling option
var duplicateOption = new Option<DuplicateHandling>(
  aliases: ["--duplicates"],
  getDefaultValue: () => DuplicateHandling.Smart,
  description: "How to handle duplicate files: Smart (detect identical), Rename, Skip, Overwrite");

// Add options to root command
rootCommand.AddOption(sourceOption);
rootCommand.AddOption(destinationOption);
rootCommand.AddOption(recursiveOption);
rootCommand.AddOption(patternOption);
rootCommand.AddOption(dryRunOption);
rootCommand.AddOption(verboseOption);
rootCommand.AddOption(preserveOption);
rootCommand.AddOption(duplicateOption);

rootCommand.SetHandler(async (source, destination, recursive, pattern, dryRun, verbose, preserve, duplicates) => { await ProcessPhotos(source, destination, recursive, pattern, dryRun, verbose, preserve, duplicates); }, sourceOption, destinationOption, recursiveOption, patternOption, dryRunOption, verboseOption, preserveOption, duplicateOption);

// Preview command
var previewCommand = new Command("preview", "Preview organization without making changes");
previewCommand.AddOption(sourceOption);
previewCommand.AddOption(destinationOption);
previewCommand.AddOption(recursiveOption);
previewCommand.AddOption(patternOption);

previewCommand.SetHandler(async (source, destination, recursive, pattern) => { await ProcessPhotos(source, destination, recursive, pattern, dryRun: true, verbose: true, preserve: false, DuplicateHandling.Smart); }, sourceOption, destinationOption, recursiveOption, patternOption);

rootCommand.AddCommand(previewCommand);

// Execute command
return await rootCommand.InvokeAsync(args);

async Task ProcessPhotos(
  DirectoryInfo source,
  DirectoryInfo? destination,
  bool recursive,
  string pattern,
  bool dryRun,
  bool verbose,
  bool preserve,
  DuplicateHandling duplicates) {
  if (!source.Exists) {
    Console.Error.WriteLine($"Error: Source directory '{source.FullName}' does not exist.");
    Environment.Exit(1);
  }

  Console.WriteLine($"Photo Manager CLI");
  Console.WriteLine($"==================");
  Console.WriteLine($"Source: {source.FullName}");
  Console.WriteLine($"Destination: {destination?.FullName ?? "In-place organization"}");
  Console.WriteLine($"Pattern: {pattern}");
  Console.WriteLine($"Mode: {(dryRun ? "Preview" : preserve ? "Copy" : "Move")}");
  Console.WriteLine($"Recursive: {recursive}");
  Console.WriteLine();

  // Create services
  IImportManager importManager = new ImportManager();

  var settings = new ImportSettings {
    SourceDirectory = source,
    DestinationDirectory = destination,
    Recursive = recursive,
    DateFormatPattern = pattern,
    DryRun = dryRun,
    PreserveOriginals = preserve,
    DuplicateHandling = duplicates
  };

  var progressReported = false;
  var progress = new Progress<ImportProgress>(p => {
    if (verbose || !progressReported) {
      Console.WriteLine($"[{p.PercentComplete:F1}%] Processing: {p.CurrentFileName}");
      progressReported = true;
    } else {
      Console.SetCursorPosition(0, Console.CursorTop);
      Console.Write($"[{p.PercentComplete:F1}%] {p.CurrentFile}/{p.TotalFiles} files processed");
    }
  });

  try {
    var result = await importManager.ProcessDirectoryAsync(settings, progress);

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine("Summary");
    Console.WriteLine("-------");
    Console.WriteLine($"Total files: {result.TotalFiles}");
    Console.WriteLine($"Successfully processed: {result.SuccessfullyProcessed}");
    Console.WriteLine($"Failed: {result.Failed}");
    Console.WriteLine($"Skipped: {result.Skipped}");
    Console.WriteLine($"Time elapsed: {result.ElapsedTime:mm\\:ss}");

    if (verbose && result.Failed > 0) {
      Console.WriteLine();
      Console.WriteLine("Failed files:");
      foreach (var failure in result.FileResults.Where(r => !r.Success))
        Console.WriteLine($"  - {failure.SourcePath}: {failure.ErrorMessage}");
    }

    if (dryRun) {
      Console.WriteLine();
      Console.WriteLine("This was a dry run. No files were moved.");
    }
  } catch (Exception ex) {
    Console.Error.WriteLine($"Error: {ex.Message}");
    if (verbose)
      Console.Error.WriteLine(ex.StackTrace);
    Environment.Exit(1);
  }
}
