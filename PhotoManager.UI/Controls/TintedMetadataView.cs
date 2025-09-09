using PhotoManager.UI.Models;
using PhotoManager.Core.Enums;
using PhotoManager.UI.Resources;

namespace PhotoManager.UI.Controls;

/// <summary>
/// Custom metadata view that can tint individual cells containing missing values or winning dates
/// </summary>
public class TintedMetadataView : ListView {
  private readonly Color _salmonTint = Color.LightSalmon;
  private readonly Color _limeTint = Color.LightGreen;
  private readonly Color _defaultBackground = SystemColors.Window;
  private readonly HashSet<string> _missingValueIndicators;
  
  public TintedMetadataView() {
    this._missingValueIndicators = [
      Strings.Metadata_NotFound,
      Strings.Metadata_NotDetected
    ];

    this.InitializeView();
  }

  private void InitializeView() {
    this.View = View.Details;
    this.FullRowSelect = true;
    this.GridLines = true;
    this.HeaderStyle = ColumnHeaderStyle.Nonclickable;
    this.MultiSelect = false;
    this.HideSelection = false;
    this.OwnerDraw = true;
    
    // Clear any existing columns first
    this.Columns.Clear();
    
    // Add exactly two columns with explicit width control
    this.Columns.Add("Property", 120);
    this.Columns.Add("Value", 200); // Start with fixed width, will resize dynamically
    
    // Wire up custom drawing events
    this.DrawColumnHeader += OnDrawColumnHeader;
    this.DrawSubItem += this.OnDrawSubItem;
    
    // Handle resizing to maintain two-column layout
    this.SizeChanged += this.OnSizeChanged;
  }
  
  private void OnSizeChanged(object? sender, EventArgs e) {
    // Adjust second column to fill remaining width
    if (this.Columns.Count == 2) {
      var availableWidth = this.ClientSize.Width - this.Columns[0].Width - SystemInformation.VerticalScrollBarWidth;
      if (availableWidth > 50) // Minimum width for readability
        this.Columns[1].Width = availableWidth;
    }
  }

  public void SetMetadata(FileMetadataModel model, DateTimeSource winningSource = DateTimeSource.Unknown) {
    this.Items.Clear();

    // TODO: generate this list dynamically via reflection?
    var properties = new[] {
      ("File Name", model.FileName, DateTimeSource.Unknown),
      ("File Path", model.FilePath, DateTimeSource.Unknown),
      ("File Size", model.FileSize, DateTimeSource.Unknown),
      ("Date Created", model.DateCreated, DateTimeSource.FileCreatedAt),
      ("Date Modified", model.DateModified, DateTimeSource.FileModifiedAt),
      ("EXIF Original Date", model.ExifOriginalDate ?? Strings.Metadata_NotFound, DateTimeSource.ExifSubIfd),
      ("EXIF Modified Date", model.ExifModifiedDate ?? Strings.Metadata_NotFound, DateTimeSource.ExifIfd0),
      ("GPS Date", model.GpsDate ?? Strings.Metadata_NotFound, DateTimeSource.Gps),
      ("Filename Date", model.FilenameDate ?? Strings.Metadata_NotDetected, DateTimeSource.FileName),
    };

    foreach (var (name, value, source) in properties) {
      var item = new ListViewItem(name);
      item.SubItems.Add(value);
      
      // Store tinting info: missing value (salmon) or winning source (lime)
      var isMissing = this._missingValueIndicators.Contains(value ?? string.Empty);
      var isWinner = source != DateTimeSource.Unknown && source == winningSource;
      item.Tag = (IsMissing: isMissing, IsWinner: isWinner);
      
      this.Items.Add(item);
    }
  }

  private static void OnDrawColumnHeader(object? __, DrawListViewColumnHeaderEventArgs e) => e.DrawDefault = true;

  private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e) {
    var tagInfo = e.Item?.Tag as (bool IsMissing, bool IsWinner)?;
    var isValueColumn = e.ColumnIndex == 1; // Value column
    
    var backgroundColor = this._defaultBackground;
    var textColor = Color.Black;
    
    if (isValueColumn && tagInfo != null) {
      if (tagInfo.Value.IsWinner) {
        // Lime background for winning date source
        backgroundColor = this._limeTint;
        textColor = Color.DarkGreen;
      } else if (tagInfo.Value.IsMissing) {
        // Salmon background for missing values
        backgroundColor = this._salmonTint;
        textColor = Color.DarkRed;
      }
    }
    
    // Fill background
    using (var brush = new SolidBrush(backgroundColor))
      e.Graphics.FillRectangle(brush, e.Bounds);
    
    // Draw text
    if (e.SubItem != null)
      TextRenderer.DrawText(e.Graphics, e.SubItem.Text, this.Font, e.Bounds, textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    
    // Draw focus rectangle if selected
    if (e.Item?.Selected == true)
      e.DrawFocusRectangle(e.Bounds);

  }
}