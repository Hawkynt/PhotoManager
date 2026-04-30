using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using PhotoManager.Core.Library;
// SmartAlbumPickMode exists in both PhotoManager.UI.Models (cull-filter chip)
// and PhotoManager.Core.Library (smart-album rule clause). Alias the Core one
// here so the existing UI alias still wins for unqualified references and we
// don't have to fully-qualify every call site.
using SmartAlbumPickMode = PhotoManager.Core.Library.PickRejectFilterMode;

namespace PhotoManager.UI.Models;

/// <summary>
/// One editable row inside <c>SmartAlbumBuilderWindow</c>. Wraps a clause type
/// dropdown and the matching value editor; <see cref="ToClause"/> projects the
/// current input back to a <see cref="RuleClause"/> for save/test runs.
/// </summary>
public sealed class ClauseRowViewModel : INotifyPropertyChanged {
  public static readonly string[] AvailableTypes = {
    "Rating ≥",
    "Keyword",
    "Person",
    "Location",
    "Color label",
    "Pick state",
    "Date range",
    "GPS box"
  };

  private string _selectedType = AvailableTypes[0];
  private Control _editor = new TextBox();

  public string[] Types => AvailableTypes;

  public string SelectedType {
    get => this._selectedType;
    set {
      if (this._selectedType == value)
        return;
      this._selectedType = value;
      this.OnPropertyChanged();
      this.Editor = BuildEditor(value);
    }
  }

  public Control Editor {
    get => this._editor;
    private set {
      this._editor = value;
      this.OnPropertyChanged();
    }
  }

  public ClauseRowViewModel() {
    this.Editor = BuildEditor(this._selectedType);
  }

  public ClauseRowViewModel(RuleClause clause) {
    var (type, editor) = FromClause(clause);
    this._selectedType = type;
    this.Editor = editor;
  }

  /// <summary>
  /// Project the current editor state back to a <see cref="RuleClause"/>.
  /// Returns null when the inputs are blank or unparseable so the caller can
  /// skip incomplete rows without erroring.
  /// </summary>
  public RuleClause? ToClause() {
    return this._selectedType switch {
      "Rating ≥" => BuildMinRating(this._editor),
      "Keyword" => BuildKeyword(this._editor),
      "Person" => BuildPerson(this._editor),
      "Location" => BuildLocation(this._editor),
      "Color label" => BuildColorLabel(this._editor),
      "Pick state" => BuildPickState(this._editor),
      "Date range" => BuildDateRange(this._editor),
      "GPS box" => BuildGpsBox(this._editor),
      _ => null
    };
  }

  private static Control BuildEditor(string type) => type switch {
    "Rating ≥" => MakeRatingCombo(3),
    "Keyword" => new TextBox { Watermark = "keyword" },
    "Person" => new TextBox { Watermark = "person name" },
    "Location" => new TextBox { Watermark = "city or country" },
    "Color label" => MakeLabelCombo("Red"),
    "Pick state" => MakePickStateCombo(SmartAlbumPickMode.Picked),
    "Date range" => MakeDateRangePanel(null, null),
    "GPS box" => MakeGpsBoxPanel(0, 0, 0, 0),
    _ => new TextBox()
  };

  private static (string Type, Control Editor) FromClause(RuleClause clause) => clause switch {
    MinRatingClause c => ("Rating ≥", MakeRatingCombo(c.MinStars)),
    KeywordClause c => ("Keyword", new TextBox { Text = c.Keyword, Watermark = "keyword" }),
    PersonClause c => ("Person", new TextBox { Text = c.Person, Watermark = "person name" }),
    LocationClause c => ("Location", new TextBox { Text = c.CityOrCountry, Watermark = "city or country" }),
    ColorLabelClause c => ("Color label", MakeLabelCombo(c.Label)),
    PickStateClause c => ("Pick state", MakePickStateCombo(c.Mode)),
    DateRangeClause c => ("Date range", MakeDateRangePanel(c.From, c.To)),
    GpsBoxClause c => ("GPS box", MakeGpsBoxPanel(c.MinLat, c.MaxLat, c.MinLon, c.MaxLon)),
    _ => ("Keyword", new TextBox())
  };

  private static ComboBox MakeRatingCombo(int initial) {
    var combo = new ComboBox { ItemsSource = new[] { 1, 2, 3, 4, 5 }, SelectedItem = Math.Clamp(initial, 1, 5) };
    return combo;
  }

