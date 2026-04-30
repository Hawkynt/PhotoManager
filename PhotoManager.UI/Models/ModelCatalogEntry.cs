using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using PhotoManager.Core.Models;

namespace PhotoManager.UI.Models;

/// <summary>
/// One row in the Models dialog. Tracks install state and an optional
/// download-in-progress indication so the UI can show an enabled/disabled
/// button and a progress bar per row.
///
/// Visual cues: installed rows get a tinted background and the action
/// button becomes "Re-download" instead of "Download" so the user can
/// tell at a glance which models are already on disk.
/// </summary>
public sealed class ModelCatalogEntry : INotifyPropertyChanged {
  // Calm colour matching the app's other "this is good" cues. Light green
  // tint that works on both light and dark themes (alpha keeps the row
  // readable on the dark theme's near-black background).
  private static readonly IBrush InstalledBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x6F, 0xCF, 0x97));
  private static readonly IBrush MissingBrush = Brushes.Transparent;

  private bool _isInstalled;
  private bool _isDownloading;
  private double _downloadFraction;
  private string _statusText = string.Empty;
  private string _progressDetail = string.Empty;

  public ModelCatalogEntry(ModelInfo model) {
    this.Model = model;
    this.Refresh();
  }

  public ModelInfo Model { get; }

  public string DisplayName => this.Model.DisplayName;
  public string Description => this.Model.Description;

  public string SizeText {
    get {
      var bytes = this.Model.TotalDownloadBytes;
      var kb = bytes / 1024.0;
      return kb < 1024
        ? $"~{kb:0} KB"
        : $"~{kb / 1024:0.#} MB";
    }
  }

  public bool IsInstalled {
    get => this._isInstalled;
    private set => this.SetProperty(ref this._isInstalled, value);
  }

  public bool IsDownloading {
    get => this._isDownloading;
    set => this.SetProperty(ref this._isDownloading, value);
  }

  public double DownloadFraction {
    get => this._downloadFraction;
    set => this.SetProperty(ref this._downloadFraction, value);
  }

  public string StatusText {
    get => this._statusText;
    set => this.SetProperty(ref this._statusText, value);
  }

  /// <summary>
  /// Right-aligned secondary line under the progress bar that carries the
  /// percentage / elapsed / ETA detail (set by the download window from
  /// the timer + Progress callback). Empty when not downloading.
  /// </summary>
  public string ProgressDetail {
    get => this._progressDetail;
    set => this.SetProperty(ref this._progressDetail, value);
  }

  public bool CanDownload => !this._isDownloading;

  /// <summary>"⬇️ Download" when the model is missing, "🔄 Re-download" when it's already on disk.</summary>
  public string DownloadButtonText => this._isInstalled ? "🔄 Re-download" : "⬇️ Download";

  /// <summary>Tinted green when installed, transparent otherwise. Bound to the row's Border.Background.</summary>
  public IBrush RowBackground => this._isInstalled ? InstalledBrush : MissingBrush;

  /// <summary>"✅ Installed" / "○ Missing" badge for the right edge of each row.</summary>
  public string StatusBadge => this._isInstalled ? "✅ Installed" : "○ Missing";

  public void Refresh() {
    this.IsInstalled = this.Model.IsInstalled();
    this.StatusText = this.IsInstalled
      ? $"Installed at {this.Model.ResolveDestination().FullName}"
      : $"Not installed. Will download to {this.Model.ResolveDestination().FullName}";
  }

  public event PropertyChangedEventHandler? PropertyChanged;

  private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null) {
    if (EqualityComparer<T>.Default.Equals(field, value))
      return;
    field = value;
    this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    if (propertyName == nameof(this.IsDownloading))
      this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.CanDownload)));
    if (propertyName == nameof(this.IsInstalled)) {
      this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.DownloadButtonText)));
      this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.RowBackground)));
      this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.StatusBadge)));
    }
  }
}
