using System.Text;
using FileFormat.JpegArchive;
using MetadataExtractor.Formats.Exif;

namespace Hawkynt.PhotoManager.Core.Metadata;

public sealed class MetadataReader : IMetadataReader {
  public async Task<FullMetadata> ReadAsync(FileInfo imageFile, CancellationToken cancellationToken = default) {
    var exifState = await Task.Run(() => ReadFromExif(imageFile), cancellationToken);

    // In-file XMP (e.g. JPEG APP1 XMP segment) takes precedence over EXIF
    // because it carries our richer schema; a sidecar on top of that can
    // override either.
    var withInFile = await this.MergeInFileXmpAsync(imageFile, exifState, cancellationToken);

    var sidecar = SidecarPath.For(imageFile);
    if (!sidecar.Exists)
      return withInFile;

    try {
      var xml = await File.ReadAllTextAsync(sidecar.FullName, cancellationToken);
      var (sidecarState, _) = XmpSidecarFormatter.Parse(xml);
      return MergeSidecarOverExif(withInFile, sidecarState);
    } catch {
      // Malformed sidecar — fall back to in-file + EXIF rather than propagate the parse error.
      return withInFile;
    }
  }

  private async Task<FullMetadata> MergeInFileXmpAsync(FileInfo imageFile, FullMetadata baseState, CancellationToken cancellationToken) {
    try {
      var bytes = await File.ReadAllBytesAsync(imageFile.FullName, cancellationToken);

      // IPTC-IIM is the lowest-priority in-file source — older than XMP,
      // read by most legacy tools. Fields it carries (title/caption/keywords
      // /place names) merge *underneath* any XMP packet, so XMP wins when
      // both are present.
      var withIptc = baseState;
      var iptcBytes = JpegSegmentSurgery.TryReadIptcSegment(bytes);
      if (iptcBytes != null)
        withIptc = MergeIptcUnderneath(baseState, IptcIimEncoder.Decode(iptcBytes));

      var xmpBytes = JpegSegmentSurgery.TryReadXmpSegment(bytes);
      if (xmpBytes == null)
        return withIptc;

      var xml = Encoding.UTF8.GetString(xmpBytes);
      var (state, _) = XmpSidecarFormatter.Parse(xml);
      return MergeSidecarOverExif(withIptc, state);
    } catch {
      return baseState;
    }
  }

  private static FullMetadata MergeIptcUnderneath(FullMetadata baseState, IptcFields iptc) {
    // IPTC carries date + time in two separate datasets — stitch them into
    // one DateTime when both are present. Time format is HHMMSS±HHMM.
    DateTime? dateCreated = baseState.DateCreated;
    if (dateCreated is null && !string.IsNullOrWhiteSpace(iptc.DateCreatedYyyyMmDd)) {
      var dateText = iptc.DateCreatedYyyyMmDd;
      var timeText = iptc.TimeCreatedHhMmSsZz ?? "000000+0000";
      if (DateTime.TryParseExact(
            dateText + " " + timeText,
            new[] { "yyyyMMdd HHmmsszzz", "yyyyMMdd HHmmssK", "yyyyMMdd HHmmss" },
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal,
            out var parsed))
        dateCreated = parsed;
    }

    return baseState with {
      Title        = baseState.Title        ?? iptc.ObjectName,
      Caption      = baseState.Caption      ?? iptc.Caption,
      City         = baseState.City         ?? iptc.City,
      Location     = baseState.Location     ?? iptc.SubLocation,
      State        = baseState.State        ?? iptc.ProvinceState,
      CountryCode  = baseState.CountryCode  ?? iptc.CountryCode,
      Country      = baseState.Country      ?? iptc.CountryName,
      Keywords     = baseState.Keywords.Count > 0 ? baseState.Keywords : (iptc.Keywords ?? Array.Empty<string>()),
      Creator            = baseState.Creator            ?? iptc.ByLine,
      Copyright          = baseState.Copyright          ?? iptc.CopyrightNotice,
      // Per-source variants: track the IPTC value distinctly so Properties
      // can show / edit XMP and IPTC separately even when they disagree.
      CreatorIptc        = iptc.ByLine,
      CopyrightIptc      = iptc.CopyrightNotice,
      Headline           = baseState.Headline           ?? iptc.Headline,
      Credit             = baseState.Credit             ?? iptc.Credit,
      Source             = baseState.Source             ?? iptc.Source,
      Instructions       = baseState.Instructions       ?? iptc.Instructions,
      DescriptionWriter  = baseState.DescriptionWriter  ?? iptc.DescriptionWriter,
      JobIdentifier      = baseState.JobIdentifier      ?? iptc.TransmissionReference,
      CreatorJobTitle    = baseState.CreatorJobTitle    ?? iptc.CreatorJobTitle,
      DateCreated        = dateCreated
    };
  }

