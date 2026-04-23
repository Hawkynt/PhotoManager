using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PhotoManager.UI.Views;

/// <summary>
/// Dialog for attaching a person's name to a region. Autocomplete picks up
/// existing registry names; typing an unknown name either just tags the
/// region (if the checkbox is off) or registers the person with the region's
/// embedding as the first reference (if the checkbox is on and an embedding
/// exists). Returns a <see cref="Result"/> on OK; null on Cancel.
/// </summary>
public partial class PersonPickerWindow : Window {
  public PersonPickerWindow() {
    this.InitializeComponent();
  }

  /// <summary>
  /// <paramref name="existingNames"/> populates the autocomplete; <paramref name="initialName"/>
  /// prefills the input; <paramref name="canAddToRegistry"/> toggles whether the
  /// "add to registry" checkbox is actually useful (only meaningful if the
  /// region has an embedding).
  /// </summary>
  public PersonPickerWindow(IEnumerable<string> existingNames, string? initialName, bool canAddToRegistry) : this() {
    if (this.FindControl<AutoCompleteBox>("NameBox") is { } box) {
      box.ItemsSource = existingNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
      box.Text = initialName ?? string.Empty;
    }

    if (this.FindControl<CheckBox>("AddToRegistryBox") is { } addBox) {
      addBox.IsEnabled = canAddToRegistry;
      addBox.IsChecked = canAddToRegistry;
      if (!canAddToRegistry)
        addBox.Content = "Add to registry (region has no embedding — name only)";
    }
  }

  /// <summary>Outcome of the dialog. <see cref="Name"/> null means the user asked to clear the label.</summary>
  public sealed record Result(string? Name, bool AddToRegistry);

  private void OnOkClick(object? sender, RoutedEventArgs e) {
    var name = this.FindControl<AutoCompleteBox>("NameBox")?.Text?.Trim();
    var addToRegistry = this.FindControl<CheckBox>("AddToRegistryBox")?.IsChecked ?? false;
    if (string.IsNullOrWhiteSpace(name)) {
      // Empty name is equivalent to "clear the label" — keep the UX predictable.
      this.Close(new Result(null, false));
      return;
    }
    this.Close(new Result(name, addToRegistry));
  }

  private void OnClearClick(object? sender, RoutedEventArgs e) => this.Close(new Result(null, false));

  private void OnCancelClick(object? sender, RoutedEventArgs e) => this.Close(null);
}