  private static ComboBox MakeLabelCombo(string initial) {
    var combo = new ComboBox {
      ItemsSource = new[] { "Red", "Yellow", "Green", "Blue", "Purple" },
      SelectedItem = string.IsNullOrEmpty(initial) ? "Red" : initial
    };
    return combo;
  }

  private static ComboBox MakePickStateCombo(SmartAlbumPickMode initial) {
    var combo = new ComboBox {
      ItemsSource = Enum.GetValues<SmartAlbumPickMode>(),
      SelectedItem = initial
    };
    return combo;
  }

  private static StackPanel MakeDateRangePanel(DateTime? from, DateTime? to) {
    var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
    var fromBox = new TextBox { Watermark = "from yyyy-mm-dd", Width = 130 };
    var toBox = new TextBox { Watermark = "to yyyy-mm-dd", Width = 130 };
    if (from is { } f) fromBox.Text = f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    if (to is { } t) toBox.Text = t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    panel.Children.Add(fromBox);
    panel.Children.Add(toBox);
    panel.Tag = new[] { fromBox, toBox };
    return panel;
  }

  private static StackPanel MakeGpsBoxPanel(double minLat, double maxLat, double minLon, double maxLon) {
    var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
    var minLatBox = new TextBox { Watermark = "minLat", Width = 80, Text = Fmt(minLat) };
    var maxLatBox = new TextBox { Watermark = "maxLat", Width = 80, Text = Fmt(maxLat) };
    var minLonBox = new TextBox { Watermark = "minLon", Width = 80, Text = Fmt(minLon) };
    var maxLonBox = new TextBox { Watermark = "maxLon", Width = 80, Text = Fmt(maxLon) };
    panel.Children.Add(minLatBox);
    panel.Children.Add(maxLatBox);
    panel.Children.Add(minLonBox);
    panel.Children.Add(maxLonBox);
    panel.Tag = new[] { minLatBox, maxLatBox, minLonBox, maxLonBox };
    return panel;
  }

  private static string Fmt(double v) => v == 0 ? string.Empty : v.ToString("0.######", CultureInfo.InvariantCulture);

  private static MinRatingClause? BuildMinRating(Control editor)
    => editor is ComboBox cb && cb.SelectedItem is int v ? new MinRatingClause(v) : null;

  private static KeywordClause? BuildKeyword(Control editor)
    => editor is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text) ? new KeywordClause(tb.Text.Trim()) : null;

  private static PersonClause? BuildPerson(Control editor)
    => editor is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text) ? new PersonClause(tb.Text.Trim()) : null;

  private static LocationClause? BuildLocation(Control editor)
    => editor is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text) ? new LocationClause(tb.Text.Trim()) : null;

  private static ColorLabelClause? BuildColorLabel(Control editor)
    => editor is ComboBox cb && cb.SelectedItem is string s ? new ColorLabelClause(s) : null;

  private static PickStateClause? BuildPickState(Control editor)
    => editor is ComboBox cb && cb.SelectedItem is SmartAlbumPickMode m ? new PickStateClause(m) : null;

  private static DateRangeClause? BuildDateRange(Control editor) {
    if (editor.Tag is not TextBox[] boxes || boxes.Length != 2)
      return null;
    var from = TryParseDate(boxes[0].Text);
    var to = TryParseDate(boxes[1].Text);
    if (from is null && to is null)
      return null;
    return new DateRangeClause(from, to);
  }

  private static GpsBoxClause? BuildGpsBox(Control editor) {
    if (editor.Tag is not TextBox[] boxes || boxes.Length != 4)
      return null;
    if (TryParseDouble(boxes[0].Text) is not { } minLat
        || TryParseDouble(boxes[1].Text) is not { } maxLat
        || TryParseDouble(boxes[2].Text) is not { } minLon
        || TryParseDouble(boxes[3].Text) is not { } maxLon)
      return null;
    return new GpsBoxClause(minLat, maxLat, minLon, maxLon);
  }

  private static DateTime? TryParseDate(string? text) {
    if (string.IsNullOrWhiteSpace(text))
      return null;
    return DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
      ? d
      : null;
  }

  private static double? TryParseDouble(string? text) {
    if (string.IsNullOrWhiteSpace(text))
      return null;
    var normalized = text.Trim().Replace(',', '.');
    return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
  }

  public event PropertyChangedEventHandler? PropertyChanged;

  private void OnPropertyChanged([CallerMemberName] string? name = null)
    => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
