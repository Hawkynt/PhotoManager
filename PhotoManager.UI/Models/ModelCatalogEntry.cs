using System.ComponentModel;
using System.Runtime.CompilerServices;
using PhotoManager.Core.Models;

namespace PhotoManager.UI.Models;

/// <summary>
/// One row in the Models dialog. Tracks install state and an optional
/// download-in-progress indication so the UI can show an enabled/disabled
/// button and a progress bar per row.
/// </summary>
public sealed class ModelCatalogEntry : INotifyPropertyChanged {
  private bool _isInstalled;
  private bool _isDownloading;
  private double _downloadFraction;
  private string _statusText = string.Empty;

  public ModelCatalogEntry(ModelInfo model) {
    this.Model = model;
    this.Refresh();
  }

  public ModelInfo Model { get; }

  public string DisplayName => this.Model.DisplayName;
  public string Description => this.Model.Description;

  public string SizeText {
    get {
      var kb = this.Model.ApproximateSizeBytes / 1024.0;
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

  public bool CanDownload => !this._isDownloading;

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
  }
}
