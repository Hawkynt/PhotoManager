using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PhotoManager.UI.Models;

public class FileItemModel : INotifyPropertyChanged {
  private string _fileName = string.Empty;
  private string _targetLocation = string.Empty;
  private string _sourcePath = string.Empty;
  private bool _isPick;
  private bool _isReject;

  public string FileName {
    get => this._fileName;
    set => this.SetProperty(ref this._fileName, value);
  }

  public string TargetLocation {
    get => this._targetLocation;
    set => this.SetProperty(ref this._targetLocation, value);
  }

  public string SourcePath {
    get => this._sourcePath;
    set => this.SetProperty(ref this._sourcePath, value);
  }

  [Browsable(false)]
  public FileInfo? FileInfo { get; set; }

  /// <summary>
  /// Lower-cased concatenation of every searchable field for this file
  /// (filename, folder, keywords, people, locations, title, caption…). The
  /// MainWindow search bar does substring contains against this string, so
  /// a single cheap string-scan per row powers the multi-token AND search
  /// without re-reading XMP per keystroke. Populated by the background
  /// metadata-reading pass after the scan.
  /// </summary>
  [Browsable(false)]
  public string SearchIndex { get; set; } = string.Empty;

  /// <summary>XMP rating in the range -1 (rejected) … 5 (best). Null = unrated. Populated alongside SearchIndex.</summary>
  [Browsable(false)]
  public int? Rating { get; set; }

  /// <summary>Adobe-style color label ("Red", "Yellow", "Green", "Blue", "Purple", or null).</summary>
  [Browsable(false)]
  public string? ColorLabel { get; set; }

  /// <summary>Picasa-style cull flag (xmp:Pick=true). Mutually exclusive with <see cref="IsReject"/>.</summary>
  public bool IsPick {
    get => this._isPick;
    set => this.SetProperty(ref this._isPick, value);
  }

  /// <summary>Picasa-style cull flag (xmp:Reject=true). Mutually exclusive with <see cref="IsPick"/>.</summary>
  public bool IsReject {
    get => this._isReject;
    set => this.SetProperty(ref this._isReject, value);
  }

  /// <summary>Composite glyph for the pick/reject column. Empty when neither flag is set.</summary>
  public string PickRejectGlyph => this._isPick ? "✅" : this._isReject ? "❌" : string.Empty;

  /// Capture date pulled from metadata; feeds the timeline scrubber.
  public DateTime? CapturedDate { get; set; }

  /// Cached metadata snapshot from the post-scan pass; feeds Memories' "on this trip" lookup.
  public PhotoManager.Core.Metadata.FullMetadata? CachedMetadata { get; set; }

  private bool _isInQuickCollection;
  /// True when this row is in the Quick Collection bucket.
  public bool IsInQuickCollection {
    get => this._isInQuickCollection;
    set {
      this.SetProperty(ref this._isInQuickCollection, value);
      this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.QuickCollectionBadge)));
    }
  }
  public string QuickCollectionBadge => this._isInQuickCollection ? "★" : string.Empty;

  /// 0 for the source file's main XMP; >0 for a sibling IMG.copyN.xmp virtual copy.
  public int CopyIndex { get; set; }
  public bool IsVirtualCopy => this.CopyIndex > 0;

  public event PropertyChangedEventHandler? PropertyChanged;

  private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null) {
    if (EqualityComparer<T>.Default.Equals(field, value))
      return;

    field = value;
    this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    if (propertyName is nameof(IsPick) or nameof(IsReject))
      this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.PickRejectGlyph)));
  }
}
