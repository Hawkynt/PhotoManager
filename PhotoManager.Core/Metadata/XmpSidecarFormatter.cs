using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PhotoManager.Core.Detection;
using PhotoManager.Core.Regions;

namespace PhotoManager.Core.Metadata;

/// <summary>
/// Serializes and parses XMP sidecar files (RDF/XML). The minimal schema
/// this formatter handles covers GPS, rating, color label, keywords, title
/// and caption — the fields the app exposes for editing. Unknown elements
/// are preserved verbatim on round-trip so interop with other tools
/// (Lightroom, darktable, exiftool) doesn't lose data the user wrote
/// elsewhere.
/// </summary>
public static class XmpSidecarFormatter {
  private static readonly XNamespace X = "adobe:ns:meta/";
  private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
  private static readonly XNamespace Exif = "http://ns.adobe.com/exif/1.0/";
  private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
  private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
  private static readonly XNamespace XmlNs = "http://www.w3.org/XML/1998/namespace";
  // MWG Regions — the schema Lightroom and digiKam use for face regions.
  private static readonly XNamespace MwgRs = "http://www.metadataworkinggroup.com/schemas/regions/";
  private static readonly XNamespace StDim = "http://ns.adobe.com/xap/1.0/sType/Dimensions#";
  private static readonly XNamespace StArea = "http://ns.adobe.com/xap/1.0/sType/Area#";
  // PhotoManager-specific extensions to MWG-RS: category + status + source.
  // Other tools see the standard MWG-RS fields; these extras are preserved
  // round-trip via the foreign-element mechanism without breaking interop.
  private static readonly XNamespace Pm = "https://hawkynt.github.io/PhotoManager/xmp/1.0/";
  // Place-name schemas used by Lightroom/digiKam/GeoSetter.
  private static readonly XNamespace Photoshop = "http://ns.adobe.com/photoshop/1.0/";
  private static readonly XNamespace IptcCore = "http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/";
  // Rights management (xmpRights:UsageTerms, xmpRights:WebStatement).
  private static readonly XNamespace XmpRights = "http://ns.adobe.com/xap/1.0/rights/";
  // IPTC Extension schema (location shown/created, persons, events, releases,
  // licensor, artwork, product, digital source type, etc.).
  private static readonly XNamespace IptcExt = "http://iptc.org/std/Iptc4xmpExt/2008-02-29/";
  // PLUS licensing vocabulary (model + property release status, data mining).
  private static readonly XNamespace Plus = "http://ns.useplus.org/ldf/xmp/1.0/";

  /// <summary>
  /// Renders a <see cref="FullMetadata"/> snapshot to an XMP sidecar document.
  /// If <paramref name="preserveSourceDocument"/> is supplied, unknown elements
  /// from it are preserved on the output so foreign fields survive round-trips.
  /// </summary>
  public static string Serialize(FullMetadata state, XDocument? preserveSourceDocument = null) {
    var description = new XElement(Rdf + "Description",
      new XAttribute(Rdf + "about", string.Empty),
      new XAttribute(XNamespace.Xmlns + "exif", Exif.NamespaceName),
      new XAttribute(XNamespace.Xmlns + "xmp", Xmp.NamespaceName),
      new XAttribute(XNamespace.Xmlns + "dc", Dc.NamespaceName),
      new XAttribute(XNamespace.Xmlns + "mwg-rs", MwgRs.NamespaceName),
      new XAttribute(XNamespace.Xmlns + "stDim", StDim.NamespaceName),
      new XAttribute(XNamespace.Xmlns + "stArea", StArea.NamespaceName),
      new XAttribute(XNamespace.Xmlns + "pm", Pm.NamespaceName),
      new XAttribute(XNamespace.Xmlns + "photoshop", Photoshop.NamespaceName),
      new XAttribute(XNamespace.Xmlns + "Iptc4xmpCore", IptcCore.NamespaceName),
      new XAttribute(XNamespace.Xmlns + "xmpRights", XmpRights.NamespaceName),
      new XAttribute(XNamespace.Xmlns + "Iptc4xmpExt", IptcExt.NamespaceName),
      new XAttribute(XNamespace.Xmlns + "plus", Plus.NamespaceName)
    );

    if (state.Gps is { } gps) {
      description.Add(new XElement(Exif + "GPSLatitude", gps.LatitudeAsXmpString()));
      description.Add(new XElement(Exif + "GPSLongitude", gps.LongitudeAsXmpString()));
      if (gps.AltitudeMeters is { } alt) {
        description.Add(new XElement(Exif + "GPSAltitude", FormatAltitudeRational(alt)));
        description.Add(new XElement(Exif + "GPSAltitudeRef", alt >= 0 ? "0" : "1"));
      }
    }

    if (state.ImageDirection is { } direction) {
      var inv = CultureInfo.InvariantCulture;
      description.Add(new XElement(Exif + "GPSImgDirection", FormatAngleRational(direction.Degrees)));
      description.Add(new XElement(Exif + "GPSImgDirectionRef", direction.ReferenceTag));
    }

    if (state.TargetGps is { } target) {
      description.Add(new XElement(Exif + "GPSDestLatitude", target.LatitudeAsXmpString()));
      description.Add(new XElement(Exif + "GPSDestLongitude", target.LongitudeAsXmpString()));
    }

    if (!string.IsNullOrEmpty(state.Location))
      description.Add(new XElement(IptcCore + "Location", state.Location));
    if (!string.IsNullOrEmpty(state.City))
      description.Add(new XElement(Photoshop + "City", state.City));
    if (!string.IsNullOrEmpty(state.State))
      description.Add(new XElement(Photoshop + "State", state.State));
    if (!string.IsNullOrEmpty(state.Country))
      description.Add(new XElement(Photoshop + "Country", state.Country));
    if (!string.IsNullOrEmpty(state.CountryCode))
      description.Add(new XElement(IptcCore + "CountryCode", state.CountryCode));

    if (state.Rating is { } rating)
      description.Add(new XElement(Xmp + "Rating", rating.ToString(CultureInfo.InvariantCulture)));

    if (!string.IsNullOrEmpty(state.ColorLabel))
      description.Add(new XElement(Xmp + "Label", state.ColorLabel));

    // Picasa-style cull flags. xmp:Label is taken by the color label so we
    // emit dedicated xmp:Pick / xmp:Reject booleans rather than overloading
    // it. Only true is written; false collapses to "absent" in XMP.
    if (state.IsPick == true)
      description.Add(new XElement(Xmp + "Pick", "true"));
    if (state.IsReject == true)
      description.Add(new XElement(Xmp + "Reject", "true"));

    if (state.Keywords.Count > 0) {
      var bag = new XElement(Rdf + "Bag");
      foreach (var keyword in state.Keywords)
        bag.Add(new XElement(Rdf + "li", keyword));
      description.Add(new XElement(Dc + "subject", bag));
    }

    if (!string.IsNullOrEmpty(state.Title))
      description.Add(BuildLangAlt(Dc + "title", state.Title));

    if (!string.IsNullOrEmpty(state.Caption))
      description.Add(BuildLangAlt(Dc + "description", state.Caption));

    // Per-source: the XMP-specific variant wins so Properties can diverge
    // the XMP and IPTC sources intentionally. Falls back to the unified
    // Creator for callers that don't populate per-source variants.
    var xmpCreator = state.CreatorXmp ?? state.Creator;
    if (!string.IsNullOrEmpty(xmpCreator)) {
      // dc:creator is an ordered list of strings — even for a single author.
      var seq = new XElement(Rdf + "Seq", new XElement(Rdf + "li", xmpCreator));
      description.Add(new XElement(Dc + "creator", seq));
    }

    var xmpCopyright = state.CopyrightXmp ?? state.Copyright;
    if (!string.IsNullOrEmpty(xmpCopyright))
      description.Add(BuildLangAlt(Dc + "rights", xmpCopyright));

    if (!string.IsNullOrEmpty(state.Headline))
      description.Add(new XElement(Photoshop + "Headline", state.Headline));
    if (!string.IsNullOrEmpty(state.Credit))
      description.Add(new XElement(Photoshop + "Credit", state.Credit));
    if (!string.IsNullOrEmpty(state.Source))
      description.Add(new XElement(Photoshop + "Source", state.Source));
    if (!string.IsNullOrEmpty(state.Instructions))
      description.Add(new XElement(Photoshop + "Instructions", state.Instructions));

    if (!string.IsNullOrEmpty(state.RightsUsage))
      description.Add(BuildLangAlt(XmpRights + "UsageTerms", state.RightsUsage));

    if (state.DateCreated is { } created)
      description.Add(new XElement(Photoshop + "DateCreated",
        created.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)));

