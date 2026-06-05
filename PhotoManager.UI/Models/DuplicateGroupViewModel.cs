using System.Collections.ObjectModel;
using Hawkynt.PhotoManager.Core.Library;

namespace Hawkynt.PhotoManager.UI.Models;

/// <summary>
/// One cluster of near-duplicate files in the duplicates window. Holds the
/// member tile view-models in an <see cref="ObservableCollection{T}"/> so
/// thumbnails can stream in without rebinding the surrounding ItemsControl.
/// </summary>
public sealed class DuplicateGroupViewModel {
  public DuplicateGroupViewModel(DuplicateGroup group, int index) {
    this.Index = index;
    this.Members = new ObservableCollection<DuplicateMemberViewModel>(
      group.Files.Zip(group.Distances, (f, d) => new DuplicateMemberViewModel(f, d))
    );
    this.Header = $"Group {index + 1} — {group.Count} files";
  }

  public int Index { get; }
  public string Header { get; }
  public ObservableCollection<DuplicateMemberViewModel> Members { get; }
}