  private static FullMetadata ReadFromExif(FileInfo imageFile) {
    if (!imageFile.Exists)
      return new FullMetadata();

    IReadOnlyList<MetadataExtractor.Directory> directories;
    try {
      directories = ImageMetadataReader.ReadMetadata(imageFile.FullName);
    } catch {
      return new FullMetadata();
    }

    var gps = ReadGps(directories);
    return new FullMetadata { Gps = gps };
  }

  private static GpsCoordinate? ReadGps(IReadOnlyList<MetadataExtractor.Directory> directories) {
    var gpsDir = directories.OfType<GpsDirectory>().FirstOrDefault();
    if (gpsDir == null)
      return null;

    var location = gpsDir.GetGeoLocation();
    if (location == null || (location.Latitude == 0 && location.Longitude == 0))
      return null;

    double? altitude = null;
    if (gpsDir.TryGetRational(GpsDirectory.TagAltitude, out var altRational)) {
      altitude = altRational.ToDouble();
      if (gpsDir.TryGetInt32(GpsDirectory.TagAltitudeRef, out var altRef) && altRef == 1)
        altitude = -altitude;
    }

    return new GpsCoordinate(location.Latitude, location.Longitude, altitude);
  }

  private static FullMetadata MergeSidecarOverExif(FullMetadata exif, FullMetadata sidecar) => new() {
    Gps            = sidecar.Gps            ?? exif.Gps,
    ImageDirection = sidecar.ImageDirection ?? exif.ImageDirection,
    TargetGps      = sidecar.TargetGps      ?? exif.TargetGps,
    Location       = sidecar.Location       ?? exif.Location,
    City           = sidecar.City           ?? exif.City,
    State          = sidecar.State          ?? exif.State,
    Country        = sidecar.Country        ?? exif.Country,
    CountryCode    = sidecar.CountryCode    ?? exif.CountryCode,
    Rating         = sidecar.Rating         ?? exif.Rating,
    ColorLabel     = sidecar.ColorLabel     ?? exif.ColorLabel,
    IsPick         = sidecar.IsPick         ?? exif.IsPick,
    IsReject       = sidecar.IsReject       ?? exif.IsReject,
    Keywords       = sidecar.Keywords.Count > 0 ? sidecar.Keywords : exif.Keywords,
    Title          = sidecar.Title          ?? exif.Title,
    Caption        = sidecar.Caption        ?? exif.Caption,
    Creator        = sidecar.Creator        ?? exif.Creator,
    Copyright      = sidecar.Copyright      ?? exif.Copyright,
    // Per-source: the sidecar/XMP values always come from dc:creator and
    // dc:rights; the IPTC variants stayed on the underneath state.
    CreatorXmp     = sidecar.Creator        ?? exif.CreatorXmp,
    CreatorIptc    = exif.CreatorIptc,
    CopyrightXmp   = sidecar.Copyright      ?? exif.CopyrightXmp,
    CopyrightIptc  = exif.CopyrightIptc,
    Headline       = sidecar.Headline       ?? exif.Headline,
    Credit         = sidecar.Credit         ?? exif.Credit,
    Source         = sidecar.Source         ?? exif.Source,
    Instructions   = sidecar.Instructions   ?? exif.Instructions,
    RightsUsage    = sidecar.RightsUsage    ?? exif.RightsUsage,
    DateCreated    = sidecar.DateCreated    ?? exif.DateCreated,

    AltTextAccessibility              = sidecar.AltTextAccessibility              ?? exif.AltTextAccessibility,
    ExtendedDescriptionAccessibility  = sidecar.ExtendedDescriptionAccessibility  ?? exif.ExtendedDescriptionAccessibility,
    DescriptionWriter                 = sidecar.DescriptionWriter                 ?? exif.DescriptionWriter,
    JobIdentifier                     = sidecar.JobIdentifier                     ?? exif.JobIdentifier,
    DigitalSourceType                 = sidecar.DigitalSourceType                 ?? exif.DigitalSourceType,
    WebStatementOfRights              = sidecar.WebStatementOfRights              ?? exif.WebStatementOfRights,
    Genre                             = sidecar.Genre                             ?? exif.Genre,
    IptcImageRating                   = sidecar.IptcImageRating                   ?? exif.IptcImageRating,
    WorldRegionCreated                = sidecar.WorldRegionCreated                ?? exif.WorldRegionCreated,
    LocationCreatedId                 = sidecar.LocationCreatedId                 ?? exif.LocationCreatedId,
    LocationShownSublocation          = sidecar.LocationShownSublocation          ?? exif.LocationShownSublocation,
    LocationShownCity                 = sidecar.LocationShownCity                 ?? exif.LocationShownCity,
    LocationShownState                = sidecar.LocationShownState                ?? exif.LocationShownState,
    LocationShownCountry              = sidecar.LocationShownCountry              ?? exif.LocationShownCountry,
    LocationShownCountryCode          = sidecar.LocationShownCountryCode          ?? exif.LocationShownCountryCode,
    LocationShownWorldRegion          = sidecar.LocationShownWorldRegion          ?? exif.LocationShownWorldRegion,
    LocationShownGps                  = sidecar.LocationShownGps                  ?? exif.LocationShownGps,
    LocationShownId                   = sidecar.LocationShownId                   ?? exif.LocationShownId,

    CreatorJobTitle                   = sidecar.CreatorJobTitle                   ?? exif.CreatorJobTitle,
    CreatorContactAddress             = sidecar.CreatorContactAddress             ?? exif.CreatorContactAddress,
    CreatorContactCity                = sidecar.CreatorContactCity                ?? exif.CreatorContactCity,
    CreatorContactState               = sidecar.CreatorContactState               ?? exif.CreatorContactState,
    CreatorContactPostalCode          = sidecar.CreatorContactPostalCode          ?? exif.CreatorContactPostalCode,
    CreatorContactCountry             = sidecar.CreatorContactCountry             ?? exif.CreatorContactCountry,
    CreatorContactPhone               = sidecar.CreatorContactPhone               ?? exif.CreatorContactPhone,
    CreatorContactEmail               = sidecar.CreatorContactEmail               ?? exif.CreatorContactEmail,
    CreatorContactWebsite             = sidecar.CreatorContactWebsite             ?? exif.CreatorContactWebsite,
    PersonsShown                      = sidecar.PersonsShown.Count > 0 ? sidecar.PersonsShown : exif.PersonsShown,
    Event                             = sidecar.Event                             ?? exif.Event,
    EventId                           = sidecar.EventId                           ?? exif.EventId,

    ModelReleaseStatus                = sidecar.ModelReleaseStatus                ?? exif.ModelReleaseStatus,
    ModelReleaseId                    = sidecar.ModelReleaseId                    ?? exif.ModelReleaseId,
    PropertyReleaseStatus             = sidecar.PropertyReleaseStatus             ?? exif.PropertyReleaseStatus,
    PropertyReleaseId                 = sidecar.PropertyReleaseId                 ?? exif.PropertyReleaseId,
    DataMining                        = sidecar.DataMining                        ?? exif.DataMining,
    LicensorName                      = sidecar.LicensorName                      ?? exif.LicensorName,
    LicensorId                        = sidecar.LicensorId                        ?? exif.LicensorId,
    ImageSupplierName                 = sidecar.ImageSupplierName                 ?? exif.ImageSupplierName,
    ImageSupplierId                   = sidecar.ImageSupplierId                   ?? exif.ImageSupplierId,
    SupplierImageId                   = sidecar.SupplierImageId                   ?? exif.SupplierImageId,
    CopyrightOwnerName                = sidecar.CopyrightOwnerName                ?? exif.CopyrightOwnerName,
    CopyrightOwnerId                  = sidecar.CopyrightOwnerId                  ?? exif.CopyrightOwnerId,
    ArtworkTitle                      = sidecar.ArtworkTitle                      ?? exif.ArtworkTitle,
    ArtworkCreator                    = sidecar.ArtworkCreator                    ?? exif.ArtworkCreator,
    ArtworkDateCreated                = sidecar.ArtworkDateCreated                ?? exif.ArtworkDateCreated,
    ArtworkSource                     = sidecar.ArtworkSource                     ?? exif.ArtworkSource,
    ArtworkCopyright                  = sidecar.ArtworkCopyright                  ?? exif.ArtworkCopyright,
    ProductName                       = sidecar.ProductName                       ?? exif.ProductName,
    ProductGtin                       = sidecar.ProductGtin                       ?? exif.ProductGtin,
    ProductDescription                = sidecar.ProductDescription                ?? exif.ProductDescription,

    Regions        = sidecar.Regions.Count > 0 ? sidecar.Regions : exif.Regions
  };
}
