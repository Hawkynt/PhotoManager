using PhotoManager.Core.Regions;

namespace PhotoManager.Core.Metadata;

/// <summary>
/// A fully-resolved snapshot of a file's metadata: EXIF values plus any
/// XMP sidecar overrides merged on top. Sidecar values win for fields the
/// user has edited; EXIF fills in the rest. All fields are nullable so
/// callers can tell "not present" apart from "empty".
/// </summary>
public sealed record FullMetadata {
  public GpsCoordinate? Gps { get; init; }

  /// <summary>Direction the camera was pointing (compass heading 0..360°).</summary>
  public ImageDirection? ImageDirection { get; init; }

  /// <summary>GPS location of the photographed subject — distinct from <see cref="Gps"/>
  /// which is the camera's position. Maps to exif:GPSDestLatitude/Longitude.</summary>
  public GpsCoordinate? TargetGps { get; init; }

  /// <summary>Free-form sublocation (neighborhood, venue, road) — Iptc4xmpCore:Location.</summary>
  public string? Location { get; init; }

  public string? City { get; init; }
  public string? State { get; init; }
  public string? Country { get; init; }

  /// <summary>ISO 3166-1 alpha-2, e.g. "DE", "US".</summary>
  public string? CountryCode { get; init; }

  /// <summary>0–5 (5 best), -1 means rejected per XMP convention.</summary>
  public int? Rating { get; init; }

  /// <summary>Adobe-style color label: "Red", "Yellow", "Green", "Blue", "Purple", or user-defined.</summary>
  public string? ColorLabel { get; init; }

  public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

  public string? Title { get; init; }
  public string? Caption { get; init; }

  /// <summary>Person or organisation who made the image — XMP <c>dc:creator</c>, IPTC 2:80 By-line, EXIF Artist.</summary>
  public string? Creator { get; init; }

  /// <summary>Copyright notice — XMP <c>dc:rights</c>, IPTC 2:116, EXIF Copyright.</summary>
  public string? Copyright { get; init; }

  /// <summary>Short headline summarising the image — XMP <c>photoshop:Headline</c>, IPTC 2:105.</summary>
  public string? Headline { get; init; }

  /// <summary>Who to credit when publishing — XMP <c>photoshop:Credit</c>, IPTC 2:110.</summary>
  public string? Credit { get; init; }

  /// <summary>Original source of the image — XMP <c>photoshop:Source</c>, IPTC 2:115.</summary>
  public string? Source { get; init; }

  /// <summary>Special instructions for the image — XMP <c>photoshop:Instructions</c>, IPTC 2:40.</summary>
  public string? Instructions { get; init; }

  /// <summary>Rights usage terms — XMP <c>xmpRights:UsageTerms</c>.</summary>
  public string? RightsUsage { get; init; }

  /// <summary>Original capture date/time (separate from file mtime) — XMP <c>photoshop:DateCreated</c>, IPTC 2:55/2:60.</summary>
  public DateTime? DateCreated { get; init; }

  // ---- IPTC Wave 1: accessibility, AI disclosure, admin, Location Shown ----

  /// <summary>Alternative text for assistive technology — <c>Iptc4xmpCore:AltTextAccessibility</c>.</summary>
  public string? AltTextAccessibility { get; init; }

  /// <summary>Longer description for accessibility — <c>Iptc4xmpCore:ExtDescrAccessibility</c>.</summary>
  public string? ExtendedDescriptionAccessibility { get; init; }

  /// <summary>Who wrote the description/caption — <c>Iptc4xmpCore:DescriptionWriter</c>, IPTC 2:122.</summary>
  public string? DescriptionWriter { get; init; }

  /// <summary>Job / transmission reference — <c>Iptc4xmpCore:TransmissionReference</c>, IPTC 2:103.</summary>
  public string? JobIdentifier { get; init; }

  /// <summary>Digital source type URI (AI-generated disclosure) — <c>Iptcext:DigitalSourceType</c>.</summary>
  public string? DigitalSourceType { get; init; }

  /// <summary>URL pointing to rights statement — <c>xmpRights:WebStatement</c>.</summary>
  public string? WebStatementOfRights { get; init; }

  /// <summary>Genre classification URI — <c>Iptcext:Genre</c>.</summary>
  public string? Genre { get; init; }

  /// <summary>IPTC's own image rating (separate from the XMP <see cref="Rating"/>) — <c>Iptcext:ImageRating</c>.</summary>
  public double? IptcImageRating { get; init; }

