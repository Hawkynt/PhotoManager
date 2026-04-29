using System.Text;
using FileFormat.JpegArchive;
using PhotoManager.Core.Metadata.Containers;
// JpegContainerExifBridge lives in Containers namespace via the file in that folder.

namespace PhotoManager.Core.Metadata;

/// <summary>
/// Routes metadata writes to the best target for each file:
///   1. If a sidecar already exists, keep writing the sidecar (respects
///      workflows from Lightroom / darktable / earlier PhotoManager sessions).
///   2. Otherwise, if the format supports in-file XMP (JPEG today), embed
///      the XMP directly in the image so the metadata travels with the file.
///   3. Fall back to writing a sidecar — the old behaviour, correct for RAW
///      files where in-place modification would be destructive.
///
/// This keeps the user-facing contract ("my edits don't get lost, and
/// editable photos have metadata in the file") while never touching a RAW
/// file's bytes.
/// </summary>
public sealed class CompositeMetadataWriter : IMetadataWriter {
  private readonly IReadOnlyList<IContainerMetadataWriter> _containerWriters;
  private readonly XmpSidecarWriter _sidecarWriter = new();

  public CompositeMetadataWriter() : this(
    new JpegContainerMetadataWriter(),
    new PngContainerMetadataWriter(),
    new WebpContainerMetadataWriter(),
    new TiffContainerMetadataWriter()
  ) {
    // Formats we explicitly DON'T write in-place:
    //   - RAW files (.nef, .cr2, .arw, .dng, etc.): editing the container is
    //     destructive, so we always sidecar.
    //   - HEIC / HEIF (.heic, .heif): ISO-BMFF container with item-location
    //     tables that cascade on any size change. A correct writer is a
    //     substantial project — deferred to a dedicated slice. Falls through
    //     to the sidecar path for now; users still get non-destructive XMP.
  }

  public CompositeMetadataWriter(params IContainerMetadataWriter[] containerWriters) {
    this._containerWriters = containerWriters?.ToArray() ?? Array.Empty<IContainerMetadataWriter>();
  }

  public async Task<FileInfo> ApplyAsync(FileInfo imageFile, MetadataEdit edit, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(imageFile);
    ArgumentNullException.ThrowIfNull(edit);

    var sidecarPath = SidecarPath.For(imageFile);
    var sidecarExists = sidecarPath.Exists;

    // For formats that can embed XMP/IPTC/EXIF (JPEG/PNG/WebP/TIFF), writing
    // in-file is preferred — metadata lives with the photo and a new sidecar
    // is never created. An existing sidecar is kept in sync so legacy tools
    // that only read sidecars (Lightroom, darktable workflows) stay happy.
    if (this.TryGetInFileStrategy(imageFile, out var inFileStrategy)) {
      var inFileOk = await inFileStrategy(imageFile, edit, cancellationToken);
      if (inFileOk) {
        if (sidecarExists)
          await this._sidecarWriter.ApplyAsync(imageFile, edit, cancellationToken);
        return imageFile;
      }

      // In-file write failed (corrupt segment, oversized XMP, etc.) —
      // fall through to sidecar so the edit isn't lost. This is the only
      // path that *creates* a sidecar for a supported format.
    }

    return await this._sidecarWriter.ApplyAsync(imageFile, edit, cancellationToken);
  }

  /// <summary>
  /// Resolves the in-file write strategy for the given image. Returns false
  /// when the format has no in-file writer (RAW, HEIC, etc.) — the caller
  /// then goes straight to sidecar.
  /// </summary>
  private bool TryGetInFileStrategy(FileInfo imageFile,
      out Func<FileInfo, MetadataEdit, CancellationToken, Task<bool>> strategy) {
    if (IsJpegExtension(imageFile.Extension)) {
      strategy = async (file, edit, ct) => {
        var (xmpBytes, merged) = await this.BuildMergedAsync(file, edit, ct);
        var exifPatch = BuildExifPatch(edit);
        var iptc = BuildIptcFields(merged);
        return await JpegContainerExifBridge.WriteAsync(file, xmpBytes, exifPatch, iptc, ct);
      };
      return true;
    }

    var containerWriter = this._containerWriters.FirstOrDefault(w => w.SupportsExtension(imageFile.Extension));
    if (containerWriter != null) {
      strategy = async (file, edit, ct) => {
        var xmpBytes = await this.BuildMergedXmpAsync(file, edit, ct);
        var result = await containerWriter.WriteXmpAsync(file, xmpBytes, ct);
        return result == ContainerWriteResult.Written;
      };
      return true;
    }

    strategy = null!;
    return false;
  }

