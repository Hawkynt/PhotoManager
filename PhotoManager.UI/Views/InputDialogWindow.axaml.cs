using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Bare-bones single-line input prompt. Returns the entered string on OK or
/// null on Cancel. Used by the saved-search "Save…" flow on the main window.
/// </summary>
public partial class InputDialogWindow : Window {
  public InputDialogWindow() {
    this.InitializeComponent();
  }

  public InputDialogWindow(string title, string prompt, string? initial = null) : this() {
    this.Title = title;
    if (this.FindControl<TextBlock>("PromptText") is { } pt)
      pt.Text = prompt;
    if (this.FindControl<TextBox>("InputBox") is { } box) {
      box.Text = initial ?? string.Empty;
      box.SelectAll();
      box.Focus();
    }
  }

  private void OnOkClick(object? sender, RoutedEventArgs e) {
    var value = this.FindControl<TextBox>("InputBox")?.Text?.Trim();
    this.Close(string.IsNullOrEmpty(value) ? null : value);
  }

  private void OnCancelClick(object? sender, RoutedEventArgs e) => this.Close(null);
}
