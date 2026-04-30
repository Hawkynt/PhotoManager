using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PhotoManager.Core.Models;
using PhotoManager.UI.Views;

namespace PhotoManager.UI.Services;

/// <summary>
/// Centralised "is the ONNX model installed? — if not, ask the user to
/// download it now" gate. Every AI feature (denoise, upscale, auto-keyword,
/// subject mask, etc.) routes through here so the user never sees a silent
/// no-op when a model is absent.
/// </summary>
public static class ModelPrompt {
  /// <summary>
  /// If <paramref name="model"/> is already on disk: returns <c>true</c>.
  /// Otherwise pops a yes/no dialog explaining what's missing; on Yes,
  /// opens the model-download window and returns whatever <c>IsInstalled</c>
  /// reports after the user closes it.
  /// </summary>
  public static async Task<bool> EnsureInstalledAsync(Window owner, ModelInfo model, string featureName) {
    if (model.IsInstalled())
      return true;

    var sizeMb = Math.Max(1, model.ApproximateSizeBytes / (1024 * 1024));
    var message = $"The {featureName} feature needs the ONNX model \"{model.DisplayName}\" (~{sizeMb} MB), which isn't installed yet.\n\nOpen the download dialog now?";

    var pick = await ConfirmAsync(owner, $"Install {model.DisplayName}?", message);
    if (!pick)
      return false;

    var window = new ModelDownloadWindow();
    await window.ShowDialog(owner);
    return model.IsInstalled();
  }

  /// <summary>
  /// Yes/No modal. Two buttons follow the app-wide convention: ✅ Yes / ❌ No.
  /// Returns true on Yes, false on No or window-close.
  /// </summary>
  public static Task<bool> ConfirmAsync(Window owner, string title, string message) {
    var tcs = new TaskCompletionSource<bool>();

    var yesButton = new Button {
      Content = "✅ Yes",
      Padding = new Thickness(14, 4),
      FontWeight = FontWeight.SemiBold,
      HorizontalAlignment = HorizontalAlignment.Right
    };
    var noButton = new Button {
      Content = "❌ No",
      Padding = new Thickness(14, 4),
      Margin = new Thickness(8, 0, 0, 0)
    };

    var dialog = new Window {
      Title = title,
      Width = 480,
      SizeToContent = SizeToContent.Height,
      CanResize = false,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
      ShowInTaskbar = false,
      Content = new StackPanel {
        Margin = new Thickness(16),
        Spacing = 16,
        Children = {
          new TextBlock {
            Text = message,
            TextWrapping = TextWrapping.Wrap
          },
          new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { yesButton, noButton }
          }
        }
      }
    };

    yesButton.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
    noButton.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
    dialog.Closed += (_, _) => tcs.TrySetResult(false);

    _ = dialog.ShowDialog(owner);
    return tcs.Task;
  }
}