    // --- Wave 1: accessibility + admin + AI disclosure ---
    if (!string.IsNullOrEmpty(state.AltTextAccessibility))
      description.Add(BuildLangAlt(IptcCore + "AltTextAccessibility", state.AltTextAccessibility));
    if (!string.IsNullOrEmpty(state.ExtendedDescriptionAccessibility))
      description.Add(BuildLangAlt(IptcCore + "ExtDescrAccessibility", state.ExtendedDescriptionAccessibility));
    if (!string.IsNullOrEmpty(state.DescriptionWriter))
      description.Add(new XElement(Photoshop + "CaptionWriter", state.DescriptionWriter));
    if (!string.IsNullOrEmpty(state.JobIdentifier))
      description.Add(new XElement(Photoshop + "TransmissionReference", state.JobIdentifier));
    if (!string.IsNullOrEmpty(state.DigitalSourceType))
      description.Add(new XElement(IptcExt + "DigitalSourceType", state.DigitalSourceType));
    if (!string.IsNullOrEmpty(state.WebStatementOfRights))
      description.Add(new XElement(XmpRights + "WebStatement", state.WebStatementOfRights));
    if (!string.IsNullOrEmpty(state.Genre))
      description.Add(BuildBag(IptcExt + "Genre", new[] { state.Genre }));
    if (state.IptcImageRating is { } iptcRating)
      description.Add(new XElement(IptcExt + "ImageRating",
        iptcRating.ToString(CultureInfo.InvariantCulture)));

    // --- Wave 1b: Location Shown (structured rdf:Bag of rdf:li resources) ---
    if (HasLocationShown(state))
      description.Add(BuildLocationShown(state));

    // Location Created extras (world region + ID on top of the flat city/state/country above).
    if (!string.IsNullOrEmpty(state.WorldRegionCreated) || !string.IsNullOrEmpty(state.LocationCreatedId))
      description.Add(BuildLocationCreated(state));

    // --- Wave 2: Creator contact info (one structured block, not a bag) ---
    if (HasCreatorContact(state))
      description.Add(BuildCreatorContactInfo(state));

    if (state.PersonsShown.Count > 0)
      description.Add(BuildBag(IptcExt + "PersonInImage", state.PersonsShown));

    if (!string.IsNullOrEmpty(state.Event))
      description.Add(BuildLangAlt(IptcExt + "Event", state.Event));
    if (!string.IsNullOrEmpty(state.EventId))
      description.Add(BuildBag(IptcExt + "EventId", new[] { state.EventId }));

    // --- Wave 3: releases, licensor, supplier, copyright owner, artwork, product ---
    if (!string.IsNullOrEmpty(state.ModelReleaseStatus))
      description.Add(new XElement(Plus + "ModelReleaseStatus", state.ModelReleaseStatus));
    if (!string.IsNullOrEmpty(state.ModelReleaseId))
      description.Add(BuildBag(Plus + "ModelReleaseID", new[] { state.ModelReleaseId }));
    if (!string.IsNullOrEmpty(state.PropertyReleaseStatus))
      description.Add(new XElement(Plus + "PropertyReleaseStatus", state.PropertyReleaseStatus));
    if (!string.IsNullOrEmpty(state.PropertyReleaseId))
      description.Add(BuildBag(Plus + "PropertyReleaseID", new[] { state.PropertyReleaseId }));
    if (!string.IsNullOrEmpty(state.DataMining))
      description.Add(new XElement(Plus + "DataMining", state.DataMining));

    if (HasLicensor(state))
      description.Add(BuildLicensor(state));
    if (HasImageSupplier(state))
      description.Add(BuildImageSupplier(state));
    if (!string.IsNullOrEmpty(state.SupplierImageId))
      description.Add(new XElement(IptcExt + "SupplierImageId", state.SupplierImageId));
    if (HasCopyrightOwner(state))
      description.Add(BuildCopyrightOwner(state));

    if (HasArtwork(state))
      description.Add(BuildArtwork(state));
    if (HasProduct(state))
      description.Add(BuildProduct(state));

    if (!string.IsNullOrEmpty(state.CreatorJobTitle))
      description.Add(new XElement(Photoshop + "AuthorsPosition", state.CreatorJobTitle));

