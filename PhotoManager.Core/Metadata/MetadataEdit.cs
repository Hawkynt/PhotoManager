namespace PhotoManager.Core.Metadata;

/// <summary>
/// An immutable patch to apply to a file's sidecar metadata. Each field has
/// three states: null means "leave this field alone", a present value means
/// "write this value", and <see cref="Clear"/> values mean "delete this field
/// from the sidecar".
///
/// Callers build a patch like <c>new MetadataEdit { Gps = new(...), Rating = 5 }</c>
/// and the writer merges it on top of the existing sidecar.
/// </summary>
public sealed record MetadataEdit {
  public Optional<GpsCoordinate?> Gps { get; init; }
  public Optional<ImageDirection?> ImageDirection { get; init; }
  public Optional<GpsCoordinate?> TargetGps { get; init; }
  public Optional<string?> Location { get; init; }
  public Optional<string?> City { get; init; }
  public Optional<string?> State { get; init; }
  public Optional<string?> Country { get; init; }
  public Optional<string?> CountryCode { get; init; }
  public Optional<int?> Rating { get; init; }
  public Optional<string?> ColorLabel { get; init; }
  public Optional<bool?> IsPick { get; init; }
  public Optional<bool?> IsReject { get; init; }
  public Optional<IReadOnlyList<string>> Keywords { get; init; }
  public Optional<string?> Title { get; init; }
  public Optional<string?> Caption { get; init; }
  public Optional<string?> Creator { get; init; }
  public Optional<string?> Copyright { get; init; }

  /// <summary>
  /// Per-source override for the XMP <c>dc:creator</c> value. When set, it
  /// wins over <see cref="Creator"/> for the XMP write path only — the IPTC
  /// By-line still tracks <see cref="Creator"/> (or its own per-source
  /// override) so callers can intentionally diverge the two containers.
  /// </summary>
  public Optional<string?> CreatorXmp { get; init; }

  /// <summary>Per-source override for IPTC 2:80 By-line. See <see cref="CreatorXmp"/> for the semantic.</summary>
  public Optional<string?> CreatorIptc { get; init; }

  /// <summary>Per-source override for XMP <c>dc:rights</c>. See <see cref="CreatorXmp"/> for the semantic.</summary>
  public Optional<string?> CopyrightXmp { get; init; }

  /// <summary>Per-source override for IPTC 2:116 Copyright Notice. See <see cref="CreatorXmp"/> for the semantic.</summary>
  public Optional<string?> CopyrightIptc { get; init; }
  public Optional<string?> Headline { get; init; }
  public Optional<string?> Credit { get; init; }
  public Optional<string?> Source { get; init; }
  public Optional<string?> Instructions { get; init; }
  public Optional<string?> RightsUsage { get; init; }
  public Optional<DateTime?> DateCreated { get; init; }

  // Wave 1
  public Optional<string?> AltTextAccessibility { get; init; }
  public Optional<string?> ExtendedDescriptionAccessibility { get; init; }
  public Optional<string?> DescriptionWriter { get; init; }
  public Optional<string?> JobIdentifier { get; init; }
  public Optional<string?> DigitalSourceType { get; init; }
  public Optional<string?> WebStatementOfRights { get; init; }
  public Optional<string?> Genre { get; init; }
  public Optional<double?> IptcImageRating { get; init; }
  public Optional<string?> WorldRegionCreated { get; init; }
  public Optional<string?> LocationCreatedId { get; init; }
  public Optional<string?> LocationShownSublocation { get; init; }
  public Optional<string?> LocationShownCity { get; init; }
  public Optional<string?> LocationShownState { get; init; }
  public Optional<string?> LocationShownCountry { get; init; }
  public Optional<string?> LocationShownCountryCode { get; init; }
  public Optional<string?> LocationShownWorldRegion { get; init; }
  public Optional<GpsCoordinate?> LocationShownGps { get; init; }
  public Optional<string?> LocationShownId { get; init; }

  // Wave 2
  public Optional<string?> CreatorJobTitle { get; init; }
  public Optional<string?> CreatorContactAddress { get; init; }
  public Optional<string?> CreatorContactCity { get; init; }
  public Optional<string?> CreatorContactState { get; init; }
  public Optional<string?> CreatorContactPostalCode { get; init; }
  public Optional<string?> CreatorContactCountry { get; init; }
  public Optional<string?> CreatorContactPhone { get; init; }
  public Optional<string?> CreatorContactEmail { get; init; }
  public Optional<string?> CreatorContactWebsite { get; init; }
  public Optional<IReadOnlyList<string>> PersonsShown { get; init; }
  public Optional<string?> Event { get; init; }
  public Optional<string?> EventId { get; init; }

  // Wave 3
  public Optional<string?> ModelReleaseStatus { get; init; }
  public Optional<string?> ModelReleaseId { get; init; }
  public Optional<string?> PropertyReleaseStatus { get; init; }
  public Optional<string?> PropertyReleaseId { get; init; }
  public Optional<string?> DataMining { get; init; }
  public Optional<string?> LicensorName { get; init; }
  public Optional<string?> LicensorId { get; init; }
  public Optional<string?> ImageSupplierName { get; init; }
  public Optional<string?> ImageSupplierId { get; init; }
  public Optional<string?> SupplierImageId { get; init; }
  public Optional<string?> CopyrightOwnerName { get; init; }
  public Optional<string?> CopyrightOwnerId { get; init; }
  public Optional<string?> ArtworkTitle { get; init; }
  public Optional<string?> ArtworkCreator { get; init; }
  public Optional<string?> ArtworkDateCreated { get; init; }
  public Optional<string?> ArtworkSource { get; init; }
  public Optional<string?> ArtworkCopyright { get; init; }
  public Optional<string?> ProductName { get; init; }
  public Optional<string?> ProductGtin { get; init; }
  public Optional<string?> ProductDescription { get; init; }

  public Optional<IReadOnlyList<PhotoManager.Core.Regions.TaggedRegion>> Regions { get; init; }
}

/// <summary>
/// Three-state optional: not set (leave field alone), set to null (clear field),
/// or set to a value (write value). Used instead of plain nullable so callers
/// can express "delete this field" distinctly from "don't touch this field".
/// </summary>
public readonly record struct Optional<T> {
  public bool HasValue { get; }
  public T? Value { get; }

  private Optional(T? value, bool hasValue) {
    this.Value = value;
    this.HasValue = hasValue;
  }

  public static Optional<T> Set(T? value) => new(value, hasValue: true);

  public static implicit operator Optional<T>(T value) => Set(value);
}
