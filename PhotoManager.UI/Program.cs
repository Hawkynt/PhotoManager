using Microsoft.Extensions.DependencyInjection;
using PhotoManager.Core.Interfaces;
using PhotoManager.Core.Services;
using PhotoManager.UI.Controllers;
using PhotoManager.UI.Models;
using PhotoManager.UI.Services;
using PhotoManager.UI.Views;

namespace PhotoManager.UI;

static class Program {
  /// <summary>
  ///   The main entry point for the application.
  /// </summary>
  [STAThread]
  static void Main() {
    ApplicationConfiguration.Initialize();

    // Configure dependency injection
    var serviceProvider = ConfigureServices();

    // Get the main form with dependencies injected
    var mainForm = serviceProvider.GetRequiredService<MainForm>();

    Application.Run(mainForm);
  }

  private static IServiceProvider ConfigureServices() {
    var services = new ServiceCollection();

    // Register Core services
    services.AddSingleton<IImportManager, ImportManager>();
    services.AddSingleton<IDateTimeParser, DateTimeParser>();
    services.AddSingleton<IFileOrganizer, FileOrganizer>();

    // Register UI services
    services.AddSingleton<ISettingsService, SettingsService>();

    // Register UI components
    services.AddSingleton<MainViewModel>();
    services.AddSingleton<MainController>();
    services.AddSingleton<MainForm>();

    return services.BuildServiceProvider();
  }
}
