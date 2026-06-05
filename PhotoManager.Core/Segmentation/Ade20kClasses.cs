namespace Hawkynt.PhotoManager.Core.Segmentation;

/// <summary>
/// Subset of ADE20K's 150 semantic classes exposed in the UI for mask
/// generation. Indices match the SegFormer-B0 (ADE20K) ONNX output's
/// channel order — same as the upstream `id2label` mapping in the
/// nvidia/segformer-b0-finetuned-ade-512-512 config.
///
/// The full 150-class list is intentionally NOT enumerated here — only
/// the classes a typical photo editor wants to mask. Add more as new
/// use cases come up; the OnnxAdeSegmenter accepts any 0..149 index.
/// </summary>
public static class Ade20kClasses {
  // Indices below taken directly from the ADE20K id2label mapping.
  public const int Wall      = 0;
  public const int Building  = 1;
  public const int Sky       = 2;
  public const int Floor     = 3;
  public const int Tree      = 4;
  public const int Ceiling   = 5;
  public const int Road      = 6;
  public const int Bed       = 7;
  public const int Windowpane = 8;
  public const int Grass     = 9;
  public const int Cabinet   = 10;
  public const int Sidewalk  = 11;
  public const int Person    = 12;
  public const int Earth     = 13;
  public const int Door      = 14;
  public const int Table     = 15;
  public const int Mountain  = 16;
  public const int Plant     = 17;
  public const int Water     = 21;
  public const int Painting  = 22;
  public const int Sea       = 26;
  public const int Mirror    = 27;
  public const int Rug       = 28;
  public const int Field     = 29;
  public const int House     = 25;
  public const int Rock      = 34;
  public const int Sand      = 46;
  public const int River     = 60;
  public const int Snow      = 65;
  public const int Hill      = 68;
  public const int Path      = 52;
  public const int Lake      = 128;
  public const int Pool      = 109;
  public const int WaterfallC = 113;

  /// <summary>
  /// User-facing class set surfaced in the EditImageWindow class flyout.
  /// Display order is the order users will pick from. Sky / subject have
  /// their own dedicated buttons so they are intentionally omitted here.
  /// </summary>
  public sealed record AdeClass(int Index, string DisplayName, string Emoji);

  public static readonly IReadOnlyList<AdeClass> FlyoutClasses = [
    new(Grass,    "Grass",            "🌱"),
    new(Tree,     "Trees",            "🌳"),
    new(Plant,    "Plants / foliage", "🌿"),
    new(Water,    "Water",            "💧"),
    new(Sea,      "Sea",              "🌊"),
    new(Mountain, "Mountains",        "⛰️"),
    new(Building, "Buildings",        "🏛️"),
    new(Road,     "Road",             "🛣️"),
    new(Sand,     "Sand",             "🏖️"),
    new(Rock,     "Rocks",            "🪨"),
    new(Snow,     "Snow",             "❄️"),
    new(Person,   "People",           "🧍"),
  ];
}
