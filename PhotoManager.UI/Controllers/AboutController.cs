using System.Reflection;
using PhotoManager.UI.Models;

namespace PhotoManager.UI.Controllers;

public class AboutController {
  public AboutViewModel GetAboutInfo() {
    var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

    return new AboutViewModel {
      Title = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "Photo Manager",
      Version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString() ?? "1.0.0",
      Copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "",
      Description = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description ?? ""
    };
  }
}
