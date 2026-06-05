using Avalonia.Controls;
using Avalonia.Interactivity;
using Hawkynt.PhotoManager.Core.Metadata;

namespace Hawkynt.PhotoManager.UI.Views;

/// <summary>
/// Small dialog showing the current state of the metadata write-back queue.
/// Pending items are display-only; failed items can be retried or discarded.
/// </summary>
public partial class WriteQueueDialog : Window {
  private readonly WriteBackQueue _queue;

  public WriteQueueDialog() {
    this._queue = null!;
    this.InitializeComponent();
  }

  public WriteQueueDialog(WriteBackQueue queue) {
    this._queue = queue;
    this.InitializeComponent();
    this.Refresh();

    queue.QueueChanged += () =>
      Avalonia.Threading.Dispatcher.UIThread.Post(this.Refresh);
  }

  private void Refresh() {
    var pendingList = this.FindControl<ListBox>("PendingList");
    var failedList = this.FindControl<ListBox>("FailedList");

    if (pendingList is not null)
      pendingList.ItemsSource = this._queue.GetPending()
        .Select(i => $"{Path.GetFileName(i.FilePath)}  (retry #{i.RetryCount}, queued {i.EnqueuedUtc:u})")
        .ToList();

    if (failedList is not null) {
      var failed = this._queue.GetFailed();
      failedList.ItemsSource = failed
        .Select(i => $"{Path.GetFileName(i.FilePath)}  (retries: {i.RetryCount}, queued {i.EnqueuedUtc:u})")
        .ToList();
      failedList.Tag = failed;
    }
  }

  private void OnRetryClick(object? sender, RoutedEventArgs e) {
    var failedList = this.FindControl<ListBox>("FailedList");
    if (failedList?.SelectedIndex is not { } idx || idx < 0)
      return;
    if (failedList.Tag is not IReadOnlyList<WriteBackItem> items || idx >= items.Count)
      return;

    this._queue.RetryFailed(items[idx]);
  }

  private void OnDiscardClick(object? sender, RoutedEventArgs e) {
    var failedList = this.FindControl<ListBox>("FailedList");
    if (failedList?.SelectedIndex is not { } idx || idx < 0)
      return;
    if (failedList.Tag is not IReadOnlyList<WriteBackItem> items || idx >= items.Count)
      return;

    this._queue.DiscardFailed(items[idx]);
  }

  private void OnCloseClick(object? sender, RoutedEventArgs e) => this.Close();
}
