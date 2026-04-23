using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PhotoManager.Core.Geocoding;
using PhotoManager.Core.Metadata;

namespace PhotoManager.UI.Views;

/// <summary>
/// GeoSetter-style multi-tab property editor for a single photo. Tabs: General
/// (title/caption/keywords/rating/label), Location (place fields + GPS with
/// pick-on-map, resolve-address, fill-altitude), Direction & Target (compass
/// heading + target GPS with map picker that infers the bearing). Save writes
/// through <see cref="CompositeMetadataWriter"/> like every other edit path,
/// so IPTC+EXIF+XMP all land in a single rewrite for JPEGs.
/// </summary>
public partial class PropertiesWindow : Window {
  private readonly FileInfo _file;
  private readonly IMetadataReader _reader;
  private readonly IMetadataWriter _writer;
  private FullMetadata _loaded = new();
  private DispatcherTimer? _gpsLookupTimer;
  private (double Lat, double Lon)? _lastLookedUp;
  private bool _suppressGpsLookup;

  public PropertiesWindow() {
    this.InitializeComponent();
    this._file = null!;
    this._reader = new MetadataReader();
    this._writer = new CompositeMetadataWriter();
  }

  public PropertiesWindow(FileInfo file) : this() {
    this._file = file;
    this.InitCombos();
    this.WireGpsAutoLookup();
    if (this.FindControl<TextBlock>("HeaderText") is { } header)
      header.Text = file.Name;
    _ = this.LoadAsync();
  }

  /// <summary>
  /// When the user types or pastes GPS coordinates, fire an altitude lookup
  /// + reverse-geocode after a short debounce so each keystroke doesn't hit
  /// the network. Blank fields are filled; user-entered values are never
  /// overwritten. Suppressed while we're programmatically loading metadata
  /// (otherwise the auto-fill would fight the just-loaded values).
  /// </summary>
  private void WireGpsAutoLookup() {
    var lat = this.FindControl<TextBox>("GpsLatBox");
    var lon = this.FindControl<TextBox>("GpsLonBox");
    if (lat is null || lon is null)
      return;

    lat.TextChanged += (_, _) => this.ScheduleGpsLookup();
    lon.TextChanged += (_, _) => this.ScheduleGpsLookup();
  }