  /// <summary>
  /// Best-effort in-file XMP refresh after a sidecar write. Keeps the file's
  /// embedded metadata in sync with what the sidecar says so readers that
  /// only look at the file (Windows Explorer, ancient viewers, non-DAM
  /// tools) see the same thing as DAMs that read the sidecar. Failure is
  /// silent — the sidecar is still authoritative.
  /// </summary>
  private async Task TrySyncInFileAsync(FileInfo imageFile, MetadataEdit edit, CancellationToken cancellationToken) {
    try {
      if (IsJpegExtension(imageFile.Extension)) {
        var (xmpBytes, merged) = await this.BuildMergedAsync(imageFile, edit, cancellationToken);
        var exifPatch = BuildExifPatch(edit);
        var iptc = BuildIptcFields(merged);
        await JpegContainerExifBridge.WriteAsync(imageFile, xmpBytes, exifPatch, iptc, cancellationToken);
        return;
      }

      var containerWriter = this._containerWriters.FirstOrDefault(w => w.SupportsExtension(imageFile.Extension));
      if (containerWriter == null)
        return;

      var xmp = await this.BuildMergedXmpAsync(imageFile, edit, cancellationToken);
      await containerWriter.WriteXmpAsync(imageFile, xmp, cancellationToken);
    } catch {
      // Sidecar already has the truth — a failed in-file sync just means
      // some legacy readers will see the older embedded data.
    }
  }

  /// <summary>
  /// Loads the existing XMP (from in-file if present, otherwise reads the
  /// file via MetadataExtractor) and serializes the edit merged on top.
  /// We need the final bytes for the container writer to embed.
  /// </summary>
  private async Task<byte[]> BuildMergedXmpAsync(FileInfo imageFile, MetadataEdit edit, CancellationToken cancellationToken) {
    var (xmpBytes, _) = await this.BuildMergedAsync(imageFile, edit, cancellationToken);
    return xmpBytes;
  }

  /// <summary>
  /// Produces both the serialized XMP bytes AND the merged FullMetadata so
  /// callers that also need to emit IPTC (which needs the post-patch values)
  /// don't have to re-parse.
  /// </summary>
  private async Task<(byte[] xmp, FullMetadata merged)> BuildMergedAsync(FileInfo imageFile, MetadataEdit edit, CancellationToken cancellationToken) {
    FullMetadata existing = new();
    System.Xml.Linq.XDocument? sourceDoc = null;

    try {
      var bytes = await File.ReadAllBytesAsync(imageFile.FullName, cancellationToken);
      var xmpBytes = JpegSegmentSurgery.TryReadXmpSegment(bytes);
      if (xmpBytes != null) {
        var xml = Encoding.UTF8.GetString(xmpBytes);
        (existing, sourceDoc) = XmpSidecarFormatter.Parse(xml);
      }
    } catch {
      // File unreadable or not JPEG — write a fresh XMP packet.
    }

    var merged = ApplyPatch(existing, edit);
    var newXml = XmpSidecarFormatter.Serialize(merged, sourceDoc);
    return (Encoding.UTF8.GetBytes(newXml), merged);
  }

  /// <summary>
  /// Maps the XMP-native fields to their IPTC-IIM equivalents. Non-IPTC
  /// fields (regions, face embeddings, rating, color label) have no IIM tag
  /// and stay XMP-only. Writers that read IPTC (Windows Explorer details
  /// pane, digiKam, Lightroom legacy) will pick these up without needing
  /// to parse XMP.
  /// </summary>
  private static FileFormat.JpegArchive.IptcFields? BuildIptcFields(FullMetadata md) {
    string? dateYmd = null;
    string? timeHms = null;
    if (md.DateCreated is { } dt) {
      var inv = System.Globalization.CultureInfo.InvariantCulture;
      dateYmd = dt.ToString("yyyyMMdd", inv);
      timeHms = dt.ToString("HHmmss", inv) + "+0000";
    }

    var fields = new FileFormat.JpegArchive.IptcFields {
      ObjectName = md.Title,
      Caption = md.Caption,
      City = md.City,
      SubLocation = md.Location,
      ProvinceState = md.State,
      CountryCode = md.CountryCode,
      CountryName = md.Country,
      Keywords = md.Keywords.Count > 0 ? md.Keywords : null,
      // Per-source: use the IPTC-specific variant so diverged XMP/IPTC
      // values aren't collapsed. Falls back to the unified value for
      // readers/callers that don't populate per-source.
      ByLine = md.CreatorIptc ?? md.Creator,
      CopyrightNotice = md.CopyrightIptc ?? md.Copyright,
      Headline = md.Headline,
      Credit = md.Credit,
      Source = md.Source,
      Instructions = md.Instructions,
      DateCreatedYyyyMmDd = dateYmd,
      TimeCreatedHhMmSsZz = timeHms,
      DescriptionWriter = md.DescriptionWriter,
      TransmissionReference = md.JobIdentifier,
      CreatorJobTitle = md.CreatorJobTitle
    };
    return fields.IsEmpty ? null : fields;
  }

