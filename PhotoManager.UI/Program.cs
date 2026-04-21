using Avalonia;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using PhotoManager.Core.Interfaces;
using PhotoManager.Core.Services;
using PhotoManager.UI.Controllers;
using PhotoManager.UI.Models;
using PhotoManager.UI.Services;

namespace PhotoManager.UI;

internal static class Program {
  public static IServiceProvider Services { get; private set; } = null!;

  [STAThread]
  public static void Main(string[] args) {
    Services = ConfigureServices();
    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
  }

  public static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
      .UsePlatformDetect()
      .WithInterFont()
      .LogToTrace()
      .UseReactiveUI();

  private static IServiceProvider ConfigureServices() {
    var services = new ServiceCollection();

    services.AddSingleton<IImportManager, ImportManager>();
    services.AddSingleton<IDateTimeParser, DateTimeParser>();
    services.AddSingleton<IFileOrganizer, FileOrganizer>();

    services.AddSingleton<ISettingsService, SettingsService>();
    services.AddSingleton<IFolderPicker, AvaloniaFolderPicker>();

    services.AddSingleton<MainViewModel>();
    services.AddSingleton<MainController>();
    services.AddTransient<AboutController>();

    return services.BuildServiceProvider();
  }
}