    if (state.Regions.Count > 0)
      description.Add(BuildRegionsElement(state.Regions));

    // Preserve foreign/unknown elements so third-party edits round-trip.
    if (preserveSourceDocument is { Root: { } root }) {
      var sourceDescription = root
        .Descendants(Rdf + "Description")
        .FirstOrDefault();

      if (sourceDescription != null) {
        foreach (var foreign in sourceDescription.Elements()) {
          // Skip any element we own — otherwise a field the user just
          // cleared (regions, a place name, rating) would get resurrected
          // from the original document on write. Unknown foreign elements
          // (other tools' extensions) still pass through untouched.
          if (IsKnownElement(foreign.Name))
            continue;
          description.Add(new XElement(foreign));
        }
      }
    }

    var rdf = new XElement(Rdf + "RDF",
      new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName),
      description
    );

    var xmpmeta = new XElement(X + "xmpmeta",
      new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName),
      new XAttribute(X + "xmptk", "PhotoManager"),
      rdf
    );

    var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), xmpmeta);

    using var stream = new MemoryStream();
    var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    using (var xmlWriter = XmlWriter.Create(stream, new XmlWriterSettings {
      OmitXmlDeclaration = false,
      Indent = true,
      IndentChars = "  ",
      Encoding = utf8
    })) {
      doc.Save(xmlWriter);
    }

    return utf8.GetString(stream.ToArray());
  }

  /// <summary>
  /// Parses a sidecar document and returns both the decoded state and the
  /// original <see cref="XDocument"/> so callers can pass it back into
  /// <see cref="Serialize"/> to preserve foreign fields.
  /// </summary>
  public static (FullMetadata State, XDocument Document) Parse(string xml) {
    var doc = XDocument.Parse(xml);
    var description = doc
      .Descendants(Rdf + "Description")
      .FirstOrDefault();

    if (description == null)
      return (new FullMetadata(), doc);

    var latText = description.Element(Exif + "GPSLatitude")?.Value;
    var lonText = description.Element(Exif + "GPSLongitude")?.Value;
    var altText = description.Element(Exif + "GPSAltitude")?.Value;
    var altRefText = description.Element(Exif + "GPSAltitudeRef")?.Value;

    GpsCoordinate? gps = null;
    if (latText != null && lonText != null &&
        GpsCoordinate.TryParseXmpLatitude(latText, out var lat) &&
        GpsCoordinate.TryParseXmpLongitude(lonText, out var lon)) {
      double? altitude = null;
      if (altText != null && TryParseRational(altText, out var altValue)) {
        altitude = altRefText == "1" ? -altValue : altValue;
      }
      gps = new GpsCoordinate(lat, lon, altitude);
    }

    var ratingText = description.Element(Xmp + "Rating")?.Value;
    int? rating = int.TryParse(ratingText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : null;

    var label = description.Element(Xmp + "Label")?.Value;
    if (string.IsNullOrEmpty(label))
      label = null;

    var isPick = ReadXmpBool(description.Element(Xmp + "Pick")?.Value);
    var isReject = ReadXmpBool(description.Element(Xmp + "Reject")?.Value);

    var keywords = description
      .Element(Dc + "subject")?
      .Element(Rdf + "Bag")?
      .Elements(Rdf + "li")
      .Select(e => e.Value)
      .Where(v => !string.IsNullOrEmpty(v))
      .ToArray() ?? Array.Empty<string>();

    var title = ReadLangAlt(description.Element(Dc + "title"));
    var caption = ReadLangAlt(description.Element(Dc + "description"));
    var regions = ReadRegionsElement(description.Element(MwgRs + "Regions"));

    var direction = ReadImageDirection(description);
    var target = ReadTargetGps(description);
    var location = EmptyAsNull(description.Element(IptcCore + "Location")?.Value);
    var city = EmptyAsNull(description.Element(Photoshop + "City")?.Value);
    var stateField = EmptyAsNull(description.Element(Photoshop + "State")?.Value);
    var country = EmptyAsNull(description.Element(Photoshop + "Country")?.Value);
    var countryCode = EmptyAsNull(description.Element(IptcCore + "CountryCode")?.Value);

    // dc:creator is rdf:Seq/li — pull the first entry; loss on multi-author
    // docs, but we only surface a single creator in the UI.
    var creator = EmptyAsNull(description
      .Element(Dc + "creator")?
      .Element(Rdf + "Seq")?
      .Element(Rdf + "li")?.Value);
    var copyright = ReadLangAlt(description.Element(Dc + "rights"));
    var headline = EmptyAsNull(description.Element(Photoshop + "Headline")?.Value);
    var credit = EmptyAsNull(description.Element(Photoshop + "Credit")?.Value);
    var source = EmptyAsNull(description.Element(Photoshop + "Source")?.Value);
    var instructions = EmptyAsNull(description.Element(Photoshop + "Instructions")?.Value);
    var rightsUsage = ReadLangAlt(description.Element(XmpRights + "UsageTerms"));

    DateTime? dateCreated = null;
    var dateText = description.Element(Photoshop + "DateCreated")?.Value;
    if (!string.IsNullOrWhiteSpace(dateText)
        && DateTime.TryParse(dateText, CultureInfo.InvariantCulture,
             DateTimeStyles.AssumeLocal, out var parsedDate))
      dateCreated = parsedDate;

    // Wave 1
    var altTextA11y = ReadLangAlt(description.Element(IptcCore + "AltTextAccessibility"));
    var extendedDesc = ReadLangAlt(description.Element(IptcCore + "ExtDescrAccessibility"));
    var descWriter = EmptyAsNull(description.Element(Photoshop + "CaptionWriter")?.Value);
    var jobId = EmptyAsNull(description.Element(Photoshop + "TransmissionReference")?.Value);
    var digitalSource = EmptyAsNull(description.Element(IptcExt + "DigitalSourceType")?.Value);
    var webStatement = EmptyAsNull(description.Element(XmpRights + "WebStatement")?.Value);
    var genre = ReadBagFirst(description.Element(IptcExt + "Genre"));
    double? iptcRating = null;
    var iptcRatingText = description.Element(IptcExt + "ImageRating")?.Value;
    if (!string.IsNullOrWhiteSpace(iptcRatingText)
        && double.TryParse(iptcRatingText, NumberStyles.Float, CultureInfo.InvariantCulture, out var iptcRatingValue))
      iptcRating = iptcRatingValue;

    var locShown = ReadLocationShown(description);
    var (worldRegionCreated, locationCreatedId) = ReadLocationCreatedExtras(description);

    // Wave 2
    var creatorContact = ReadCreatorContactInfo(description);
    var personsShown = ReadBag(description.Element(IptcExt + "PersonInImage"));
    var eventName = ReadLangAlt(description.Element(IptcExt + "Event"));
    var eventId = ReadBagFirst(description.Element(IptcExt + "EventId"));
    var creatorJobTitle = EmptyAsNull(description.Element(Photoshop + "AuthorsPosition")?.Value);

    // Wave 3
    var modelReleaseStatus = EmptyAsNull(description.Element(Plus + "ModelReleaseStatus")?.Value);
    var modelReleaseId = ReadBagFirst(description.Element(Plus + "ModelReleaseID"));
    var propertyReleaseStatus = EmptyAsNull(description.Element(Plus + "PropertyReleaseStatus")?.Value);
    var propertyReleaseId = ReadBagFirst(description.Element(Plus + "PropertyReleaseID"));
    var dataMining = EmptyAsNull(description.Element(Plus + "DataMining")?.Value);
    var licensor = ReadStructuredPair(description.Element(IptcExt + "Licensor"),
      IptcExt + "LicensorName", IptcExt + "LicensorID");
    var supplier = ReadStructuredPair(description.Element(IptcExt + "ImageSupplier"),
      IptcExt + "ImageSupplierName", IptcExt + "ImageSupplierID");
    var supplierImageId = EmptyAsNull(description.Element(IptcExt + "SupplierImageId")?.Value);
    var copyrightOwner = ReadStructuredPair(description.Element(IptcExt + "CopyrightOwner"),
      IptcExt + "CopyrightOwnerName", IptcExt + "CopyrightOwnerID");
    var artwork = ReadArtwork(description.Element(IptcExt + "ArtworkOrObject"));
    var product = ReadProduct(description.Element(IptcExt + "ProductInImage"));

    var state = new FullMetadata {
      Gps = gps,
      ImageDirection = direction,
      TargetGps = target,
      Location = location,
      City = city,
      State = stateField,
      Country = country,
      CountryCode = countryCode,
      Rating = rating,
      ColorLabel = label,
      IsPick = isPick,
      IsReject = isReject,
      Keywords = keywords,
      Title = title,
      Caption = caption,
      Creator = creator,
      Copyright = copyright,
      // XMP parse — these values always came from dc:creator / dc:rights so
      // record them as the XMP-source variant. The IPTC variant is filled
      // separately by the IPTC reader when an IPTC segment exists.
      CreatorXmp = creator,
      CopyrightXmp = copyright,
      Headline = headline,
      Credit = credit,
      Source = source,
      Instructions = instructions,
      RightsUsage = rightsUsage,
      DateCreated = dateCreated,

      AltTextAccessibility = altTextA11y,
      ExtendedDescriptionAccessibility = extendedDesc,
      DescriptionWriter = descWriter,
      JobIdentifier = jobId,
      DigitalSourceType = digitalSource,
      WebStatementOfRights = webStatement,
      Genre = genre,
      IptcImageRating = iptcRating,
      WorldRegionCreated = worldRegionCreated,
      LocationCreatedId = locationCreatedId,
      LocationShownSublocation = locShown.Sublocation,
      LocationShownCity = locShown.City,
      LocationShownState = locShown.State,
      LocationShownCountry = locShown.Country,
      LocationShownCountryCode = locShown.CountryCode,
      LocationShownWorldRegion = locShown.WorldRegion,
      LocationShownGps = locShown.Gps,
      LocationShownId = locShown.LocationId,

      CreatorJobTitle = creatorJobTitle,
      CreatorContactAddress = creatorContact.Address,
      CreatorContactCity = creatorContact.City,
      CreatorContactState = creatorContact.State,
      CreatorContactPostalCode = creatorContact.Postal,
      CreatorContactCountry = creatorContact.Country,
      CreatorContactPhone = creatorContact.Phone,
      CreatorContactEmail = creatorContact.Email,
      CreatorContactWebsite = creatorContact.Website,
      PersonsShown = personsShown,
      Event = eventName,
      EventId = eventId,

      ModelReleaseStatus = modelReleaseStatus,
      ModelReleaseId = modelReleaseId,
      PropertyReleaseStatus = propertyReleaseStatus,
      PropertyReleaseId = propertyReleaseId,
      DataMining = dataMining,
      LicensorName = licensor.Name,
      LicensorId = licensor.Id,
      ImageSupplierName = supplier.Name,
      ImageSupplierId = supplier.Id,
      SupplierImageId = supplierImageId,
      CopyrightOwnerName = copyrightOwner.Name,
      CopyrightOwnerId = copyrightOwner.Id,

      ArtworkTitle = artwork.Title,
      ArtworkCreator = artwork.Creator,
      ArtworkDateCreated = artwork.DateCreated,
      ArtworkSource = artwork.Source,
      ArtworkCopyright = artwork.Copyright,
      ProductName = product.Name,
      ProductGtin = product.Gtin,
      ProductDescription = product.Description,

      Regions = regions
    };

    return (state, doc);
  }

  private sealed record LocationShownData(
    string? Sublocation, string? City, string? State, string? Country,
    string? CountryCode, string? WorldRegion, GpsCoordinate? Gps, string? LocationId);

  private static LocationShownData ReadLocationShown(XElement description) {
    var bag = description.Element(IptcExt + "LocationShown")?.Element(Rdf + "Bag");
    var li = bag?.Element(Rdf + "li");
    if (li is null)
      return new LocationShownData(null, null, null, null, null, null, null, null);

    GpsCoordinate? gps = null;
    var lat = li.Element(Exif + "GPSLatitude")?.Value;
    var lon = li.Element(Exif + "GPSLongitude")?.Value;
    if (lat != null && lon != null
        && GpsCoordinate.TryParseXmpLatitude(lat, out var latVal)
        && GpsCoordinate.TryParseXmpLongitude(lon, out var lonVal)) {
      double? alt = null;
      var altText2 = li.Element(Exif + "GPSAltitude")?.Value;
      if (altText2 != null && TryParseRational(altText2, out var altValue))
        alt = altValue;
      gps = new GpsCoordinate(latVal, lonVal, alt);
    }

    return new LocationShownData(
      EmptyAsNull(li.Element(IptcExt + "Sublocation")?.Value),
      EmptyAsNull(li.Element(IptcExt + "City")?.Value),
      EmptyAsNull(li.Element(IptcExt + "ProvinceState")?.Value),
      EmptyAsNull(li.Element(IptcExt + "CountryName")?.Value),
      EmptyAsNull(li.Element(IptcExt + "CountryCode")?.Value),
      EmptyAsNull(li.Element(IptcExt + "WorldRegion")?.Value),
      gps,
      EmptyAsNull(li.Element(IptcExt + "LocationId")?.Value)
    );
  }

  private static (string? WorldRegion, string? LocationId) ReadLocationCreatedExtras(XElement description) {
    var bag = description.Element(IptcExt + "LocationCreated")?.Element(Rdf + "Bag");
    var li = bag?.Element(Rdf + "li");
    if (li is null)
      return (null, null);
    return (
      EmptyAsNull(li.Element(IptcExt + "WorldRegion")?.Value),
      EmptyAsNull(li.Element(IptcExt + "LocationId")?.Value)
    );
  }

  private sealed record CreatorContactData(
    string? Address, string? City, string? State, string? Postal, string? Country,
    string? Phone, string? Email, string? Website);

  private static CreatorContactData ReadCreatorContactInfo(XElement description) {
    var el = description.Element(IptcCore + "CreatorContactInfo");
    if (el is null)
      return new CreatorContactData(null, null, null, null, null, null, null, null);
    return new CreatorContactData(
      EmptyAsNull(el.Element(IptcCore + "CiAdrExtadr")?.Value),
      EmptyAsNull(el.Element(IptcCore + "CiAdrCity")?.Value),
      EmptyAsNull(el.Element(IptcCore + "CiAdrRegion")?.Value),
      EmptyAsNull(el.Element(IptcCore + "CiAdrPcode")?.Value),
      EmptyAsNull(el.Element(IptcCore + "CiAdrCtry")?.Value),
      EmptyAsNull(el.Element(IptcCore + "CiTelWork")?.Value),
      EmptyAsNull(el.Element(IptcCore + "CiEmailWork")?.Value),
      EmptyAsNull(el.Element(IptcCore + "CiUrlWork")?.Value)
    );
  }

  private static (string? Name, string? Id) ReadStructuredPair(XElement? container, XName nameElement, XName idElement) {
    var li = container?.Element(Rdf + "Bag")?.Element(Rdf + "li");
    if (li is null)
      return (null, null);
    return (
      EmptyAsNull(li.Element(nameElement)?.Value),
      EmptyAsNull(li.Element(idElement)?.Value)
    );
  }

  private sealed record ArtworkData(string? Title, string? Creator, string? DateCreated, string? Source, string? Copyright);

  private static ArtworkData ReadArtwork(XElement? container) {
    var li = container?.Element(Rdf + "Bag")?.Element(Rdf + "li");
    if (li is null)
      return new ArtworkData(null, null, null, null, null);
    return new ArtworkData(
      EmptyAsNull(li.Element(IptcExt + "AOTitle")?.Value),
      EmptyAsNull(li.Element(IptcExt + "AOCreator")?.Value),
      EmptyAsNull(li.Element(IptcExt + "AODateCreated")?.Value),
      EmptyAsNull(li.Element(IptcExt + "AOSource")?.Value),
      EmptyAsNull(li.Element(IptcExt + "AOCopyrightNotice")?.Value)
    );
  }

  private sealed record ProductData(string? Name, string? Gtin, string? Description);

  private static ProductData ReadProduct(XElement? container) {
    var li = container?.Element(Rdf + "Bag")?.Element(Rdf + "li");
    if (li is null)
      return new ProductData(null, null, null);
    return new ProductData(
      EmptyAsNull(li.Element(IptcExt + "ProductName")?.Value),
      EmptyAsNull(li.Element(IptcExt + "Gtin")?.Value),
      EmptyAsNull(li.Element(IptcExt + "ProductDescription")?.Value)
    );
  }

  private static ImageDirection? ReadImageDirection(XElement description) {
    var text = description.Element(Exif + "GPSImgDirection")?.Value;
    if (string.IsNullOrEmpty(text) || !TryParseRational(text, out var degrees))
      return null;

    var refText = description.Element(Exif + "GPSImgDirectionRef")?.Value;
    return new ImageDirection(degrees, ImageDirection.ParseReference(refText));
  }

  private static GpsCoordinate? ReadTargetGps(XElement description) {
    var lat = description.Element(Exif + "GPSDestLatitude")?.Value;
    var lon = description.Element(Exif + "GPSDestLongitude")?.Value;
    if (lat == null || lon == null)
      return null;

    if (!GpsCoordinate.TryParseXmpLatitude(lat, out var latDeg) ||
        !GpsCoordinate.TryParseXmpLongitude(lon, out var lonDeg))
      return null;

    return new GpsCoordinate(latDeg, lonDeg);
  }

  private static string? EmptyAsNull(string? text)
    => string.IsNullOrWhiteSpace(text) ? null : text;

  private static XElement BuildRegionsElement(IReadOnlyList<TaggedRegion> regions) {
    var list = new XElement(MwgRs + "RegionList",
      new XElement(Rdf + "Bag",
        regions.Select(BuildRegionLi).Cast<object>().ToArray()
      )
    );

    return new XElement(MwgRs + "Regions",
      new XAttribute(Rdf + "parseType", "Resource"),
      // Normalized-unit AppliedToDimensions means face Areas are expressed
      // as fractions of the image with no dependency on pixel dimensions.
      new XElement(MwgRs + "AppliedToDimensions",
        new XAttribute(StDim + "w", "1"),
        new XAttribute(StDim + "h", "1"),
        new XAttribute(StDim + "unit", "normalized")
      ),
      list
    );
  }

  private static XElement BuildRegionLi(TaggedRegion region) {
    var inv = CultureInfo.InvariantCulture;
    // MWG Area uses centre coordinates; NormalizedBoundingBox stores top-left.
    var cx = region.Box.X + region.Box.Width / 2f;
    var cy = region.Box.Y + region.Box.Height / 2f;

    var li = new XElement(Rdf + "li",
      new XAttribute(Rdf + "parseType", "Resource"),
      new XElement(MwgRs + "Type", MapCategoryToMwgType(region.Category)),
      new XElement(MwgRs + "Area",
        new XAttribute(StArea + "x", cx.ToString("0.######", inv)),
        new XAttribute(StArea + "y", cy.ToString("0.######", inv)),
        new XAttribute(StArea + "w", region.Box.Width.ToString("0.######", inv)),
        new XAttribute(StArea + "h", region.Box.Height.ToString("0.######", inv)),
        new XAttribute(StArea + "unit", "normalized")
      ),
      // PhotoManager extensions — preserved round-trip through our own namespace.
      new XElement(Pm + "category", region.Category.ToString()),
      new XElement(Pm + "status", region.Status.ToString())
    );

    if (!string.IsNullOrEmpty(region.Label))
      li.AddFirst(new XElement(MwgRs + "Name", region.Label));

    if (!string.IsNullOrEmpty(region.Source))
      li.Add(new XElement(Pm + "source", region.Source));

    // Face embeddings: persist as base64 inside our namespace so the vector
    // travels with the photo and the face-gallery scan can rebuild clusters
    // without re-running ONNX on every launch.
    if (region.Embedding is { Length: > 0 } embedding)
      li.Add(new XElement(Pm + "embedding", EncodeEmbedding(embedding)));

    return li;
  }

  private static IReadOnlyList<TaggedRegion> ReadRegionsElement(XElement? regions) {
    if (regions == null)
      return Array.Empty<TaggedRegion>();

    var list = regions.Element(MwgRs + "RegionList");
    var bag = list?.Element(Rdf + "Bag");
    if (bag == null)
      return Array.Empty<TaggedRegion>();

    var parsed = new List<TaggedRegion>();
    foreach (var li in bag.Elements(Rdf + "li")) {
      var area = li.Element(MwgRs + "Area");
      if (area == null)
        continue;

      var inv = CultureInfo.InvariantCulture;
      if (!float.TryParse(area.Attribute(StArea + "x")?.Value, NumberStyles.Float, inv, out var cx) ||
          !float.TryParse(area.Attribute(StArea + "y")?.Value, NumberStyles.Float, inv, out var cy) ||
          !float.TryParse(area.Attribute(StArea + "w")?.Value, NumberStyles.Float, inv, out var w) ||
          !float.TryParse(area.Attribute(StArea + "h")?.Value, NumberStyles.Float, inv, out var h))
        continue;

      var box = new NormalizedBoundingBox(cx - w / 2f, cy - h / 2f, w, h);

      // Prefer the PhotoManager category if present; otherwise infer from MWG Type
      // so faces/pets from Lightroom/digiKam map to Person/Animal correctly.
      var category = ReadCategory(li);
      var status = ReadStatus(li);
      var source = li.Element(Pm + "source")?.Value;
      if (string.IsNullOrEmpty(source))
        source = null;

      var name = li.Element(MwgRs + "Name")?.Value;
      if (string.IsNullOrEmpty(name))
        name = null;

      var embeddingText = li.Element(Pm + "embedding")?.Value;
      var embedding = string.IsNullOrWhiteSpace(embeddingText) ? null : TryDecodeEmbedding(embeddingText);

      parsed.Add(new TaggedRegion(box, category, name, status, source, embedding));
    }

    return parsed;
  }

  /// <summary>
  /// Encodes a 512-float face embedding as base64. Uses little-endian IEEE 754
  /// binary32 so the byte layout matches what other tools would produce from
  /// the same vector; that keeps cross-tool interop possible.
  /// </summary>
  internal static string EncodeEmbedding(float[] embedding) {
    var bytes = new byte[embedding.Length * 4];
    Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
    // Normalize to little-endian so the base64 output is byte-order-stable.
    if (!BitConverter.IsLittleEndian) {
      for (var i = 0; i < bytes.Length; i += 4) {
        (bytes[i], bytes[i + 3]) = (bytes[i + 3], bytes[i]);
        (bytes[i + 1], bytes[i + 2]) = (bytes[i + 2], bytes[i + 1]);
      }
    }
    return Convert.ToBase64String(bytes);
  }

  internal static float[]? TryDecodeEmbedding(string base64) {
    try {
      var bytes = Convert.FromBase64String(base64);
      if (bytes.Length == 0 || bytes.Length % 4 != 0)
        return null;
      if (!BitConverter.IsLittleEndian) {
        for (var i = 0; i < bytes.Length; i += 4) {
          (bytes[i], bytes[i + 3]) = (bytes[i + 3], bytes[i]);
          (bytes[i + 1], bytes[i + 2]) = (bytes[i + 2], bytes[i + 1]);
        }
      }
      var floats = new float[bytes.Length / 4];
      Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
      return floats;
    } catch (FormatException) {
      return null;
    }
  }

  private static RegionCategory ReadCategory(XElement li) {
    var explicitCategory = li.Element(Pm + "category")?.Value;
    if (!string.IsNullOrEmpty(explicitCategory)
        && Enum.TryParse<RegionCategory>(explicitCategory, ignoreCase: true, out var parsed))
      return parsed;

    var mwgType = li.Element(MwgRs + "Type")?.Value;
    return mwgType?.ToLowerInvariant() switch {
      "face" => RegionCategory.Person,
      "pet" => RegionCategory.Animal,
      "focus" or "barcode" or null or "" => RegionCategory.Item,
      _ => RegionCategory.Other
    };
  }

  private static RegionStatus ReadStatus(XElement li) {
    var statusText = li.Element(Pm + "status")?.Value;
    if (!string.IsNullOrEmpty(statusText)
        && Enum.TryParse<RegionStatus>(statusText, ignoreCase: true, out var parsed))
      return parsed;

    // Regions in an XMP file written by a third party (Lightroom, digiKam)
    // don't have our status element — treat them as already accepted.
    return RegionStatus.Accepted;
  }

  private static string MapCategoryToMwgType(RegionCategory category) => category switch {
    RegionCategory.Person => "Face",
    RegionCategory.Animal => "Pet",
    // MWG-RS doesn't have Item/Place/Other — standard readers will ignore
    // these but we round-trip them via the Pm category element.
    _ => "Focus"
  };

  private static XElement BuildLangAlt(XName name, string value)
    => new(name,
      new XElement(Rdf + "Alt",
        new XElement(Rdf + "li",
          new XAttribute(XmlNs + "lang", "x-default"),
          value
        )
      )
    );

  private static XElement BuildBag(XName name, IEnumerable<string> values) {
    var bag = new XElement(Rdf + "Bag");
    foreach (var v in values)
      if (!string.IsNullOrWhiteSpace(v))
        bag.Add(new XElement(Rdf + "li", v));
    return new XElement(name, bag);
  }

  private static IReadOnlyList<string> ReadBag(XElement? element) {
    if (element == null)
      return Array.Empty<string>();
    var bag = element.Element(Rdf + "Bag");
    if (bag == null)
      return Array.Empty<string>();
    return bag.Elements(Rdf + "li")
      .Select(e => e.Value)
      .Where(v => !string.IsNullOrWhiteSpace(v))
      .ToList();
  }

  private static string? ReadBagFirst(XElement? element) {
    var values = ReadBag(element);
    return values.Count == 0 ? null : values[0];
  }

  private static bool HasLocationShown(FullMetadata s) =>
    !string.IsNullOrEmpty(s.LocationShownSublocation)
    || !string.IsNullOrEmpty(s.LocationShownCity)
    || !string.IsNullOrEmpty(s.LocationShownState)
    || !string.IsNullOrEmpty(s.LocationShownCountry)
    || !string.IsNullOrEmpty(s.LocationShownCountryCode)
    || !string.IsNullOrEmpty(s.LocationShownWorldRegion)
    || s.LocationShownGps is not null
    || !string.IsNullOrEmpty(s.LocationShownId);

  private static XElement BuildLocationShown(FullMetadata s) {
    var li = new XElement(Rdf + "li", new XAttribute(Rdf + "parseType", "Resource"));
    AddIfText(li, IptcExt + "Sublocation",  s.LocationShownSublocation);
    AddIfText(li, IptcExt + "City",         s.LocationShownCity);
    AddIfText(li, IptcExt + "ProvinceState", s.LocationShownState);
    AddIfText(li, IptcExt + "CountryName",  s.LocationShownCountry);
    AddIfText(li, IptcExt + "CountryCode",  s.LocationShownCountryCode);
    AddIfText(li, IptcExt + "WorldRegion",  s.LocationShownWorldRegion);
    AddIfText(li, IptcExt + "LocationId",   s.LocationShownId);
    if (s.LocationShownGps is { } g) {
      var inv = CultureInfo.InvariantCulture;
      li.Add(new XElement(Exif + "GPSLatitude", g.LatitudeAsXmpString()));
      li.Add(new XElement(Exif + "GPSLongitude", g.LongitudeAsXmpString()));
      if (g.AltitudeMeters is { } alt)
        li.Add(new XElement(Exif + "GPSAltitude", FormatAltitudeRational(alt)));
    }
    return new XElement(IptcExt + "LocationShown", new XElement(Rdf + "Bag", li));
  }

  private static XElement BuildLocationCreated(FullMetadata s) {
    var li = new XElement(Rdf + "li", new XAttribute(Rdf + "parseType", "Resource"));
    AddIfText(li, IptcExt + "WorldRegion", s.WorldRegionCreated);
    AddIfText(li, IptcExt + "LocationId", s.LocationCreatedId);
    return new XElement(IptcExt + "LocationCreated", new XElement(Rdf + "Bag", li));
  }

  private static bool HasCreatorContact(FullMetadata s) =>
    !string.IsNullOrEmpty(s.CreatorContactAddress)
    || !string.IsNullOrEmpty(s.CreatorContactCity)
    || !string.IsNullOrEmpty(s.CreatorContactState)
    || !string.IsNullOrEmpty(s.CreatorContactPostalCode)
    || !string.IsNullOrEmpty(s.CreatorContactCountry)
    || !string.IsNullOrEmpty(s.CreatorContactPhone)
    || !string.IsNullOrEmpty(s.CreatorContactEmail)
    || !string.IsNullOrEmpty(s.CreatorContactWebsite);

  private static XElement BuildCreatorContactInfo(FullMetadata s) {
    var el = new XElement(IptcCore + "CreatorContactInfo",
      new XAttribute(Rdf + "parseType", "Resource"));
    AddIfText(el, IptcCore + "CiAdrExtadr", s.CreatorContactAddress);
    AddIfText(el, IptcCore + "CiAdrCity",   s.CreatorContactCity);
    AddIfText(el, IptcCore + "CiAdrRegion", s.CreatorContactState);
    AddIfText(el, IptcCore + "CiAdrPcode",  s.CreatorContactPostalCode);
    AddIfText(el, IptcCore + "CiAdrCtry",   s.CreatorContactCountry);
    AddIfText(el, IptcCore + "CiTelWork",   s.CreatorContactPhone);
    AddIfText(el, IptcCore + "CiEmailWork", s.CreatorContactEmail);
    AddIfText(el, IptcCore + "CiUrlWork",   s.CreatorContactWebsite);
    return el;
  }

  private static bool HasLicensor(FullMetadata s) =>
    !string.IsNullOrEmpty(s.LicensorName) || !string.IsNullOrEmpty(s.LicensorId);

  private static XElement BuildLicensor(FullMetadata s) {
    var li = new XElement(Rdf + "li", new XAttribute(Rdf + "parseType", "Resource"));
    AddIfText(li, IptcExt + "LicensorName", s.LicensorName);
    AddIfText(li, IptcExt + "LicensorID",   s.LicensorId);
    return new XElement(IptcExt + "Licensor", new XElement(Rdf + "Bag", li));
  }

  private static bool HasImageSupplier(FullMetadata s) =>
    !string.IsNullOrEmpty(s.ImageSupplierName) || !string.IsNullOrEmpty(s.ImageSupplierId);

  private static XElement BuildImageSupplier(FullMetadata s) {
    var li = new XElement(Rdf + "li", new XAttribute(Rdf + "parseType", "Resource"));
    AddIfText(li, IptcExt + "ImageSupplierName", s.ImageSupplierName);
    AddIfText(li, IptcExt + "ImageSupplierID",   s.ImageSupplierId);
    return new XElement(IptcExt + "ImageSupplier", new XElement(Rdf + "Bag", li));
  }

  private static bool HasCopyrightOwner(FullMetadata s) =>
    !string.IsNullOrEmpty(s.CopyrightOwnerName) || !string.IsNullOrEmpty(s.CopyrightOwnerId);

  private static XElement BuildCopyrightOwner(FullMetadata s) {
    var li = new XElement(Rdf + "li", new XAttribute(Rdf + "parseType", "Resource"));
    AddIfText(li, IptcExt + "CopyrightOwnerName", s.CopyrightOwnerName);
    AddIfText(li, IptcExt + "CopyrightOwnerID",   s.CopyrightOwnerId);
    return new XElement(IptcExt + "CopyrightOwner", new XElement(Rdf + "Bag", li));
  }

  private static bool HasArtwork(FullMetadata s) =>
    !string.IsNullOrEmpty(s.ArtworkTitle)
    || !string.IsNullOrEmpty(s.ArtworkCreator)
    || !string.IsNullOrEmpty(s.ArtworkDateCreated)
    || !string.IsNullOrEmpty(s.ArtworkSource)
    || !string.IsNullOrEmpty(s.ArtworkCopyright);

  private static XElement BuildArtwork(FullMetadata s) {
    var li = new XElement(Rdf + "li", new XAttribute(Rdf + "parseType", "Resource"));
    AddIfText(li, IptcExt + "AOTitle",             s.ArtworkTitle);
    AddIfText(li, IptcExt + "AOCreator",           s.ArtworkCreator);
    AddIfText(li, IptcExt + "AODateCreated",       s.ArtworkDateCreated);
    AddIfText(li, IptcExt + "AOSource",            s.ArtworkSource);
    AddIfText(li, IptcExt + "AOCopyrightNotice",   s.ArtworkCopyright);
    return new XElement(IptcExt + "ArtworkOrObject", new XElement(Rdf + "Bag", li));
  }

  private static bool HasProduct(FullMetadata s) =>
    !string.IsNullOrEmpty(s.ProductName)
    || !string.IsNullOrEmpty(s.ProductGtin)
    || !string.IsNullOrEmpty(s.ProductDescription);

  private static XElement BuildProduct(FullMetadata s) {
    var li = new XElement(Rdf + "li", new XAttribute(Rdf + "parseType", "Resource"));
    AddIfText(li, IptcExt + "ProductName",        s.ProductName);
    AddIfText(li, IptcExt + "Gtin",               s.ProductGtin);
    AddIfText(li, IptcExt + "ProductDescription", s.ProductDescription);
    return new XElement(IptcExt + "ProductInImage", new XElement(Rdf + "Bag", li));
  }

  private static void AddIfText(XElement parent, XName name, string? value) {
    if (!string.IsNullOrWhiteSpace(value))
      parent.Add(new XElement(name, value));
  }

  private static string? ReadLangAlt(XElement? element) {
    if (element == null)
      return null;

    var li = element
      .Element(Rdf + "Alt")?
      .Elements(Rdf + "li")
      .FirstOrDefault(e => e.Attribute(XmlNs + "lang")?.Value == "x-default")
      ?? element.Element(Rdf + "Alt")?.Element(Rdf + "li");

    var value = li?.Value;
    return string.IsNullOrEmpty(value) ? null : value;
  }

  private static bool IsKnownElement(XName name) {
    if (name.Namespace == Exif)
      return name.LocalName is "GPSLatitude" or "GPSLongitude" or "GPSAltitude" or "GPSAltitudeRef"
        or "GPSImgDirection" or "GPSImgDirectionRef" or "GPSDestLatitude" or "GPSDestLongitude";
    if (name.Namespace == Xmp)
      return name.LocalName is "Rating" or "Label" or "Pick" or "Reject";
    if (name.Namespace == Dc)
      return name.LocalName is "subject" or "title" or "description" or "creator" or "rights";
    if (name.Namespace == MwgRs)
      return name.LocalName is "Regions";
    if (name.Namespace == Photoshop)
      return name.LocalName is "City" or "State" or "Country"
        or "Headline" or "Credit" or "Source" or "Instructions" or "DateCreated"
        or "CaptionWriter" or "TransmissionReference" or "AuthorsPosition";
    if (name.Namespace == IptcCore)
      return name.LocalName is "Location" or "CountryCode"
        or "AltTextAccessibility" or "ExtDescrAccessibility"
        or "CreatorContactInfo";
    if (name.Namespace == XmpRights)
      return name.LocalName is "UsageTerms" or "WebStatement";
    if (name.Namespace == IptcExt)
      return name.LocalName is "LocationCreated" or "LocationShown"
        or "PersonInImage" or "Event" or "EventId"
        or "Licensor" or "ImageSupplier" or "SupplierImageId" or "CopyrightOwner"
        or "ArtworkOrObject" or "ProductInImage"
        or "DigitalSourceType" or "Genre" or "ImageRating";
    if (name.Namespace == Plus)
      return name.LocalName is "ModelReleaseStatus" or "ModelReleaseID"
        or "PropertyReleaseStatus" or "PropertyReleaseID" or "DataMining";
    // Pm-namespaced elements at description level (currently just
    // pm:developSettings, written by DevelopMetadataStore) are NOT owned
    // by this formatter — let them flow through the foreign-preservation
    // block so saves from the Properties dialog don't strip develop edits.
    return false;
  }

  private static string FormatAngleRational(double degrees) {
    // exif:GPSImgDirection is a rational. Use 1/1000 precision so sub-degree
    // headings (e.g. compass readings) survive round-trip.
    if (degrees == Math.Floor(degrees))
      return string.Create(CultureInfo.InvariantCulture, $"{(long)degrees}/1");
    var numerator = (long)Math.Round(degrees * 1000);
    return string.Create(CultureInfo.InvariantCulture, $"{numerator}/1000");
  }

  private static string FormatAltitudeRational(double meters) {
    var absolute = Math.Abs(meters);
    // XMP altitude is an unsigned rational; sign lives in GPSAltitudeRef.
    // Use millimeter precision (denominator 1000) unless the value is already integral.
    if (absolute == Math.Floor(absolute))
      return string.Create(CultureInfo.InvariantCulture, $"{(long)absolute}/1");

    var numerator = (long)Math.Round(absolute * 1000);
    return string.Create(CultureInfo.InvariantCulture, $"{numerator}/1000");
  }

  /// <summary>
  /// XMP booleans use "True"/"False" in older docs, "true"/"false" in newer.
  /// Returns null when the element is missing or the value isn't recognised
  /// so callers can treat absent vs explicitly-false distinctly.
  /// </summary>
  private static bool? ReadXmpBool(string? text) {
    if (string.IsNullOrWhiteSpace(text))
      return null;
    var trimmed = text.Trim();
    if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase))
      return true;
    if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
      return false;
    return null;
  }

  private static bool TryParseRational(string text, out double value) {
    value = 0;
    var slash = text.IndexOf('/');
    if (slash < 0)
      return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    if (!long.TryParse(text[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
      return false;
    if (!long.TryParse(text[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var den) || den == 0)
      return false;

    value = (double)num / den;
    return true;
  }
}