  private static bool IsJpegExtension(string extension) =>
    extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
    extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
    extension.Equals(".jpe", StringComparison.OrdinalIgnoreCase) ||
    extension.Equals(".jfif", StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Maps a <see cref="MetadataEdit"/> to the subset of fields the EXIF
  /// writer knows how to encode as native TIFF IFD entries. Fields that
  /// don't map cleanly (rating, color label, keywords, regions, face data)
  /// stay in the XMP packet only — there's no EXIF tag for them.
  /// </summary>
  private static ExifPatch BuildExifPatch(MetadataEdit edit) {
    GpsPoint? gps = null;
    if (edit.Gps is { HasValue: true, Value: { } cam })
      gps = new GpsPoint(cam.Latitude, cam.Longitude, cam.AltitudeMeters);

    GpsPoint? target = null;
    if (edit.TargetGps is { HasValue: true, Value: { } tgt })
      target = new GpsPoint(tgt.Latitude, tgt.Longitude);

    double? directionDeg = null;
    var directionMagnetic = false;
    if (edit.ImageDirection is { HasValue: true, Value: { } dir }) {
      directionDeg = dir.Degrees;
      directionMagnetic = dir.Reference == DirectionReference.Magnetic;
    }

    string? description = null;
    if (edit.Caption is { HasValue: true, Value: { } cap } && !string.IsNullOrEmpty(cap))
      description = cap;
    else if (edit.Title is { HasValue: true, Value: { } ttl } && !string.IsNullOrEmpty(ttl))
      description = ttl;

    return new ExifPatch {
      Gps = gps,
      TargetGps = target,
      ImageDirectionDegrees = directionDeg,
      ImageDirectionIsMagnetic = directionMagnetic,
      ImageDescription = description
    };
  }

  private static FullMetadata ApplyPatch(FullMetadata current, MetadataEdit edit) {
    // Per-source resolution for Creator/Copyright. Per-source override wins;
    // unified edit syncs both; otherwise keep whatever was already there.
    // Pre-computing these before the `new()` keeps the field expressions
    // short-circuited and readable.
    var creatorXmp  = edit.CreatorXmp.HasValue  ? edit.CreatorXmp.Value  :
                      edit.Creator.HasValue    ? edit.Creator.Value    : current.CreatorXmp;
    var creatorIptc = edit.CreatorIptc.HasValue ? edit.CreatorIptc.Value :
                      edit.Creator.HasValue    ? edit.Creator.Value    : current.CreatorIptc;
    var copyrightXmp  = edit.CopyrightXmp.HasValue  ? edit.CopyrightXmp.Value  :
                        edit.Copyright.HasValue    ? edit.Copyright.Value    : current.CopyrightXmp;
    var copyrightIptc = edit.CopyrightIptc.HasValue ? edit.CopyrightIptc.Value :
                        edit.Copyright.HasValue    ? edit.Copyright.Value    : current.CopyrightIptc;
    // Unified view prefers XMP (mirrors the read-path precedence).
    var creator   = creatorXmp   ?? creatorIptc;
    var copyright = copyrightXmp ?? copyrightIptc;

    return new FullMetadata {
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
    IsPick         = edit.IsPick.HasValue         ? edit.IsPick.Value         : current.IsPick,
    IsReject       = edit.IsReject.HasValue       ? edit.IsReject.Value       : current.IsReject,
    Keywords       = edit.Keywords.HasValue       ? edit.Keywords.Value ?? Array.Empty<string>() : current.Keywords,
    Title          = edit.Title.HasValue          ? edit.Title.Value          : current.Title,
    Caption        = edit.Caption.HasValue        ? edit.Caption.Value        : current.Caption,
    Creator        = creator,
    Copyright      = copyright,
    CreatorXmp     = creatorXmp,
    CreatorIptc    = creatorIptc,
    CopyrightXmp   = copyrightXmp,
    CopyrightIptc  = copyrightIptc,
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
  }
}