  private void ScheduleGpsLookup() {
    if (this._suppressGpsLookup)
      return;
    this._gpsLookupTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, OnGpsLookupTimerTick);
    this._gpsLookupTimer.Stop();
    this._gpsLookupTimer.Start();
  }

  private async void OnGpsLookupTimerTick(object? sender, EventArgs e) {
    this._gpsLookupTimer?.Stop();

    var gps = this.ReadGpsFromFields();
    if (gps is null || !gps.Value.IsValid)
      return;

    // Only hit the network if the rounded coordinate pair actually changed.
    var key = (Math.Round(gps.Value.Latitude, 4), Math.Round(gps.Value.Longitude, 4));
    if (this._lastLookedUp.HasValue && this._lastLookedUp.Value == key)
      return;
    this._lastLookedUp = key;

    await this.ResolveAddressAsync(gps.Value, statusOnSuccess: false);
    await this.FillAltitudeAsync(gps.Value, statusOnSuccess: false);
  }

  private void InitCombos() {
    if (this.FindControl<ComboBox>("RatingCombo") is { } rating) {
      rating.ItemsSource = new object[] { "—", -1, 0, 1, 2, 3, 4, 5 };
      rating.SelectedIndex = 0;
    }
    if (this.FindControl<ComboBox>("LabelCombo") is { } label) {
      label.ItemsSource = new[] { "—", "Red", "Yellow", "Green", "Blue", "Purple" };
      label.SelectedIndex = 0;
    }

    // IPTC Digital Source Type — the common presets from cv.iptc.org. Users
    // who need a different one can type it into the edit mode of the ComboBox
    // (IsEditable=true below) or edit the XMP directly.
    if (this.FindControl<ComboBox>("DigitalSourceCombo") is { } dsc) {
      dsc.ItemsSource = new[] {
        "",
        "http://cv.iptc.org/newscodes/digitalsourcetype/digitalCapture",
        "http://cv.iptc.org/newscodes/digitalsourcetype/negativeFilm",
        "http://cv.iptc.org/newscodes/digitalsourcetype/positiveFilm",
        "http://cv.iptc.org/newscodes/digitalsourcetype/print",
        "http://cv.iptc.org/newscodes/digitalsourcetype/softwareImage",
        "http://cv.iptc.org/newscodes/digitalsourcetype/compositeCapture",
        "http://cv.iptc.org/newscodes/digitalsourcetype/compositeSynthetic",
        "http://cv.iptc.org/newscodes/digitalsourcetype/trainedAlgorithmicMedia",
        "http://cv.iptc.org/newscodes/digitalsourcetype/algorithmicMedia"
      };
    }

    // PLUS model-release status URIs.
    if (this.FindControl<ComboBox>("ModelReleaseStatusCombo") is { } mrc) {
      mrc.ItemsSource = new[] {
        "",
        "http://ns.useplus.org/ldf/vocab/MR-NON", // None
        "http://ns.useplus.org/ldf/vocab/MR-NAP", // Not applicable
        "http://ns.useplus.org/ldf/vocab/MR-LPR", // Limited or incomplete
        "http://ns.useplus.org/ldf/vocab/MR-UPR", // Unlimited model releases
        "http://ns.useplus.org/ldf/vocab/MR-OTR"  // Other
      };
    }
    if (this.FindControl<ComboBox>("PropertyReleaseStatusCombo") is { } prc) {
      prc.ItemsSource = new[] {
        "",
        "http://ns.useplus.org/ldf/vocab/PR-NON",
        "http://ns.useplus.org/ldf/vocab/PR-NAP",
        "http://ns.useplus.org/ldf/vocab/PR-LPR",
        "http://ns.useplus.org/ldf/vocab/PR-UPR",
        "http://ns.useplus.org/ldf/vocab/PR-OTR"
      };
    }
    if (this.FindControl<ComboBox>("DataMiningCombo") is { } dmc) {
      dmc.ItemsSource = new[] {
        "",
        "http://ns.useplus.org/ldf/vocab/DMI-ALLOWED",
        "http://ns.useplus.org/ldf/vocab/DMI-PROHIBITED",
        "http://ns.useplus.org/ldf/vocab/DMI-PROHIBITED-AIMLTRAINING",
        "http://ns.useplus.org/ldf/vocab/DMI-PROHIBITED-EXCEPTSEARCHINDEXING"
      };
    }
  }

  private async Task LoadAsync() {
    try {
      this._loaded = await this._reader.ReadAsync(this._file);
    } catch {
      this._loaded = new FullMetadata();
    }

    // Suppress GPS auto-lookup during the initial field population so we
    // don't overwrite freshly-loaded values with a network lookup.
    this._suppressGpsLookup = true;
    try {
      this.SyncFieldsFromMetadata(this._loaded);
    } finally {
      this._suppressGpsLookup = false;
    }
  }

  private void SyncFieldsFromMetadata(FullMetadata md) {
    var inv = CultureInfo.InvariantCulture;

    if (this.FindControl<TextBox>("TitleBox")        is { } t)   t.Text   = md.Title ?? string.Empty;
    if (this.FindControl<TextBox>("HeadlineBox")     is { } h)   h.Text   = md.Headline ?? string.Empty;
    if (this.FindControl<TextBox>("CaptionBox")      is { } cap) cap.Text = md.Caption ?? string.Empty;
    if (this.FindControl<TextBox>("KeywordsBox")     is { } kw)  kw.Text  = md.Keywords.Count == 0 ? string.Empty : string.Join(", ", md.Keywords);
    if (this.FindControl<TextBox>("CreatorBox")      is { } cr)  cr.Text  = md.Creator ?? string.Empty;
    if (this.FindControl<TextBox>("CreditBox")       is { } cd)  cd.Text  = md.Credit ?? string.Empty;
    if (this.FindControl<TextBox>("SourceBox")       is { } sr)  sr.Text  = md.Source ?? string.Empty;
    if (this.FindControl<TextBox>("CopyrightBox")    is { } cp)  cp.Text  = md.Copyright ?? string.Empty;
    if (this.FindControl<TextBox>("RightsUsageBox")  is { } ru)  ru.Text  = md.RightsUsage ?? string.Empty;
    if (this.FindControl<TextBox>("InstructionsBox") is { } ins) ins.Text = md.Instructions ?? string.Empty;

    if (md.DateCreated is { } dt) {
      if (this.FindControl<DatePicker>("DateCreatedPicker") is { } datePicker)
        datePicker.SelectedDate = new DateTimeOffset(dt.Date);
      if (this.FindControl<TimePicker>("TimeCreatedPicker") is { } timePicker)
        timePicker.SelectedTime = dt.TimeOfDay;
    } else {
      if (this.FindControl<DatePicker>("DateCreatedPicker") is { } datePicker)
        datePicker.SelectedDate = null;
      if (this.FindControl<TimePicker>("TimeCreatedPicker") is { } timePicker)
        timePicker.SelectedTime = null;
    }

    if (this.FindControl<TextBox>("LocationBox") is { } loc)    loc.Text = md.Location ?? string.Empty;
    if (this.FindControl<TextBox>("CityBox")     is { } city)   city.Text = md.City ?? string.Empty;
    if (this.FindControl<TextBox>("StateBox")    is { } st)     st.Text   = md.State ?? string.Empty;
    if (this.FindControl<TextBox>("CountryBox")  is { } co)     co.Text   = md.Country ?? string.Empty;
    if (this.FindControl<TextBox>("CountryCodeBox") is { } cc)  cc.Text   = md.CountryCode ?? string.Empty;

    if (md.Gps is { } gps) {
      if (this.FindControl<TextBox>("GpsLatBox") is { } lat) lat.Text = gps.Latitude.ToString("0.######", inv);
      if (this.FindControl<TextBox>("GpsLonBox") is { } lon) lon.Text = gps.Longitude.ToString("0.######", inv);
      if (gps.AltitudeMeters is { } alt && this.FindControl<TextBox>("GpsAltBox") is { } altBox)
        altBox.Text = alt.ToString("0.##", inv);
      else if (this.FindControl<TextBox>("GpsAltBox") is { } altBox2)
        altBox2.Text = string.Empty;
    } else {
      if (this.FindControl<TextBox>("GpsLatBox") is { } lat) lat.Text = string.Empty;
      if (this.FindControl<TextBox>("GpsLonBox") is { } lon) lon.Text = string.Empty;
      if (this.FindControl<TextBox>("GpsAltBox") is { } altBox) altBox.Text = string.Empty;
    }

    if (md.ImageDirection is { } dir) {
      if (this.FindControl<TextBox>("DirectionBox") is { } db)
        db.Text = dir.Degrees.ToString("0.##", inv);
      if (this.FindControl<CheckBox>("DirectionMagneticCheck") is { } mag)
        mag.IsChecked = dir.Reference == DirectionReference.Magnetic;
    } else {
      if (this.FindControl<TextBox>("DirectionBox") is { } db) db.Text = string.Empty;
      if (this.FindControl<CheckBox>("DirectionMagneticCheck") is { } mag) mag.IsChecked = false;
    }

    if (md.TargetGps is { } tgt) {
      if (this.FindControl<TextBox>("TargetLatBox") is { } tlat) tlat.Text = tgt.Latitude.ToString("0.######", inv);
      if (this.FindControl<TextBox>("TargetLonBox") is { } tlon) tlon.Text = tgt.Longitude.ToString("0.######", inv);
    } else {
      if (this.FindControl<TextBox>("TargetLatBox") is { } tlat) tlat.Text = string.Empty;
      if (this.FindControl<TextBox>("TargetLonBox") is { } tlon) tlon.Text = string.Empty;
    }

    if (this.FindControl<ComboBox>("RatingCombo") is { } rating)
      rating.SelectedItem = md.Rating is { } r ? (object)r : "—";
    if (this.FindControl<ComboBox>("LabelCombo") is { } labelCombo)
      labelCombo.SelectedItem = string.IsNullOrWhiteSpace(md.ColorLabel) ? "—" : md.ColorLabel;

    // Accessibility
    SetText("AltTextBox", md.AltTextAccessibility);
    SetText("ExtendedDescBox", md.ExtendedDescriptionAccessibility);
    SetText("DescriptionWriterBox", md.DescriptionWriter);

    // Contact
    SetText("CreatorJobTitleBox", md.CreatorJobTitle);
    SetText("ContactAddressBox", md.CreatorContactAddress);
    SetText("ContactCityBox", md.CreatorContactCity);
    SetText("ContactStateBox", md.CreatorContactState);
    SetText("ContactPostalBox", md.CreatorContactPostalCode);
    SetText("ContactCountryBox", md.CreatorContactCountry);
    SetText("ContactPhoneBox", md.CreatorContactPhone);
    SetText("ContactEmailBox", md.CreatorContactEmail);
    SetText("ContactWebsiteBox", md.CreatorContactWebsite);

    // Location extras (created)
    SetText("WorldRegionCreatedBox", md.WorldRegionCreated);
    SetText("LocationCreatedIdBox", md.LocationCreatedId);

    // Location Shown
    SetText("LocShownSublocationBox", md.LocationShownSublocation);
    SetText("LocShownCityBox", md.LocationShownCity);
    SetText("LocShownStateBox", md.LocationShownState);
    SetText("LocShownCountryBox", md.LocationShownCountry);
    SetText("LocShownCountryCodeBox", md.LocationShownCountryCode);
    SetText("LocShownWorldRegionBox", md.LocationShownWorldRegion);
    SetText("LocShownIdBox", md.LocationShownId);
    if (md.LocationShownGps is { } lsGps) {
      SetText("LocShownLatBox", lsGps.Latitude.ToString("0.######", inv));
      SetText("LocShownLonBox", lsGps.Longitude.ToString("0.######", inv));
    } else {
      SetText("LocShownLatBox", null);
      SetText("LocShownLonBox", null);
    }

    // Persons & Events
    SetText("PersonsShownBox", md.PersonsShown.Count == 0 ? null : string.Join(", ", md.PersonsShown));
    SetText("EventBox", md.Event);
    SetText("EventIdBox", md.EventId);

    // Releases + parties
    SetCombo("ModelReleaseStatusCombo", md.ModelReleaseStatus);
    SetText("ModelReleaseIdBox", md.ModelReleaseId);
    SetCombo("PropertyReleaseStatusCombo", md.PropertyReleaseStatus);
    SetText("PropertyReleaseIdBox", md.PropertyReleaseId);
    SetCombo("DataMiningCombo", md.DataMining);
    SetText("LicensorNameBox", md.LicensorName);
    SetText("LicensorIdBox", md.LicensorId);
    SetText("ImageSupplierNameBox", md.ImageSupplierName);
    SetText("ImageSupplierIdBox", md.ImageSupplierId);
    SetText("SupplierImageIdBox", md.SupplierImageId);
    SetText("CopyrightOwnerNameBox", md.CopyrightOwnerName);
    SetText("CopyrightOwnerIdBox", md.CopyrightOwnerId);

    // Artwork / Product
    SetText("ArtworkTitleBox", md.ArtworkTitle);
    SetText("ArtworkCreatorBox", md.ArtworkCreator);
    SetText("ArtworkDateCreatedBox", md.ArtworkDateCreated);
    SetText("ArtworkSourceBox", md.ArtworkSource);
    SetText("ArtworkCopyrightBox", md.ArtworkCopyright);
    SetText("ProductNameBox", md.ProductName);
    SetText("ProductGtinBox", md.ProductGtin);
    SetText("ProductDescriptionBox", md.ProductDescription);

    // Admin
    SetText("JobIdentifierBox", md.JobIdentifier);
    SetCombo("DigitalSourceCombo", md.DigitalSourceType);
    SetText("WebStatementBox", md.WebStatementOfRights);
    SetText("IptcRatingBox", md.IptcImageRating is { } ir
      ? ir.ToString("0.#", inv)
      : string.Empty);
    SetText("GenreBox", md.Genre);

    void SetText(string name, string? value) {
      if (this.FindControl<TextBox>(name) is { } tb)
        tb.Text = value ?? string.Empty;
    }

    void SetCombo(string name, string? value) {
      if (this.FindControl<ComboBox>(name) is { } cb)
        cb.SelectedItem = value ?? string.Empty;
    }
  }

  private async void OnSaveClick(object? sender, RoutedEventArgs e) {
    var edit = this.BuildEdit();
    try {
      await this._writer.ApplyAsync(this._file, edit);
      this.Close(true);
    } catch (Exception ex) {
      this.SetStatus($"Save failed: {ex.Message}");
    }
  }

  private void OnCancelClick(object? sender, RoutedEventArgs e) => this.Close(false);
  private void OnRevertClick(object? sender, RoutedEventArgs e) => this.SyncFieldsFromMetadata(this._loaded);

  private async void OnPickCameraOnMapClick(object? sender, RoutedEventArgs e)
    => await this.LaunchMapPickerAsync(includeTarget: false);

  private async void OnPickCameraAndTargetOnMapClick(object? sender, RoutedEventArgs e)
    => await this.LaunchMapPickerAsync(includeTarget: true);

  private async Task LaunchMapPickerAsync(bool includeTarget) {
    var currentCamera = this.ReadGpsFromFields();
    var currentTarget = this.ReadTargetFromFields();
    var picker = new MapPickerWindow(currentCamera, currentTarget);
    var result = await picker.ShowDialog<MapPickerWindow.Result?>(this);
    if (result is null)
      return;

    var inv = CultureInfo.InvariantCulture;
    if (this.FindControl<TextBox>("GpsLatBox") is { } lat) lat.Text = result.Camera.Latitude.ToString("0.######", inv);
    if (this.FindControl<TextBox>("GpsLonBox") is { } lon) lon.Text = result.Camera.Longitude.ToString("0.######", inv);

    if (includeTarget && result.Target is { } target) {
      if (this.FindControl<TextBox>("TargetLatBox") is { } tlat) tlat.Text = target.Latitude.ToString("0.######", inv);
      if (this.FindControl<TextBox>("TargetLonBox") is { } tlon) tlon.Text = target.Longitude.ToString("0.######", inv);
      if (result.BearingDegrees is { } bearing && this.FindControl<TextBox>("DirectionBox") is { } db)
        db.Text = bearing.ToString("0.##", inv);
    }
  }

  private async void OnTriangulateFromPhotoClick(object? sender, RoutedEventArgs e) {
    if (!this._file.Exists) {
      this.SetStatus("File missing.");
      return;
    }

    var bitmap = await PhotoManager.UI.Services.ImagePreviewLoader.LoadAsync(this._file);
    if (bitmap is null) {
      this.SetStatus("Couldn't load the photo for triangulation.");
      return;
    }

    // Pass whatever camera GPS is currently in the fields (or (0,0) if blank —
    // the 3-point resection flow solves for the camera position from the
    // landmarks so the seed is only used for the 2-point path).
    var cameraSeed = this.ReadGpsFromFields() ?? new GpsCoordinate(0, 0);

    var dialog = new TriangulateFromPhotoWindow(bitmap, cameraSeed);
    var result = await dialog.ShowDialog<TriangulateFromPhotoWindow.Result?>(this);
    if (result is null)
      return;

    var inv = CultureInfo.InvariantCulture;
    if (this.FindControl<TextBox>("DirectionBox") is { } dir)
      dir.Text = result.HeadingDegrees.ToString("0.##", inv);
    if (this.FindControl<CheckBox>("DirectionMagneticCheck") is { } mag)
      mag.IsChecked = false;
    if (this.FindControl<TextBox>("TargetLatBox") is { } tlat)
      tlat.Text = result.Target.Latitude.ToString("0.######", inv);
    if (this.FindControl<TextBox>("TargetLonBox") is { } tlon)
      tlon.Text = result.Target.Longitude.ToString("0.######", inv);

    if (result.ResectedCamera is { } solved) {
      if (this.FindControl<TextBox>("GpsLatBox") is { } lat) lat.Text = solved.Latitude.ToString("0.######", inv);
      if (this.FindControl<TextBox>("GpsLonBox") is { } lon) lon.Text = solved.Longitude.ToString("0.######", inv);
    }

    this.SetStatus(result.ResectedCamera is not null
      ? "Resected camera + heading. Click Save to commit."
      : "Triangulated heading. Click Save to commit.");
  }

  private async void OnResolveAddressClick(object? sender, RoutedEventArgs e) {
    var gps = this.ReadGpsFromFields();
    if (gps is null) {
      this.SetStatus("Enter GPS coordinates first.");
      return;
    }
    await this.ResolveAddressAsync(gps.Value, statusOnSuccess: true);
  }

  private async void OnFillElevationClick(object? sender, RoutedEventArgs e) {
    var gps = this.ReadGpsFromFields();
    if (gps is null) {
      this.SetStatus("Enter GPS coordinates first.");
      return;
    }
    await this.FillAltitudeAsync(gps.Value, statusOnSuccess: true);
  }

  private async Task ResolveAddressAsync(GpsCoordinate gps, bool statusOnSuccess) {
    if (statusOnSuccess)
      this.SetStatus("Resolving address...");
    try {
      using var geocoder = new NominatimReverseGeocoder();
      var result = await geocoder.ResolveAsync(gps);
      if (result is not { HasAny: true }) {
        if (statusOnSuccess) this.SetStatus("No address found for these coordinates.");
        return;
      }
      // Only fill blanks. User-entered values always win.
      SetIfBlank("LocationBox", result.Location);
      SetIfBlank("CityBox", result.City);
      SetIfBlank("StateBox", result.State);
      SetIfBlank("CountryBox", result.Country);
      SetIfBlank("CountryCodeBox", result.CountryCode);
      if (statusOnSuccess) this.SetStatus("Address filled from Nominatim.");
    } catch (Exception ex) {
      this.SetStatus($"Resolve failed: {ex.Message}");
    }

    void SetIfBlank(string name, string? value) {
      if (string.IsNullOrWhiteSpace(value))
        return;
      if (this.FindControl<TextBox>(name) is not { } tb)
        return;
      if (string.IsNullOrWhiteSpace(tb.Text))
        tb.Text = value;
    }
  }

  private async Task FillAltitudeAsync(GpsCoordinate gps, bool statusOnSuccess) {
    var altBox = this.FindControl<TextBox>("GpsAltBox");
    if (altBox is null)
      return;

    // Respect an altitude the user has already entered.
    if (!string.IsNullOrWhiteSpace(altBox.Text))
      return;

    if (statusOnSuccess)
      this.SetStatus("Looking up altitude...");
    try {
      using var elev = new OpenTopoElevationService();
      var m = await elev.GetAltitudeMetersAsync(gps);
      if (m is null) {
        if (statusOnSuccess) this.SetStatus("No altitude returned.");
        return;
      }
      altBox.Text = m.Value.ToString("0.#", CultureInfo.InvariantCulture);
      if (statusOnSuccess) this.SetStatus($"Altitude: {m.Value:0.#} m.");
    } catch (Exception ex) {
      this.SetStatus($"Altitude lookup failed: {ex.Message}");
    }
  }

  private GpsCoordinate? ReadGpsFromFields() {
    var inv = CultureInfo.InvariantCulture;
    if (!double.TryParse(this.FindControl<TextBox>("GpsLatBox")?.Text, NumberStyles.Float, inv, out var lat))
      return null;
    if (!double.TryParse(this.FindControl<TextBox>("GpsLonBox")?.Text, NumberStyles.Float, inv, out var lon))
      return null;
    double? alt = null;
    if (double.TryParse(this.FindControl<TextBox>("GpsAltBox")?.Text, NumberStyles.Float, inv, out var altValue))
      alt = altValue;
    return new GpsCoordinate(lat, lon, alt);
  }

  private GpsCoordinate? ReadTargetFromFields() {
    var inv = CultureInfo.InvariantCulture;
    if (!double.TryParse(this.FindControl<TextBox>("TargetLatBox")?.Text, NumberStyles.Float, inv, out var lat))
      return null;
    if (!double.TryParse(this.FindControl<TextBox>("TargetLonBox")?.Text, NumberStyles.Float, inv, out var lon))
      return null;
    return new GpsCoordinate(lat, lon);
  }

  private MetadataEdit BuildEdit() {
    var inv = CultureInfo.InvariantCulture;

    Optional<GpsCoordinate?> gps = default;
    var latText = this.FindControl<TextBox>("GpsLatBox")?.Text;
    var lonText = this.FindControl<TextBox>("GpsLonBox")?.Text;
    if (string.IsNullOrWhiteSpace(latText) && string.IsNullOrWhiteSpace(lonText)) {
      gps = Optional<GpsCoordinate?>.Set(null);
    } else if (double.TryParse(latText, NumberStyles.Float, inv, out var lat)
            && double.TryParse(lonText, NumberStyles.Float, inv, out var lon)) {
      double? alt = null;
      if (double.TryParse(this.FindControl<TextBox>("GpsAltBox")?.Text, NumberStyles.Float, inv, out var altValue))
        alt = altValue;
      gps = Optional<GpsCoordinate?>.Set(new GpsCoordinate(lat, lon, alt));
    }

    Optional<ImageDirection?> direction = default;
    var dirText = this.FindControl<TextBox>("DirectionBox")?.Text;
    if (string.IsNullOrWhiteSpace(dirText)) {
      direction = Optional<ImageDirection?>.Set(null);
    } else if (double.TryParse(dirText, NumberStyles.Float, inv, out var deg)) {
      var isMag = this.FindControl<CheckBox>("DirectionMagneticCheck")?.IsChecked == true;
      direction = Optional<ImageDirection?>.Set(
        new ImageDirection(deg, isMag ? DirectionReference.Magnetic : DirectionReference.True));
    }

    Optional<GpsCoordinate?> target = default;
    var tgtLatText = this.FindControl<TextBox>("TargetLatBox")?.Text;
    var tgtLonText = this.FindControl<TextBox>("TargetLonBox")?.Text;
    if (string.IsNullOrWhiteSpace(tgtLatText) && string.IsNullOrWhiteSpace(tgtLonText)) {
      target = Optional<GpsCoordinate?>.Set(null);
    } else if (double.TryParse(tgtLatText, NumberStyles.Float, inv, out var tLat)
            && double.TryParse(tgtLonText, NumberStyles.Float, inv, out var tLon)) {
      target = Optional<GpsCoordinate?>.Set(new GpsCoordinate(tLat, tLon));
    }

    return new MetadataEdit {
      Gps = gps,
      ImageDirection = direction,
      TargetGps = target,
      Location     = ReadText("LocationBox"),
      City         = ReadText("CityBox"),
      State        = ReadText("StateBox"),
      Country      = ReadText("CountryBox"),
      CountryCode  = ReadText("CountryCodeBox"),
      Rating       = ReadRating(),
      ColorLabel   = ReadLabel(),
      Keywords     = ReadKeywords(),
      Title        = ReadText("TitleBox"),
      Caption      = ReadText("CaptionBox"),
      Creator      = ReadText("CreatorBox"),
      Copyright    = ReadText("CopyrightBox"),
      Headline     = ReadText("HeadlineBox"),
      Credit       = ReadText("CreditBox"),
      Source       = ReadText("SourceBox"),
      Instructions = ReadText("InstructionsBox"),
      RightsUsage  = ReadText("RightsUsageBox"),
      DateCreated  = ReadDateCreated(),

      AltTextAccessibility = ReadText("AltTextBox"),
      ExtendedDescriptionAccessibility = ReadText("ExtendedDescBox"),
      DescriptionWriter = ReadText("DescriptionWriterBox"),
      JobIdentifier = ReadText("JobIdentifierBox"),
      DigitalSourceType = ReadCombo("DigitalSourceCombo"),
      WebStatementOfRights = ReadText("WebStatementBox"),
      Genre = ReadText("GenreBox"),
      IptcImageRating = ReadDouble("IptcRatingBox"),
      WorldRegionCreated = ReadText("WorldRegionCreatedBox"),
      LocationCreatedId = ReadText("LocationCreatedIdBox"),
      LocationShownSublocation = ReadText("LocShownSublocationBox"),
      LocationShownCity = ReadText("LocShownCityBox"),
      LocationShownState = ReadText("LocShownStateBox"),
      LocationShownCountry = ReadText("LocShownCountryBox"),
      LocationShownCountryCode = ReadText("LocShownCountryCodeBox"),
      LocationShownWorldRegion = ReadText("LocShownWorldRegionBox"),
      LocationShownGps = ReadLocShownGps(),
      LocationShownId = ReadText("LocShownIdBox"),

      CreatorJobTitle = ReadText("CreatorJobTitleBox"),
      CreatorContactAddress = ReadText("ContactAddressBox"),
      CreatorContactCity = ReadText("ContactCityBox"),
      CreatorContactState = ReadText("ContactStateBox"),
      CreatorContactPostalCode = ReadText("ContactPostalBox"),
      CreatorContactCountry = ReadText("ContactCountryBox"),
      CreatorContactPhone = ReadText("ContactPhoneBox"),
      CreatorContactEmail = ReadText("ContactEmailBox"),
      CreatorContactWebsite = ReadText("ContactWebsiteBox"),
      PersonsShown = ReadPersonsShown(),
      Event = ReadText("EventBox"),
      EventId = ReadText("EventIdBox"),

      ModelReleaseStatus = ReadCombo("ModelReleaseStatusCombo"),
      ModelReleaseId = ReadText("ModelReleaseIdBox"),
      PropertyReleaseStatus = ReadCombo("PropertyReleaseStatusCombo"),
      PropertyReleaseId = ReadText("PropertyReleaseIdBox"),
      DataMining = ReadCombo("DataMiningCombo"),
      LicensorName = ReadText("LicensorNameBox"),
      LicensorId = ReadText("LicensorIdBox"),
      ImageSupplierName = ReadText("ImageSupplierNameBox"),
      ImageSupplierId = ReadText("ImageSupplierIdBox"),
      SupplierImageId = ReadText("SupplierImageIdBox"),
      CopyrightOwnerName = ReadText("CopyrightOwnerNameBox"),
      CopyrightOwnerId = ReadText("CopyrightOwnerIdBox"),
      ArtworkTitle = ReadText("ArtworkTitleBox"),
      ArtworkCreator = ReadText("ArtworkCreatorBox"),
      ArtworkDateCreated = ReadText("ArtworkDateCreatedBox"),
      ArtworkSource = ReadText("ArtworkSourceBox"),
      ArtworkCopyright = ReadText("ArtworkCopyrightBox"),
      ProductName = ReadText("ProductNameBox"),
      ProductGtin = ReadText("ProductGtinBox"),
      ProductDescription = ReadText("ProductDescriptionBox")
    };

    Optional<string?> ReadText(string name) {
      var tb = this.FindControl<TextBox>(name);
      if (tb is null) return default;
      return string.IsNullOrWhiteSpace(tb.Text) ? Optional<string?>.Set(null) : Optional<string?>.Set(tb.Text.Trim());
    }

    Optional<int?> ReadRating() {
      if (this.FindControl<ComboBox>("RatingCombo")?.SelectedItem is int r)
        return Optional<int?>.Set(r);
      return Optional<int?>.Set(null);
    }

    Optional<string?> ReadLabel() {
      var sel = this.FindControl<ComboBox>("LabelCombo")?.SelectedItem as string;
      return string.IsNullOrWhiteSpace(sel) || sel == "—"
        ? Optional<string?>.Set(null)
        : Optional<string?>.Set(sel);
    }

    Optional<IReadOnlyList<string>> ReadKeywords() {
      var text = this.FindControl<TextBox>("KeywordsBox")?.Text;
      if (text is null) return default;
      var list = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(s => s.Length > 0)
        .ToArray();
      return Optional<IReadOnlyList<string>>.Set(list);
    }

    Optional<string?> ReadCombo(string name) {
      var cb = this.FindControl<ComboBox>(name);
      if (cb is null)
        return default;
      var value = cb.SelectedItem as string;
      return string.IsNullOrWhiteSpace(value)
        ? Optional<string?>.Set(null)
        : Optional<string?>.Set(value);
    }

    Optional<double?> ReadDouble(string name) {
      var tb = this.FindControl<TextBox>(name);
      if (tb is null) return default;
      if (string.IsNullOrWhiteSpace(tb.Text))
        return Optional<double?>.Set(null);
      if (double.TryParse(tb.Text, NumberStyles.Float, inv, out var v))
        return Optional<double?>.Set(v);
      return default;
    }

    Optional<IReadOnlyList<string>> ReadPersonsShown() {
      var text = this.FindControl<TextBox>("PersonsShownBox")?.Text;
      if (text is null) return default;
      var list = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(s => s.Length > 0)
        .ToArray();
      return Optional<IReadOnlyList<string>>.Set(list);
    }

    Optional<GpsCoordinate?> ReadLocShownGps() {
      var latText = this.FindControl<TextBox>("LocShownLatBox")?.Text;
      var lonText = this.FindControl<TextBox>("LocShownLonBox")?.Text;
      if (string.IsNullOrWhiteSpace(latText) && string.IsNullOrWhiteSpace(lonText))
        return Optional<GpsCoordinate?>.Set(null);
      if (double.TryParse(latText, NumberStyles.Float, inv, out var latVal)
          && double.TryParse(lonText, NumberStyles.Float, inv, out var lonVal))
        return Optional<GpsCoordinate?>.Set(new GpsCoordinate(latVal, lonVal));
      return default;
    }

    Optional<DateTime?> ReadDateCreated() {
      var datePicker = this.FindControl<DatePicker>("DateCreatedPicker");
      var timePicker = this.FindControl<TimePicker>("TimeCreatedPicker");
      if (datePicker is null)
        return default;
      if (datePicker.SelectedDate is not { } date)
        return Optional<DateTime?>.Set(null);
      var time = timePicker?.SelectedTime ?? TimeSpan.Zero;
      return Optional<DateTime?>.Set(date.Date.Add(time));
    }
  }

  private void OnClearDateClick(object? sender, RoutedEventArgs e) {
    if (this.FindControl<DatePicker>("DateCreatedPicker") is { } datePicker)
      datePicker.SelectedDate = null;
    if (this.FindControl<TimePicker>("TimeCreatedPicker") is { } timePicker)
      timePicker.SelectedTime = null;
  }

  private void SetStatus(string message) {
    if (this.FindControl<TextBlock>("StatusText") is { } s)
      s.Text = message;
  }
}
