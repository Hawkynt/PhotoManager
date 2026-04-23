using System.Xml.Linq;

namespace PhotoManager.Core.Metadata;

public sealed class XmpSidecarWriter : IMetadataWriter {
  public async Task<FileInfo> ApplyAsync(FileInfo imageFile, MetadataEdit edit, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(imageFile);
    ArgumentNullException.ThrowIfNull(edit);

    var sidecar = SidecarPath.For(imageFile);

    var existing = new FullMetadata();
    XDocument? sourceDocument = null;

    if (sidecar.Exists) {
      try {
        var existingXml = await File.ReadAllTextAsync(sidecar.FullName, cancellationToken);
        (existing, sourceDocument) = XmpSidecarFormatter.Parse(existingXml);
      } catch {
        // Corrupt sidecar — start fresh rather than propagate the parse failure.
        existing = new FullMetadata();
        sourceDocument = null;
      }
    }

    var merged = ApplyPatch(existing, edit);
    var xml = XmpSidecarFormatter.Serialize(merged, sourceDocument);

    sidecar.Directory?.Create();
    await WriteAtomicAsync(sidecar, xml, cancellationToken);

    sidecar.Refresh();
    return sidecar;
  }

  private static FullMetadata ApplyPatch(FullMetadata current, MetadataEdit edit) => new() {
    Gps            = edit.Gps.HasValue            ? edit.Gps.Value            : current.Gps,
    ImageDirection = edit.ImageDirection.HasValue ? edit.ImageDirection.Value : current.ImageDirection,
    TargetGps      = edit.TargetGps.HasValue      ? edit.TargetGps.Value      : current.TargetGps,
    Location       = edit.Location.HasValue       ? edit.Location.Value       : current.Location,
    City           = edit.City.HasValue           ? edit.City.Value           : current.City,
    State          = edit.State.HasValue          ? edit.State.Value          : current.State,
    Country        = edit.Country.HasValue        ? edit.Country.Value        : current.Country,
    CountryCode    = edit.CountryCode.HasValue    ? edit.CountryCode.Value    : current.CountryCode,
    Rating         = edit.Rating.HasValue         ? edit.Rating.Value         : current.Rating,
    ColorLabel     = edit.ColorLabel.HasValue     ? edit.ColorLabel.Value     : current.ColorLabel,
    Keywords       = edit.Keywords.HasValue       ? edit.Keywords.Value ?? Array.Empty<string>() : current.Keywords,
    Title          = edit.Title.HasValue          ? edit.Title.Value          : current.Title,
    Caption        = edit.Caption.HasValue        ? edit.Caption.Value        : current.Caption,
    Creator        = edit.Creator.HasValue        ? edit.Creator.Value        : current.Creator,
    Copyright      = edit.Copyright.HasValue      ? edit.Copyright.Value      : current.Copyright,
    Headline       = edit.Headline.HasValue       ? edit.Headline.Value       : current.Headline,
    Credit         = edit.Credit.HasValue         ? edit.Credit.Value         : current.Credit,
    Source         = edit.Source.HasValue         ? edit.Source.Value         : current.Source,
    Instructions   = edit.Instructions.HasValue   ? edit.Instructions.Value   : current.Instructions,
    RightsUsage    = edit.RightsUsage.HasValue    ? edit.RightsUsage.Value    : current.RightsUsage,
    DateCreated    = edit.DateCreated.HasValue    ? edit.DateCreated.Value    : current.DateCreated,

    AltTextAccessibility              = edit.AltTextAccessibility.HasValue              ? edit.AltTextAccessibility.Value              : current.AltTextAccessibility,
    ExtendedDescriptionAccessibility  = edit.ExtendedDescriptionAccessibility.HasValue  ? edit.ExtendedDescriptionAccessibility.Value  : current.ExtendedDescriptionAccessibility,
    DescriptionWriter                 = edit.DescriptionWriter.HasValue                 ? edit.DescriptionWriter.Value                 : current.DescriptionWriter,
    JobIdentifier                     = edit.JobIdentifier.HasValue                     ? edit.JobIdentifier.Value                     : current.JobIdentifier,
    DigitalSourceType                 = edit.DigitalSourceType.HasValue                 ? edit.DigitalSourceType.Value                 : current.DigitalSourceType,
    WebStatementOfRights              = edit.WebStatementOfRights.HasValue              ? edit.WebStatementOfRights.Value              : current.WebStatementOfRights,
    Genre                             = edit.Genre.HasValue                             ? edit.Genre.Value                             : current.Genre,
    IptcImageRating                   = edit.IptcImageRating.HasValue                   ? edit.IptcImageRating.Value                   : current.IptcImageRating,
    WorldRegionCreated                = edit.WorldRegionCreated.HasValue                ? edit.WorldRegionCreated.Value                : current.WorldRegionCreated,
    LocationCreatedId                 = edit.LocationCreatedId.HasValue                 ? edit.LocationCreatedId.Value                 : current.LocationCreatedId,
    LocationShownSublocation          = edit.LocationShownSublocation.HasValue          ? edit.LocationShownSublocation.Value          : current.LocationShownSublocation,
    LocationShownCity                 = edit.LocationShownCity.HasValue                 ? edit.LocationShownCity.Value                 : current.LocationShownCity,
    LocationShownState                = edit.LocationShownState.HasValue                ? edit.LocationShownState.Value                : current.LocationShownState,
    LocationShownCountry              = edit.LocationShownCountry.HasValue              ? edit.LocationShownCountry.Value              : current.LocationShownCountry,
    LocationShownCountryCode          = edit.LocationShownCountryCode.HasValue          ? edit.LocationShownCountryCode.Value          : current.LocationShownCountryCode,
    LocationShownWorldRegion          = edit.LocationShownWorldRegion.HasValue          ? edit.LocationShownWorldRegion.Value          : current.LocationShownWorldRegion,
    LocationShownGps                  = edit.LocationShownGps.HasValue                  ? edit.LocationShownGps.Value                  : current.LocationShownGps,
    LocationShownId                   = edit.LocationShownId.HasValue                   ? edit.LocationShownId.Value                   : current.LocationShownId,

    CreatorJobTitle                   = edit.CreatorJobTitle.HasValue                   ? edit.CreatorJobTitle.Value                   : current.CreatorJobTitle,
    CreatorContactAddress             = edit.CreatorContactAddress.HasValue             ? edit.CreatorContactAddress.Value             : current.CreatorContactAddress,
    CreatorContactCity                = edit.CreatorContactCity.HasValue                ? edit.CreatorContactCity.Value                : current.CreatorContactCity,
    CreatorContactState               = edit.CreatorContactState.HasValue               ? edit.CreatorContactState.Value               : current.CreatorContactState,
    CreatorContactPostalCode          = edit.CreatorContactPostalCode.HasValue          ? edit.CreatorContactPostalCode.Value          : current.CreatorContactPostalCode,
    CreatorContactCountry             = edit.CreatorContactCountry.HasValue             ? edit.CreatorContactCountry.Value             : current.CreatorContactCountry,
    CreatorContactPhone               = edit.CreatorContactPhone.HasValue               ? edit.CreatorContactPhone.Value               : current.CreatorContactPhone,
    CreatorContactEmail               = edit.CreatorContactEmail.HasValue               ? edit.CreatorContactEmail.Value               : current.CreatorContactEmail,
    CreatorContactWebsite             = edit.CreatorContactWebsite.HasValue             ? edit.CreatorContactWebsite.Value             : current.CreatorContactWebsite,
    PersonsShown                      = edit.PersonsShown.HasValue                      ? edit.PersonsShown.Value ?? Array.Empty<string>() : current.PersonsShown,
    Event                             = edit.Event.HasValue                             ? edit.Event.Value                             : current.Event,
    EventId                           = edit.EventId.HasValue                           ? edit.EventId.Value                           : current.EventId,

    ModelReleaseStatus                = edit.ModelReleaseStatus.HasValue                ? edit.ModelReleaseStatus.Value                : current.ModelReleaseStatus,
    ModelReleaseId                    = edit.ModelReleaseId.HasValue                    ? edit.ModelReleaseId.Value                    : current.ModelReleaseId,
    PropertyReleaseStatus             = edit.PropertyReleaseStatus.HasValue             ? edit.PropertyReleaseStatus.Value             : current.PropertyReleaseStatus,
    PropertyReleaseId                 = edit.PropertyReleaseId.HasValue                 ? edit.PropertyReleaseId.Value                 : current.PropertyReleaseId,
    DataMining                        = edit.DataMining.HasValue                        ? edit.DataMining.Value                        : current.DataMining,
    LicensorName                      = edit.LicensorName.HasValue                      ? edit.LicensorName.Value                      : current.LicensorName,
    LicensorId                        = edit.LicensorId.HasValue                        ? edit.LicensorId.Value                        : current.LicensorId,
    ImageSupplierName                 = edit.ImageSupplierName.HasValue                 ? edit.ImageSupplierName.Value                 : current.ImageSupplierName,
    ImageSupplierId                   = edit.ImageSupplierId.HasValue                   ? edit.ImageSupplierId.Value                   : current.ImageSupplierId,
    SupplierImageId                   = edit.SupplierImageId.HasValue                   ? edit.SupplierImageId.Value                   : current.SupplierImageId,
    CopyrightOwnerName                = edit.CopyrightOwnerName.HasValue                ? edit.CopyrightOwnerName.Value                : current.CopyrightOwnerName,
    CopyrightOwnerId                  = edit.CopyrightOwnerId.HasValue                  ? edit.CopyrightOwnerId.Value                  : current.CopyrightOwnerId,
    ArtworkTitle                      = edit.ArtworkTitle.HasValue                      ? edit.ArtworkTitle.Value                      : current.ArtworkTitle,
    ArtworkCreator                    = edit.ArtworkCreator.HasValue                    ? edit.ArtworkCreator.Value                    : current.ArtworkCreator,
    ArtworkDateCreated                = edit.ArtworkDateCreated.HasValue                ? edit.ArtworkDateCreated.Value                : current.ArtworkDateCreated,
    ArtworkSource                     = edit.ArtworkSource.HasValue                     ? edit.ArtworkSource.Value                     : current.ArtworkSource,
    ArtworkCopyright                  = edit.ArtworkCopyright.HasValue                  ? edit.ArtworkCopyright.Value                  : current.ArtworkCopyright,
    ProductName                       = edit.ProductName.HasValue                       ? edit.ProductName.Value                       : current.ProductName,
    ProductGtin                       = edit.ProductGtin.HasValue                       ? edit.ProductGtin.Value                       : current.ProductGtin,
    ProductDescription                = edit.ProductDescription.HasValue                ? edit.ProductDescription.Value                : current.ProductDescription,

    Regions        = edit.Regions.HasValue        ? edit.Regions.Value ?? Array.Empty<Core.Regions.TaggedRegion>() : current.Regions
  };

  private static async Task WriteAtomicAsync(FileInfo target, string content, CancellationToken cancellationToken) {
    // Write to a temp file in the same directory, then rename into place.
    // Keeps partial writes from leaving a truncated sidecar if the process dies.
    var tempName = target.FullName + ".tmp";
    await File.WriteAllTextAsync(tempName, content, new System.Text.UTF8Encoding(false), cancellationToken);

    if (File.Exists(target.FullName))
      File.Replace(tempName, target.FullName, destinationBackupFileName: null);
    else
      File.Move(tempName, target.FullName);
  }
}
