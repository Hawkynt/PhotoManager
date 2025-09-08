namespace PhotoManager;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        ImportManager importManager=new();
        OrderFiles(importManager,new(@"X:\Users\Hawkynt\Pictures\Photos\_Input\Arya Ookami")).GetAwaiter().GetResult();
        OrderFiles(importManager,new(@"X:\Users\Hawkynt\Pictures\Photos\_Input\Chibinom")).GetAwaiter().GetResult();
        OrderFiles(importManager,new(@"X:\Users\Hawkynt\Pictures\Photos\_Input\Foxy")).GetAwaiter().GetResult();
        OrderFiles(importManager,new(@"X:\Users\Hawkynt\Pictures\Photos\_Input\Ookami")).GetAwaiter().GetResult();
        OrderFiles(importManager,new(@"X:\Users\Hawkynt\Pictures\Photos\_Input\Oryo Hawkynt")).GetAwaiter().GetResult();

        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        //Application.Run(new MainForm());
    }  

    static async Task OrderFiles(ImportManager manager, DirectoryInfo inputDirectory){
        if (!inputDirectory.Exists)        {
            Console.WriteLine($"Input directory '{inputDirectory.FullName}' does not exist.");
            return;
        }

        await foreach (var fileToImport in manager.EnumerateDirectory(inputDirectory, recursive: false))
        {
            Console.WriteLine($"\r\nProcessing file {fileToImport.FileName}");
            await foreach (var (source, dateTime) in manager.EnumerateDateTimes(fileToImport))
                Console.WriteLine($"{source}: {dateTime}");

            var mostProbableDate = await manager.GetMostLogicalCreationDateAsync(fileToImport);
            if (!mostProbableDate.HasValue)
            {
                Console.WriteLine($"Could not determine the date for file '{fileToImport.Source.Name}'.");
                continue;
            }
             
            var date=mostProbableDate.Value;
            Console.WriteLine($"Most probable original date {date:dd'.'MM'.'yyyy' 'HH':'mm':'ss}");
            
            var newDirPath = Path.Combine(inputDirectory.FullName, date.ToString("yyyy"), date.ToString("yyyyMMdd"));
            var baseFilePath = Path.Combine(newDirPath, mostProbableDate.Value.ToString("HHmmss"));
            
            var newFilePath = $"{baseFilePath}{fileToImport.Source.Extension}";
            for(var i=2;File.Exists(newFilePath);++i)
                newFilePath = $"{baseFilePath} ({i}){fileToImport.Source.Extension}";

            Directory.CreateDirectory(newDirPath); // Create the directory if it doesn't exist

            try
            {
                File.Move(fileToImport.Source.FullName, newFilePath,overwrite:false); // Move the file
                Console.WriteLine($"Moved file '{fileToImport.Source.Name}' to '{newFilePath}'.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error moving file '{fileToImport.Source.Name}': {ex.Message}");
            }
            
        }
    }  
}