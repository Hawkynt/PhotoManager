using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Hawkynt.PhotoManager.Core.Detection;
using Hawkynt.PhotoManager.Core.Library;
using Hawkynt.PhotoManager.Core.Metadata;
using Hawkynt.PhotoManager.UI.Models;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// View-model wrapper around <see cref="KeywordNode"/>. The data record uses
/// plain <c>List&lt;T&gt;</c> for children so JSON serialisation stays trivial;
/// the dialog needs <see cref="ObservableCollection{T}"/> so the TreeView
/// reacts to add/remove/move without manual refresh.
/// </summary>
public sealed class KeywordTreeNodeViewModel : INotifyPropertyChanged {
  private string _name;

  public KeywordTreeNodeViewModel(string name) {
    this._name = name;
    this.Children = new ObservableCollection<KeywordTreeNodeViewModel>();
  }

  public KeywordTreeNodeViewModel(KeywordNode node) {
    this._name = node.Name;
    this.Children = new ObservableCollection<KeywordTreeNodeViewModel>(
      node.Children.Select(c => new KeywordTreeNodeViewModel(c))
    );
  }

  public string Name {
    get => this._name;
    set {
      if (this._name == value)
        return;
      this._name = value;
      this.OnPropertyChanged();
    }
  }

  public ObservableCollection<KeywordTreeNodeViewModel> Children { get; }

  public KeywordNode ToData() => new(this.Name, this.Children.Select(c => c.ToData()));

  public event PropertyChangedEventHandler? PropertyChanged;

  private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Editor for the user's hierarchical keyword tree. Users build a tree of
/// tags (e.g. <c>Travel &gt; Italy &gt; Rome</c>); on Apply the highlighted
/// node + every ancestor flatten to <c>dc:subject</c> keywords on every
/// selected file in the main window grid. The tree itself is persisted as
/// JSON in <c>UserSettingsData.KeywordTreeRoots</c>.
/// </summary>
public partial class KeywordTreeWindow : Window {
  private readonly ObservableCollection<KeywordTreeNodeViewModel> _roots = new();
  private readonly IReadOnlyList<FileItemModel> _selectedFiles;
  private readonly IMetadataReader _metadataReader;
  private readonly IMetadataWriter _metadataWriter;

  public KeywordTreeWindow() : this(Array.Empty<KeywordNode>(), Array.Empty<FileItemModel>(), new MetadataReader(), new CompositeMetadataWriter()) { }

  public KeywordTreeWindow(
    IEnumerable<KeywordNode> initialRoots,
    IReadOnlyList<FileItemModel> selectedFiles,
    IMetadataReader metadataReader,
    IMetadataWriter metadataWriter
  ) {
    this._selectedFiles = selectedFiles;
    this._metadataReader = metadataReader;
    this._metadataWriter = metadataWriter;

    this.InitializeComponent();

    foreach (var node in initialRoots)
      this._roots.Add(new KeywordTreeNodeViewModel(node));

    if (this.FindControl<TreeView>("KeywordTree") is { } tree)
      tree.ItemsSource = this._roots;

    this.SetStatus(selectedFiles.Count == 0
      ? "No files selected — Apply needs a grid selection."
      : $"{selectedFiles.Count} file(s) selected.");
  }

