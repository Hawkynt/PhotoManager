using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Hawkynt.PhotoManager.Core.Faces;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Result returned when the user picks a named cluster to merge into.
/// </summary>
public sealed record MergeSuggestionResult(FaceCluster TargetCluster, float Similarity);

/// <summary>
/// "Is this the same person?" dialog. Shows the top-N nearest named clusters
/// by centroid-to-centroid cosine similarity. The user clicks a row to
/// confirm the merge, or cancels to leave the cluster unnamed. No disk IO —
/// the caller (FaceGalleryWindow) handles the actual XMP write if the user
/// confirms.
/// </summary>
public partial class MergeSuggestionDialog : Window {
  public MergeSuggestionDialog() {
    this.InitializeComponent();
  }

  public MergeSuggestionDialog(
    FaceCluster sourceCluster,
    IReadOnlyList<(FaceCluster Cluster, float Similarity)> suggestions
  ) {
    this.InitializeComponent();

    if (this.FindControl<TextBlock>("HeaderText") is { } header)
      header.Text = $"Who is \"{sourceCluster.DisplayName}\" ({sourceCluster.Count} face(s))?";

    var panel = this.FindControl<StackPanel>("SuggestionsList");
    if (panel == null)
      return;

    if (suggestions.Count == 0) {
      panel.Children.Add(new TextBlock {
        Text = "No named clusters with embeddings found.",
        Opacity = 0.75
      });
      return;
    }

    foreach (var (cluster, similarity) in suggestions) {
      var row = new Button {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Avalonia.Thickness(8, 6),
        Tag = new MergeSuggestionResult(cluster, similarity),
        Content = new Grid {
          ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"),
          Children = {
            SetColumn(new TextBlock {
              Text = cluster.DisplayName,
              FontWeight = FontWeight.SemiBold,
              VerticalAlignment = VerticalAlignment.Center,
              TextTrimming = TextTrimming.CharacterEllipsis
            }, 0),
            SetColumn(new TextBlock {
              Text = $"{cluster.Count} face(s)",
              FontSize = 11,
              Opacity = 0.7,
              VerticalAlignment = VerticalAlignment.Center,
              Margin = new Avalonia.Thickness(8, 0)
            }, 1),
            SetColumn(new TextBlock {
              Text = $"{similarity:P0} similar",
              FontSize = 11,
              FontWeight = FontWeight.SemiBold,
              VerticalAlignment = VerticalAlignment.Center,
              Foreground = similarity >= 0.7f
                ? Brushes.Green
                : similarity >= 0.5f
                  ? Brushes.DarkGoldenrod
                  : Brushes.Gray
            }, 2)
          }
        }
      };
      row.Click += this.OnSuggestionClick;
      panel.Children.Add(row);
    }
  }

  private void OnSuggestionClick(object? sender, RoutedEventArgs e) {
    if (sender is not Button { Tag: MergeSuggestionResult result })
      return;
    this.Close(result);
  }

  private void OnCancelClick(object? sender, RoutedEventArgs e) => this.Close(null);

  private static T SetColumn<T>(T control, int column) where T : Control {
    Grid.SetColumn(control, column);
    return control;
  }
}
