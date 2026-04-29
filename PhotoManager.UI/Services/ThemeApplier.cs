using Avalonia;
using Avalonia.Styling;
using PhotoManager.UI.Models;

namespace PhotoManager.UI.Services;

public static class ThemeApplier {
  public static ThemeVariant ToAvalonia(ThemeVariantPreference preference) => preference switch {
    ThemeVariantPreference.Light => ThemeVariant.Light,
    ThemeVariantPreference.Dark => ThemeVariant.Dark,
    _ => ThemeVariant.Default
  };

  public static void Apply(ThemeVariantPreference preference) {
    if (Application.Current is { } app)
      app.RequestedThemeVariant = ToAvalonia(preference);
  }
}