  /// <summary>World region of the Location Created — <c>Iptcext:LocationCreated/Iptcext:WorldRegion</c>.</summary>
  public string? WorldRegionCreated { get; init; }

  /// <summary>Identifier (e.g. Wikidata URL) for the Location Created — <c>Iptcext:LocationCreated/Iptcext:LocationId</c>.</summary>
  public string? LocationCreatedId { get; init; }

  /// <summary>Sublocation visible/depicted in the image — <c>Iptcext:LocationShown/Iptcext:Sublocation</c>.</summary>
  public string? LocationShownSublocation { get; init; }
  public string? LocationShownCity { get; init; }
  public string? LocationShownState { get; init; }
  public string? LocationShownCountry { get; init; }
  public string? LocationShownCountryCode { get; init; }
  public string? LocationShownWorldRegion { get; init; }
  public GpsCoordinate? LocationShownGps { get; init; }
  public string? LocationShownId { get; init; }

  // ---- IPTC Wave 2: creator contact, persons, events ----

  public string? CreatorJobTitle { get; init; }
  public string? CreatorContactAddress { get; init; }
  public string? CreatorContactCity { get; init; }
  public string? CreatorContactState { get; init; }
  public string? CreatorContactPostalCode { get; init; }
  public string? CreatorContactCountry { get; init; }
  public string? CreatorContactPhone { get; init; }
  public string? CreatorContactEmail { get; init; }
  public string? CreatorContactWebsite { get; init; }

  /// <summary>Names of recognizable people shown in the image — <c>Iptcext:PersonInImage</c>.</summary>
  public IReadOnlyList<string> PersonsShown { get; init; } = Array.Empty<string>();

  /// <summary>Event associated with the image — <c>Iptcext:Event</c>.</summary>
  public string? Event { get; init; }
  /// <summary>Unique identifier for the event — <c>Iptcext:EventId</c>.</summary>
  public string? EventId { get; init; }

  // ---- IPTC Wave 3: releases, licensor, supplier, copyright owner, artwork, product ----

  /// <summary>Model release status URI (PLUS vocabulary) — <c>Iptcext:ModelReleaseStatus</c>.</summary>
  public string? ModelReleaseStatus { get; init; }
  public string? ModelReleaseId { get; init; }

  public string? PropertyReleaseStatus { get; init; }
  public string? PropertyReleaseId { get; init; }

  /// <summary>Data-mining permission URI (PLUS vocabulary) — <c>Iptcext:DataMining</c>.</summary>
  public string? DataMining { get; init; }

  /// <summary>Licensor (first entry, if multiple) — <c>Iptcext:Licensor</c>.</summary>
  public string? LicensorName { get; init; }
  public string? LicensorId { get; init; }

  public string? ImageSupplierName { get; init; }
  public string? ImageSupplierId { get; init; }
  public string? SupplierImageId { get; init; }

  public string? CopyrightOwnerName { get; init; }
  public string? CopyrightOwnerId { get; init; }

  // Single-artwork, single-product simplification — the IPTC schema allows lists,
  // but the overwhelming majority of DAM edits target one artwork / one product
  // and this keeps the UI flat and tractable. Users with multi-artwork needs can
  // still edit the XMP directly.
  public string? ArtworkTitle { get; init; }
  public string? ArtworkCreator { get; init; }
  public string? ArtworkDateCreated { get; init; }
  public string? ArtworkSource { get; init; }
  public string? ArtworkCopyright { get; init; }

  public string? ProductName { get; init; }
  public string? ProductGtin { get; init; }
  public string? ProductDescription { get; init; }

  /// <summary>
  /// All tagged regions — people, animals, items, places — in any status.
  /// Proposed regions come from detectors awaiting user review; Accepted
  /// regions are confirmed and contribute labels to keywords.
  /// </summary>
  public IReadOnlyList<TaggedRegion> Regions { get; init; } = Array.Empty<TaggedRegion>();

  /// <summary>
  /// Face regions — convenience view over <see cref="Regions"/> filtered
  /// to accepted Person entries. Interoperable with Lightroom/digiKam
  /// MWG-RS face tagging.
  /// </summary>
  public IReadOnlyList<FaceRegion> Faces => this.Regions
    .Where(r => r.Category == RegionCategory.Person && r.Status == RegionStatus.Accepted)
    .Select(r => new FaceRegion(r.Box, r.Label))
    .ToArray();
}
