using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Hawkynt.PhotoManager.Core.Library;
using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.UI.Models;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Builds and edits <see cref="SmartAlbumRule"/> records. Returns the newest
/// list of albums via <see cref="ShowDialog{TResult}(Window)"/>; the caller is
/// responsible for persisting via <c>MainController.SaveSettingsAsync</c>.
/// </summary>
public partial class SmartAlbumBuilderWindow : Window {
  private readonly ObservableCollection<ClauseRowViewModel> _clauses = new();
  private List<SmartAlbumRule> _albums = new();
  private readonly Func<IReadOnlyList<(FileInfo File, FullMetadata Metadata)>>? _snapshotProvider;

  public SmartAlbumBuilderWindow() : this(new List<SmartAlbumRule>(), null) { }

  /// <param name="initial">Existing albums to seed the dialog with — round-trip preserves order.</param>
  /// <param name="snapshotProvider">Optional snapshot factory; when supplied, "Test on current library" runs the rule against it.</param>
  public SmartAlbumBuilderWindow(
    IReadOnlyList<SmartAlbumRule> initial,
    Func<IReadOnlyList<(FileInfo File, FullMetadata Metadata)>>? snapshotProvider
  ) {
    this.InitializeComponent();
    this._albums = initial.Select(a => a with { Clauses = (a.Clauses ?? Array.Empty<RuleClause>()).ToArray() }).ToList();
    this._snapshotProvider = snapshotProvider;

    if (this.FindControl<ItemsControl>("ClausesList") is { } list)
      list.ItemsSource = this._clauses;

    this.RefreshAlbumsCombo();
  }

  /// <summary>Final album list as edited by the user. Always non-null after <see cref="ShowDialog"/>.</summary>
  public List<SmartAlbumRule> Albums => this._albums;

  private void RefreshAlbumsCombo() {
    if (this.FindControl<ComboBox>("AlbumsCombo") is not { } combo)
      return;
    var prior = combo.SelectedItem as string;
    combo.ItemsSource = this._albums.Select(a => a.Name).ToList();
    if (prior is { } p && this._albums.Any(a => a.Name == p))
      combo.SelectedItem = p;
    else
      combo.SelectedIndex = -1;
  }

  private void OnAlbumSelected(object? sender, SelectionChangedEventArgs e) {
    if (sender is not ComboBox combo)
      return;
    if (combo.SelectedItem is not string name)
      return;
    var match = this._albums.FirstOrDefault(a => a.Name == name);
    if (match is null)
      return;

    if (this.FindControl<TextBox>("NameBox") is { } nb)
      nb.Text = match.Name;

    this._clauses.Clear();
    foreach (var clause in match.Clauses ?? Array.Empty<RuleClause>())
      this._clauses.Add(new ClauseRowViewModel(clause));

    this.SetStatus($"Loaded \"{match.Name}\" ({match.Clauses?.Length ?? 0} clauses).");
  }

  private void OnAddClauseClick(object? sender, RoutedEventArgs e) {
    this._clauses.Add(new ClauseRowViewModel());
  }

  private void OnRemoveClauseClick(object? sender, RoutedEventArgs e) {
    if (sender is not Button { Tag: ClauseRowViewModel row })
      return;
    this._clauses.Remove(row);
  }

  private void OnDeleteAlbumClick(object? sender, RoutedEventArgs e) {
    if (this.FindControl<ComboBox>("AlbumsCombo") is not { } combo)
      return;
    if (combo.SelectedItem is not string name)
      return;
    this._albums = this._albums.Where(a => a.Name != name).ToList();
    this._clauses.Clear();
    if (this.FindControl<TextBox>("NameBox") is { } nb)
      nb.Text = string.Empty;
    this.RefreshAlbumsCombo();
    this.SetStatus($"Deleted \"{name}\".");
  }

  private void OnTestClick(object? sender, RoutedEventArgs e) {
    if (this._snapshotProvider is null) {
      this.SetStatus("No library snapshot available — scan files in the main window first.");
      return;
    }
    var rule = this.BuildRuleFromUi(allowEmptyName: true);
    if (rule is null) {
      this.SetStatus("Add at least one valid clause before testing.");
      return;
    }
    var snapshot = this._snapshotProvider();
    var hits = SmartAlbumEvaluator.Evaluate(snapshot, rule);
    this.SetStatus($"{hits.Count} of {snapshot.Count} files match.");
  }

  private void OnSaveClick(object? sender, RoutedEventArgs e) {
    var name = (this.FindControl<TextBox>("NameBox")?.Text ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(name)) {
      this.SetStatus("Album needs a name.");
      return;
    }
    var rule = this.BuildRuleFromUi(allowEmptyName: false);
    if (rule is null) {
      this.SetStatus("Add at least one valid clause before saving.");
      return;
    }

    this._albums = this._albums.Where(a => !string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
    this._albums.Add(rule);

    this.Close(this._albums);
  }

  private void OnCancelClick(object? sender, RoutedEventArgs e) => this.Close(null);

  private SmartAlbumRule? BuildRuleFromUi(bool allowEmptyName) {
    var name = (this.FindControl<TextBox>("NameBox")?.Text ?? string.Empty).Trim();
    if (!allowEmptyName && string.IsNullOrWhiteSpace(name))
      return null;

    var clauses = this._clauses
      .Select(r => r.ToClause())
      .Where(c => c is not null)
      .Cast<RuleClause>()
      .ToArray();

    if (clauses.Length == 0)
      return null;

    return new SmartAlbumRule {
      Name = name,
      Clauses = clauses,
      LogicOp = LogicalOp.And
    };
  }

  private void SetStatus(string text) {
    if (this.FindControl<TextBlock>("StatusText") is { } t)
      t.Text = text;
  }
}