  /// <summary>Snapshot of the current edit state, in plain-data form.</summary>
  public IReadOnlyList<KeywordNode> CurrentRoots
    => this._roots.Select(r => r.ToData()).ToList();

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } st)
      st.Text = message;
  }

  private KeywordTreeNodeViewModel? GetSelectedNode()
    => this.FindControl<TreeView>("KeywordTree")?.SelectedItem as KeywordTreeNodeViewModel;

  private (KeywordTreeNodeViewModel? Parent, ObservableCollection<KeywordTreeNodeViewModel> Siblings, int Index)
    LocateSelected() {
    var sel = this.GetSelectedNode();
    if (sel == null)
      return (null, this._roots, -1);

    var rootIndex = this._roots.IndexOf(sel);
    if (rootIndex >= 0)
      return (null, this._roots, rootIndex);

    foreach (var ancestor in this.AllNodes()) {
      var idx = ancestor.Children.IndexOf(sel);
      if (idx >= 0)
        return (ancestor, ancestor.Children, idx);
    }

    return (null, this._roots, -1);
  }

  private IEnumerable<KeywordTreeNodeViewModel> AllNodes() {
    foreach (var r in this._roots)
      foreach (var n in Walk(r))
        yield return n;

    static IEnumerable<KeywordTreeNodeViewModel> Walk(KeywordTreeNodeViewModel node) {
      yield return node;
      foreach (var c in node.Children)
        foreach (var n in Walk(c))
          yield return n;
    }
  }

  private async void OnAddRootClick(object? sender, RoutedEventArgs e) {
    var name = await this.PromptForNameAsync("Add root keyword", "New top-level keyword:");
    if (string.IsNullOrWhiteSpace(name))
      return;
    this._roots.Add(new KeywordTreeNodeViewModel(name));
    this.SetStatus($"Added root '{name}'.");
  }

  private async void OnAddChildClick(object? sender, RoutedEventArgs e) {
    var parent = this.GetSelectedNode();
    if (parent == null) {
      this.SetStatus("Select a parent node first.");
      return;
    }

    var name = await this.PromptForNameAsync("Add child", $"Child of '{parent.Name}':");
    if (string.IsNullOrWhiteSpace(name))
      return;
    parent.Children.Add(new KeywordTreeNodeViewModel(name));
    this.SetStatus($"Added '{name}' under '{parent.Name}'.");
  }

  private async void OnRenameClick(object? sender, RoutedEventArgs e) {
    var sel = this.GetSelectedNode();
    if (sel == null) {
      this.SetStatus("Select a node first.");
      return;
    }
    var newName = await this.PromptForNameAsync("Rename keyword", "New name:", sel.Name);
    if (string.IsNullOrWhiteSpace(newName))
      return;
    sel.Name = newName;
    this.SetStatus($"Renamed to '{newName}'.");
  }

  private void OnDeleteClick(object? sender, RoutedEventArgs e) {
    var (_, siblings, index) = this.LocateSelected();
    if (index < 0) {
      this.SetStatus("Select a node first.");
      return;
    }
    var name = siblings[index].Name;
    siblings.RemoveAt(index);
    this.SetStatus($"Deleted '{name}'.");
  }

  private void OnMoveUpClick(object? sender, RoutedEventArgs e) {
    var (_, siblings, index) = this.LocateSelected();
    if (index <= 0)
      return;
    siblings.Move(index, index - 1);
  }

  private void OnMoveDownClick(object? sender, RoutedEventArgs e) {
    var (_, siblings, index) = this.LocateSelected();
    if (index < 0 || index >= siblings.Count - 1)
      return;
    siblings.Move(index, index + 1);
  }

  private async void OnApplyToSelectionClick(object? sender, RoutedEventArgs e) {
    var sel = this.GetSelectedNode();
    if (sel == null) {
      this.SetStatus("Select a node to apply.");
      return;
    }
    if (this._selectedFiles.Count == 0) {
      this.SetStatus("No files selected in the main grid.");
      return;
    }

    var rootsData = this.CurrentRoots;
    var expanded = KeywordHierarchy.Expand(rootsData, new[] { sel.Name });

    var written = 0;
    var errors = 0;
    foreach (var item in this._selectedFiles) {
      if (item.FileInfo is not { Exists: true } file)
        continue;
      try {
        var existing = await this._metadataReader.ReadAsync(file);
        var merged = DetectionService.MergeKeywords(existing.Keywords, expanded);
        var edit = new MetadataEdit {
          Keywords = Optional<IReadOnlyList<string>>.Set(merged)
        };
        await this._metadataWriter.ApplyAsync(file, edit);
        written++;
      } catch {
        errors++;
      }
    }

    this.SetStatus(errors == 0
      ? $"Wrote {expanded.Count} keyword(s) to {written} file(s)."
      : $"Wrote to {written}; {errors} failed.");
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();

  private async Task<string?> PromptForNameAsync(string title, string prompt, string? initial = null) {
    var dialog = new InputDialogWindow(title, prompt, initial);
    var result = await dialog.ShowDialog<string?>(this);
    return result;
  }
}
