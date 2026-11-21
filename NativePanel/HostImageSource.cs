using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ImgCore;
using P1.C1;
using P1.C1.Common.ColorProfiles;
using P1.C1.Common.DestinationNaming;
using P1.C1.Components.IcScheduler;
using P1.C1.Core;
using P1.C1.Core.DestinationProcessing;
using P1.C1.Core.Interfaces;
using P1.C1.Core.Keyword;
using P1.C1.Core.ModelEntities;
using P1.C1.Framework;
using P1.C1.ImageCoreWrapper.CoordinateSystem;
using P1.C1.ImageCoreWrapper.ImageCore;
using P1.C1.ViewModels.DestinationNaming;
using P1.ModelCoreWrapper;

internal static class HostImageSource
{
    private static readonly FieldInfo? VariantReaderLock = typeof(VariantSettings)
        .GetField("m_ReaderLock", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Regex HighResolutionCounterLine = new(
        "^[ \\t]*\"highresProcessCounter\"[ \\t]*:[ \\t]*([0-9]+)[ \\t]*,?[ \\t]*\\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Lazy<Func<IGeometryTransformer, ICoreImage, IDestinationProcessingSettings, int, bool, ScaleCalc>> RecipeScale =
        new(() => NativeRecipeScale.Bind<IGeometryTransformer, ICoreImage, IDestinationProcessingSettings, ScaleCalc>(typeof(ScaleCalcHelpers)));
    private static readonly Lazy<Func<IDestinationProcessingSettings, FileFormat>> ReadRecipeFormat =
        new(NativeRecipeFileFormat.Bind<IDestinationProcessingSettings, FileFormat>);
    private static readonly NativeBitmapMetadata<IC_FileDestination> BitmapMetadata = new();
    private const int DraftEdgeLimit = 1200;
    private const int ScaledPreviewEdgeLimit = 4096;
    private const int NativePreviewEdgeLimit = RenderFrameLimits.MaxHighDepthEdge;
    private static readonly TimeSpan DraftScheduleTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NativeScheduleTimeout = TimeSpan.FromMinutes(3);
    private static readonly SemaphoreSlim SchedulerSlot = new(1, 1);
    private static readonly Type ProgramType = typeof(ImageCoreScheduler).Assembly.GetType("P1.C1.Program", throwOnError: true)!;
    private static readonly PropertyInfo SchedulerProperty = HostProperty("ImageCoreScheduler");
    private static readonly PropertyInfo? KeywordManagerProperty = ProgramType.GetProperty("KeywordLibraryManager",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly PropertyInfo ColorProfilesProperty = HostProperty("ColorProfileLibrary");
    private static readonly MethodInfo? GetDestinationManager = HostProperty("DocumentManager").PropertyType
        .GetMethod("GetDestinationDocumentManager", new[] { typeof(ICaptureOneDocument) });
    private static readonly MethodInfo? GetExportNamingManager = HostProperty("DocumentManager").PropertyType
        .GetMethod("GetExportNamingManager", new[] { typeof(ICaptureOneDocument) });
    private static readonly MethodInfo? GetRecipeSubfolderNamingManager = HostProperty("DocumentManager").PropertyType
        .GetMethod("GetOutputRecipeSubFolderNamingManager", new[] { typeof(ICaptureOneDocument) });
    private static readonly MethodInfo? GetExportRootLocation = ProgramType.Assembly
        .GetType("P1.C1.Components.ExportDialog.Tools.ExportDestination")
        ?.GetMethod("GetRootLocation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(ICaptureOneDocument), typeof(IDestinationProcessingSettings), typeof(IImage) }, null);
    internal sealed record Frame(int Width, int Height, byte[] FloatBytes, int BitDepth, FrameDescription? Description = null);
    internal sealed record ExportFrame(Frame Image, Rectangle Crop);
    internal sealed record PreviewCropIdentity(PreviewCropModel Model, string Profiles, Guid Image, string Path,
        long FileLength, DateTime WrittenUtc, DateTime CreatedUtc)
    {
        internal bool IsCropChangeFrom(PreviewCropIdentity before) => Model.Crop != before.Model.Crop
            && (this with { Model = before.Model }) == before && Model.PixelSettingsSha256 == before.Model.PixelSettingsSha256;
    }
    internal sealed class DevelopmentDeadlineException(string message) : TimeoutException(message);
    internal sealed class ExportSourceChangedException(string message) : InvalidOperationException(message);
    internal sealed record RecipeNaming(string Stem, Func<bool>? AdvanceCounter);
    internal sealed record RecipeInfo(string Name, string FormatId, string ColorProfileName, string ColorProfilePath,
        string CropMode, string RootFolderMode, string CustomRootFolder, string Subfolder, string Filename,
        string Suffix, int TiffBits, int JpegQuality, int Width, int Height, double Scale, bool IgnoreCrop,
        double Resolution, string ResolutionUnit, ExportMetadata Metadata, TiffCompressOption TiffCompression,
        RecipeNaming? Naming, string? SuggestedDirectory, string? ExactDirectory,
        bool AddSuffixToExistingFileName, bool SkipExistingOutput, SharpeningOutputControl Sharpening,
        ExportOverlay? Watermark, ExportOverlay? TransparencyOverlay, ExportOverlay? Annotations, SpektraExport.RecipeOmission Omissions,
        PsdCropPath? CropPath, PsdGuide[]? Guides)
    {
        internal string? SuggestedFilename => Naming?.Stem;
    }

    internal static (Guid VariantId, NativeImageRevision AdjustmentKey, bool OriginalAvailable) GetSelectedSourceState(object window)
    {
        var selected = HostProfile.ReadSelection(window).Variant;
        return (selected.UUID, ReadRevision(selected), selected.CoreImage?.IsFileAvailable == true);
    }

    internal static bool IsCurrentAvailableSource(object window, Guid expectedVariant, NativeImageRevision expectedRevision)
    {
        var (variant, revision, available) = GetSelectedSourceState(window);
        return available && variant == expectedVariant && revision == expectedRevision;
    }

    internal static NativeImageRevision GetRevision(object window, Guid variantId) => ReadRevision(RequireSelection(window, variantId).Variant);

    internal static PreviewCropIdentity? ReadPreviewCropIdentity(object window, Guid variantId, NativeImageRevision revision)
    {
        try { return TryReadCropIdentity(RequireSelection(window, variantId).Variant, revision); }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException) { return null; }
    }

    private static PreviewCropIdentity? TryReadCropIdentity(ICoreVariant variant, NativeImageRevision revision)
    {
        if (StartupHook.VerifiedHost?.FileVersion != "16.4.6.2414"
            || variant.CoreImage is not { IsRawFile: true, IsFileAvailable: true } image)
            return null;
        var path = image.FullpathFilename;
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;

        try
        {
            // Keep removable and network originals on the ordinary development path.
            if (new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed) return null;
            var file = new FileInfo(path);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            var settings = variant.Settings;
            if (VariantReaderLock?.FieldType != typeof(object) || VariantReaderLock.GetValue(settings) is not { } reader
                || !Monitor.TryEnter(reader))
                return null;
            string model;
            string? profiles;
            try
            {
                if (ReadRevision(variant) != revision || variant.LocalAdjustmentsLayers is not { Count: 0 }) return null;
                profiles = NativeProfileIdentity.TryRead(variant, verifyLibraryProfiles: true);
                model = settings.ModelCoreRepresentation;
            }
            finally { Monitor.Exit(reader); }

            return profiles is not null && PreviewCropModel.Read(model, revision.Geometry) is { } cropped
                && ReadRevision(variant) == revision && image.IsFileAvailable
                ? new(cropped, profiles, image.UUID, path, file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    internal static RecipeInfo? ActiveRecipe(object window)
    {
        var selection = HostProfile.ReadSelection(window);
        var settings = selection.Variant.GetICProcessSettings();
        return ReadRecipe(selection, ref settings);
    }

    internal static Snapshot Capture(object window, HostProfile.Target target,
        PreviewInputSpace inputSpace = PreviewInputSpace.Srgb, bool captureRecipe = false) =>
        CaptureSnapshot(window, target, inputSpace, captureRecipe, forExport: false);

    internal static Snapshot CaptureForExport(object window, HostProfile.Target target,
        PreviewInputSpace inputSpace, bool captureRecipe) =>
        CaptureSnapshot(window, target, inputSpace, captureRecipe, forExport: true);

    internal static Snapshot CaptureBatchExport(HostProfile.Selection selection, HostProfile.Target target,
        PreviewInputSpace inputSpace)
    {
        try { return CaptureSnapshot(selection, target, inputSpace, captureRecipe: true, forExport: true); }
        catch (ExportSourceChangedException)
        {
            // An inactive variant may finish loading during its first native
            // settings read. Take one fresh snapshot before verification.
            return CaptureSnapshot(selection, target, inputSpace, captureRecipe: true, forExport: true);
        }
    }

    internal static async Task WaitForNativeDevelopmentAsync(CancellationToken token)
    {
        // A canceled preview can return before its running Capture One callback.
        if (!await SchedulerSlot.WaitAsync(NativeScheduleTimeout, token).ConfigureAwait(false))
            throw new DevelopmentDeadlineException("Capture One did not finish the previous image development. Try Export again.");
        SchedulerSlot.Release();
    }

    private static Snapshot CaptureSnapshot(object window, HostProfile.Target target,
        PreviewInputSpace inputSpace, bool captureRecipe, bool forExport) =>
        CaptureSnapshot(RequireSelection(window, target.VariantId, forExport), target, inputSpace, captureRecipe, forExport);

    private static Snapshot CaptureSnapshot(HostProfile.Selection selection, HostProfile.Target target,
        PreviewInputSpace inputSpace, bool captureRecipe, bool forExport)
    {
        var variant = selection.Variant;
        var image = variant.CoreImage;
        if (image is null || !image.IsFileAvailable || !image.IsICProcessable || image.IsMovie || image.IsCorrupt)
            throw new InvalidOperationException("Capture One cannot develop the selected source image.");
        if (forExport) CheckOriginalImage(image, target);

        var revision = ReadRevision(variant);
        var settings = variant.GetICProcessSettings();
        CheckRevision(variant, target.VariantId, revision, forExport, "while capturing the image settings");
        string? nativeIdentity = null;
        if (forExport)
        {
            nativeIdentity = TryReadNativeIdentity(variant, revision);
            CheckRevision(variant, target.VariantId, revision, true, "while capturing the image settings");
        }

        var (profilePath, profileSha256) = ResolvePreviewProfile(inputSpace);
        var output = Utf8Marshaled_IC_MAX_PATH.Create(profilePath);
        settings.colorProfileList.profile2 = output;
        settings.colorProfileList.profile3 = output;
        settings.colorProfileList.count = 3;
        settings.proofMode = false;
        settings.thresholdTestUpper = false;
        settings.thresholdTestLower = false;
        settings.showFocusMask = false;
        settings.colorCorrectionInPreviewMode = false;
        settings.localAdjustShowHdl = LocalAdjustmentsMaskHandle.Empty;
        settings.localAdjustMaskDisplayType = IC_MaskDisplayType.mdtNone;

        var scheduler = SchedulerProperty.GetValue(null) as ImageCoreScheduler
            ?? throw new InvalidOperationException("Capture One’s image scheduler is unavailable.");
        var recipe = captureRecipe ? ReadRecipe(selection, ref settings) : null;
        var host = StartupHook.VerifiedHost ?? throw new InvalidOperationException("The Capture One image source requires a verified host.");
        var description = new FrameSourceDescription(target.VariantId, target.SourceFormat, inputSpace, profileSha256, revision,
            host.FileVersion, host.AssemblySha256);
        return new Snapshot(scheduler, selection.Document, variant, profilePath, settings, description, recipe, nativeIdentity, target);
    }

    private static RecipeInfo? ReadRecipe(HostProfile.Selection selection, ref IC_ProcessSettings settings)
    {
        var manager = GetDestinationManager?.Invoke(selection.Manager, new object[] { selection.Document }) as IDestinationDocumentManager;
        var recipe = manager?.SelectedDestination;
        if (recipe is null)
            return null;

        var ignoreCrop = recipe.CropExportMethod is CropExportMethod.Ignore or CropExportMethod.AsPath;
        var geometry = VariantGeometryTransformer.FromProcessSettings(ref settings);
        var scale = RecipeScale.Value(geometry, selection.Variant.CoreImage,
            recipe, ApplicationSettings.Default.PreviewSizeInPx, ignoreCrop);
        var variant = selection.Variant;
        var metadata = ReadRecipeMetadata(variant, recipe);
        var format = ReadRecipeFormat.Value(recipe);
        var cropPath = format == FileFormat.Psd && recipe.CropExportMethod == CropExportMethod.AsPath
            ? ReadPsdCropPath(geometry) : null;
        var compression = format == FileFormat.Tiff ? recipe.TiffCompression switch
        {
            TiffCompressionMethod.None => TiffCompressOption.None,
            TiffCompressionMethod.LZW => TiffCompressOption.Lzw,
            TiffCompressionMethod.Zip => TiffCompressOption.Zip,
            _ => throw new InvalidOperationException("Choose uncompressed, LZW or ZIP in Capture One’s TIFF recipe."),
        } : TiffCompressOption.Zip;
        var omissions = SpektraExport.RecipeOmission.None;
        var watermark = recipe.WatermarkType == WatermarkType.None ? null
            : NativeExportWatermark.Capture(selection, recipe, settings, (float)scale.Scale, ignoreCrop, WatermarkProfilePath);
        if (recipe.WatermarkType != WatermarkType.None && watermark is null)
            omissions |= SpektraExport.RecipeOmission.Watermark;
        ExportOverlay? annotations = null;
        if (recipe.IncludeAnnotations && variant.HasAnnotations
            && !NativeExportAnnotations.TryCapture(variant, geometry, settings, (float)scale.Scale, ignoreCrop,
                ((ColorProfileLibrary)ColorProfilesProperty.GetValue(null)!).AnnotationsColorProfile, out annotations))
            omissions |= SpektraExport.RecipeOmission.Annotations;
        var sharpening = recipe.SharpeningOutputControl;
        if (sharpening is not (SharpeningOutputControl.LensAndSharpening or SharpeningOutputControl.Lens or SharpeningOutputControl.Disabled))
            omissions |= SpektraExport.RecipeOmission.Sharpening;
        ExportOverlay? transparencyOverlay = null;
        if (recipe.IncludeOverlay && !NativeExportTransparencyOverlay.TryCapture(geometry, (float)scale.Scale,
            WatermarkProfilePath, out transparencyOverlay))
            omissions |= SpektraExport.RecipeOmission.TransparencyOverlay;
        var guides = recipe.IncludeGuides ? NativeExportGuides.Capture(selection, recipe, geometry) : null;
        if (recipe.IncludeGuides && guides is null)
            omissions |= SpektraExport.RecipeOmission.Guides;
        var (suggestedDirectory, exactDirectory) = ReadRecipeLocation(selection, recipe);
        return new RecipeInfo(recipe.Name, format.ToString(), recipe.ColorProfileName, recipe.ColorProfilePath,
            recipe.CropExportMethod.ToString(), recipe.DestinationRootFolder.ToString(), recipe.DestinationCustomRootFolder,
            recipe.DestinationSubFolder, recipe.DestinationFilename, recipe.DestinationSubName, recipe.TiffBits, recipe.JpegQuality,
            scale.ScaledPixelWidth, scale.ScaledPixelHeight, scale.Scale, ignoreCrop, recipe.Resolution, recipe.ResolutionUnit.ToString(),
            metadata, compression, ReadRecipeNaming(selection, recipe), suggestedDirectory, exactDirectory,
            recipe.FileOverwriteAction == ProcessedFileOverwriteAction.Rename,
            recipe.FileOverwriteAction == ProcessedFileOverwriteAction.Skip, sharpening, watermark, transparencyOverlay, annotations,
            omissions, cropPath, guides);
    }

    private static PsdCropPath? ReadPsdCropPath(VariantGeometryTransformer geometry)
    {
        try
        {
            var canvas = geometry.CanvasSize;
            return PsdCropPath.FromCanvas(CropInCanvas(geometry, canvas), canvas);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        {
            // An invalid crop still permits exporting the full image with a disclosure.
            return null;
        }
    }

    internal static string WatermarkProfilePath =>
        ((ColorProfileLibrary)ColorProfilesProperty.GetValue(null)!).WatermarkProfilePath;

    private static (string? Suggested, string? Exact) ReadRecipeLocation(HostProfile.Selection selection, IDestinationProcessingSettings recipe)
    {
        var variant = selection.Variant;
        if (variant.CoreImage is not { } image) return default;
        var root = recipe.DestinationRootFolder.ToString() switch
        {
            "CustomPath" => recipe.DestinationCustomRootFolder,
            "ImageFolder" => image.PathToImageDirectory,
            _ => null,
        };
        var format = recipe.DestinationSubFolder;
        var hasSubfolder = !string.IsNullOrWhiteSpace(format);
        string? subfolder = null;
        try
        {
            root = GetExportRootLocation?.Invoke(null, new object[] { selection.Document, recipe, image }) as string ?? root;
            if (!string.IsNullOrWhiteSpace(root) && hasSubfolder
                && GetRecipeSubfolderNamingManager?.Invoke(selection.Manager, new object[] { selection.Document }) is INamingManager naming
                && ((ISelectedCollectionsProvider)selection.Document).PrimarySelectedCollection is ISimpleCollection collection)
            {
                var now = DateTime.Now;
                subfolder = naming.RetrieveName(null, image, variant, recipe, collection.Name, null,
                    0, null, null, now, now, format);
            }
        }
        catch (Exception error) when (error is TargetInvocationException or InvalidOperationException or ArgumentException)
        {
            // A missing native preview still permits the manual Save location.
        }
        return SpektraExport.ExistingRecipeLocation(root, subfolder, hasSubfolder);
    }

    private static RecipeNaming? ReadRecipeNaming(HostProfile.Selection selection, IDestinationProcessingSettings recipe)
    {
        var format = recipe.DestinationFilename;
        if (string.IsNullOrWhiteSpace(format)
            || ((ISelectedCollectionsProvider)selection.Document).PrimarySelectedCollection is not ISimpleCollection collection)
            return null;
        try
        {
            if (GetExportNamingManager?.Invoke(selection.Manager, new object[] { selection.Document }) is not INamingManager naming)
                return null;
            var variant = selection.Variant;
            var collectionName = collection.Name;
            var now = DateTime.Now;
            string Resolve(bool increment) => naming.RetrieveName(
                naming.CreateNamingCounterToken(NamingCounterType.Output, argIncrementCounter: increment),
                variant.CoreImage, variant, recipe, collectionName, null, 0, null, null, now, null, format);
            var name = Resolve(increment: false);
            var stem = SpektraExport.ValidRecipeStem(name);
            if (stem is null)
                return null;
            // The native token advances lazily when it is used by name resolution.
            // Refresh the preview before consuming it to avoid advancing a stale suggestion.
            Func<bool>? advanceCounter = new DestinationName(format, null).HasCounter
                ? () => StringComparer.Ordinal.Equals(format, recipe.DestinationFilename)
                    && StringComparer.Ordinal.Equals(name, Resolve(increment: false))
                    && StringComparer.Ordinal.Equals(name, Resolve(increment: true))
                : null;
            return new RecipeNaming(stem, advanceCounter);
        }
        catch (Exception error) when (error is TargetInvocationException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static ExportMetadata ReadRecipeMetadata(ICoreVariant variant, IDestinationProcessingSettings recipe)
    {
        var keywordSet = ExportKeywordSet.Empty;
        var mode = recipe.MetadataIncludeKeywords;
        if (mode is MetadataIncludeKeywordsMode.All or MetadataIncludeKeywordsMode.Whitelist)
        {
            var whitelist = Array.Empty<string>();
            if (mode == MetadataIncludeKeywordsMode.Whitelist && recipe.MetadataSelectedLibrariesWhitelist is { Count: > 0 } libraries)
            {
                var manager = KeywordManagerProperty?.GetValue(null) as IKeywordLibraryManager
                    ?? throw new InvalidOperationException("Capture One’s selected keyword libraries could not be read.");
                whitelist = manager.GetKeywordsFromSharedLibraries(libraries.Select(item => item.UUID)).ToArray();
            }
            if (mode == MetadataIncludeKeywordsMode.All || whitelist.Length > 0)
            {
                var nativeKeywords = variant.SerializeVariantInfoAsXmp(mode, whitelist, false, false, false, false, false);
                keywordSet = ExportKeywordSet.ReadNativeXmp(nativeKeywords);
            }
        }
        var allOther = recipe.MetadataIncludeAllOther;
        var title = allOther ? variant.IptcStatusTitle : null;
        if (allOther && string.IsNullOrWhiteSpace(title)) title = variant.IptcHeadline;
        return new ExportMetadata(
            recipe.MetadataIncludeCopyright ? variant.IptcCopyrightNotice : null,
            allOther ? variant.IptcCreator : null,
            title,
            recipe.MetadataIncludeCameraData ? variant.CoreImage.DateTaken : null,
            recipe.MetadataIncludeRating ? variant.Rating : null,
            keywordSet.Flat, keywordSet.Hierarchical,
            Description: allOther ? variant.IptcDescription : null,
            Headline: allOther ? variant.IptcHeadline : null,
            Software: StartupHook.ProductName);
    }

    private static HostProfile.Selection RequireSelection(object window, Guid variantId, bool forExport = false)
    {
        var selection = HostProfile.ReadSelection(window);
        if (selection.Variant.UUID == variantId)
            return selection;
        const string message = "The selected Capture One image changed.";
        if (forExport)
            throw new ExportSourceChangedException(message + " Try Export again.");
        throw new OperationCanceledException(message);
    }

    private static NativeImageRevision ReadRevision(ICoreVariant variant)
    {
        var counters = variant.Settings.VariantProcessCounters;
        return new NativeImageRevision(counters.HighResCounter, counters.LowResCounter, counters.SyncCounter);
    }

    private static void CheckOriginalImage(ICoreImage? image, HostProfile.Target target)
    {
        if (image is null || image.UUID != target.ImageId
            || !string.Equals(image.FullpathFilename, target.SourcePath, StringComparison.OrdinalIgnoreCase))
            throw new ExportSourceChangedException("Capture One’s original image changed. Reopen Export to check the new source.");
    }

    private static void CheckRevision(ICoreVariant variant, Guid expectedVariant, NativeImageRevision expectedRevision,
        bool forExport, string phase, string? nativeIdentity = null)
    {
        var actualVariant = variant.UUID;
        var actualRevision = ReadRevision(variant);
        if (actualVariant == expectedVariant && actualRevision == expectedRevision)
            return;
        if (forExport && nativeIdentity is not null && actualVariant == expectedVariant && actualRevision.HighRes > expectedRevision.HighRes
            && actualRevision.LowRes == expectedRevision.LowRes && actualRevision.Geometry == expectedRevision.Geometry
            && string.Equals(TryReadNativeIdentity(variant, actualRevision), nativeIdentity, StringComparison.Ordinal))
        {
            StartupHook.Log("export.source.verified.v1", new { Phase = phase, Expected = expectedRevision, Observed = actualRevision });
            return;
        }
        var message = actualVariant == expectedVariant ? "Capture One image settings changed" : "The Capture One source image changed";
        if (!forExport)
            throw new OperationCanceledException($"{message} {phase}.");
        StartupHook.Log("export.source.changed.v1", new
        {
            Phase = phase,
            VariantMatches = actualVariant == expectedVariant,
            Expected = expectedRevision,
            Observed = actualRevision,
        });
        throw new ExportSourceChangedException($"{message} {phase}. Try Export again.");
    }

    private static string? TryReadNativeIdentity(ICoreVariant variant, NativeImageRevision expectedRevision, bool verifyLibraryProfiles = false)
    {
        var settings = variant.Settings;
        if (VariantReaderLock?.FieldType != typeof(object) || VariantReaderLock.GetValue(settings) is not { } readerLock
            || !Monitor.TryEnter(readerLock))
            return null;
        string model;
        string? masksSha256 = null;
        string? profilesSha256;
        try
        {
            if (ReadRevision(variant) != expectedRevision)
                return null;
            profilesSha256 = NativeProfileIdentity.TryRead(variant, verifyLibraryProfiles);
            if (profilesSha256 is null)
                return null;
            if (variant.LocalAdjustmentsLayers is { Count: > 0 } layers)
            {
                var identity = NativeLayerIdentity.TryRead(settings, layers);
                if (identity is null)
                    return null;
                model = identity.Model;
                masksSha256 = identity.MasksSha256;
            }
            else model = settings.ModelCoreRepresentation;
            if (ReadRevision(variant) != expectedRevision)
                return null;
        }
        finally { Monitor.Exit(readerLock); }

        var counters = HighResolutionCounterLine.Matches(model);
        if (counters.Count != 1)
            return null;
        var counter = counters[0].Groups[1];
        if (!long.TryParse(counter.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value != expectedRevision.HighRes)
            return null;
        var normalizedModel = model.Remove(counter.Index, counter.Length);
        var identitySha256 = $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedModel)))}:{masksSha256}:{profilesSha256}";
        return ReadRevision(variant) == expectedRevision
            ? identitySha256 : null;
    }

    private static PropertyInfo HostProperty(string name) =>
        ProgramType.GetProperty(name, BindingFlags.Static | BindingFlags.Public)
        ?? throw new MissingMemberException(ProgramType.FullName, name);

    internal static (string Path, string Sha256) ResolvePreviewProfile(PreviewInputSpace space)
    {
        var (label, path, digest) = space switch
        {
            PreviewInputSpace.Srgb => ("sRGB", ProfileLibrary().SRGBProfilePath, null),
            PreviewInputSpace.AdobeRgb => ("Adobe RGB (1998)", ProfileLibrary().AdobeRGB1998ProfilePath, null),
            PreviewInputSpace.ProPhotoRgb => ("ProPhoto RGB", ProjectProfilePath("ProPhoto RGB.icc"),
                "52FD9D9C4CB84C88E69EA0EBAF39C1BAAB871A4BD247010EE3AF6613768D0F9B"),
            PreviewInputSpace.LinearRec2020 => ("Linear Rec.2020", ProjectProfilePath("Linear Rec.2020.icc"),
                "A831F01D55F7D34983C94A5DB8A967FFC2FC64D33728ABA32F7906EDD4202931"),
            _ => throw new ArgumentOutOfRangeException(nameof(space)),
        };
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new FileNotFoundException($"The {label} preview ICC profile is missing{(path is null ? "." : $" at {path}.")}", path);
        using var data = File.OpenRead(path);
        if (digest is not null && data.Length > 4096)
            throw new InvalidDataException($"The {label} preview ICC profile is invalid. Run make_preview_profiles.py to restore {path}.");
        var hash = Convert.ToHexString(SHA256.HashData(data));
        if (digest is not null && hash != digest)
            throw new InvalidDataException($"The {label} preview ICC profile is invalid. Run make_preview_profiles.py to restore {path}.");
        return (path, hash);

        static ColorProfileLibrary ProfileLibrary() => ColorProfilesProperty.GetValue(null) as ColorProfileLibrary
            ?? throw new InvalidOperationException("Capture One’s output profile library is unavailable.");

        static string ProjectProfilePath(string filename)
        {
            var root = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")
                ?? throw new InvalidOperationException("Start Capture One using the Spektrafilm launcher.");
            return Path.Combine(root, "profiles", "preview", filename);
        }
    }

    internal sealed class Snapshot(ImageCoreScheduler scheduler, ICaptureOneDocument document, ICoreVariant variant,
        string profilePath, IC_ProcessSettings settings, FrameSourceDescription description, RecipeInfo? recipe, string? nativeIdentity,
        HostProfile.Target target)
    {
        internal NativeImageRevision AdjustmentKey => description.AdjustmentKey;
        internal PreviewCropIdentity? ReadPreviewCropIdentity() => TryReadCropIdentity(variant, AdjustmentKey);
        internal FrameSourceDescription SourceIdentity => description;
        internal RecipeInfo? Recipe => recipe;
        internal string? ReadRetryModel()
        {
            var expected = description.AdjustmentKey;
            var current = ReadRevision(variant);
            if (current != expected && (nativeIdentity is null || current.HighRes <= expected.HighRes
                || current.LowRes != expected.LowRes || current.Geometry != expected.Geometry
                || !string.Equals(TryReadNativeIdentity(variant, current), nativeIdentity, StringComparison.Ordinal)))
                return null;
            return TryReadNativeIdentity(variant, current, verifyLibraryProfiles: true);
        }

        internal void VerifyRetryModel(string expected)
        {
            if (!string.Equals(ReadRetryModel(), expected, StringComparison.Ordinal))
                throw new ExportSourceChangedException(
                    "Capture One source adjustments or camera profiles changed during the retry. Restore them or start a new export.");
        }

        internal Frame RenderNativePreview(CancellationToken token) =>
            Render(new RenderOptions(NativePreviewEdgeLimit, RenderPurpose.NativePreview), token).Image;

        internal Frame RenderScaledPreview(int maxLongEdge, CancellationToken token) =>
            Render(new RenderOptions(Math.Min(maxLongEdge, ScaledPreviewEdgeLimit), RenderPurpose.ScaledPreview), token).Image;

        internal Frame RenderDraft(int maxLongEdge, CancellationToken token) =>
            Render(new RenderOptions(Math.Min(maxLongEdge, DraftEdgeLimit), RenderPurpose.Draft), token).Image;

        internal ExportFrame RenderExport(int? maxLongEdge, CancellationToken token, bool followRecipeSize = false) =>
            Render(new RenderOptions(maxLongEdge ?? RenderFrameLimits.MaxHighDepthEdge, RenderPurpose.Export, followRecipeSize, maxLongEdge is not null), token);

        internal ExportFrame RenderVerifiedExport(CancellationToken token, bool followRecipeSize, string verifiedModel) =>
            Render(new RenderOptions(RenderFrameLimits.MaxHighDepthEdge, RenderPurpose.Export, followRecipeSize,
                VerifiedModel: verifiedModel), token);

        private ExportFrame Render(RenderOptions options, CancellationToken token)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxDimension);
            if (options.FollowRecipeSize && recipe is null)
                throw new InvalidOperationException("Select a Capture One export recipe and capture it before rendering at its size.");

            token.ThrowIfCancellationRequested();
            return ScheduleRender(options, token);
        }

        private ExportFrame ScheduleRender(RenderOptions options, CancellationToken token)
        {
            var timeout = options.Purpose == RenderPurpose.Draft ? DraftScheduleTimeout : NativeScheduleTimeout;
            using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
            work.CancelAfter(timeout);
            var workToken = work.Token;
            var releaseSlot = false;
            try
            {
                // Capture One doesn't remove jobs already transferred to the runner when
                // a listener is removed. Bound active work and clear canceled callbacks.
                SchedulerSlot.Wait(workToken);
                releaseSlot = true;
                var request = new QueuedRenderRequest<ICoreVariant, ExportFrame>(source =>
                {
                    workToken.ThrowIfCancellationRequested();
                    CheckRevision(source, description.VariantId, description.AdjustmentKey, options.ForExport,
                        "before image development", nativeIdentity);
                    if (options.VerifiedModel is { } modelBeforeDevelopment)
                        VerifyRetryModel(modelBeforeDevelopment);
                    // A timed-out caller can return while the native callback still owns the profile pathname.
                    using var sourceProfile = new FileStream(profilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (!string.Equals(Convert.ToHexString(SHA256.HashData(sourceProfile)), description.ProfileSha256, StringComparison.Ordinal))
                        throw new IOException("The input colour profile changed after the Capture One source was captured. Try the render again.");
                    return RenderOnScheduler(source, options, workToken);
                }, () => SchedulerSlot.Release(), out var result);
                releaseSlot = false; // The request now owns release, including a running native callback.
                try
                {
                    var listener = scheduler.AddListener(null, null, null);
                    try
                    {
                        scheduler.RequestUpdates(new ImageCoreScheduler.JobAndListener(
                            variant, document, IcSchedulerHandle.ProcessingJobTypes.PreviewForPrint, listener, TimeSpan.Zero, request.Run));
                        return result.WaitAsync(workToken).GetAwaiter().GetResult();
                    }
                    finally
                    {
                        request.Abandon();
                        scheduler.RemoveListener(listener);
                    }
                }
                finally { request.Abandon(); }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && workToken.IsCancellationRequested)
            {
                throw new DevelopmentDeadlineException($"Capture One did not finish developing the image in {timeout.TotalSeconds:0} seconds.");
            }
            finally
            {
                work.Cancel();
                if (releaseSlot)
                    SchedulerSlot.Release();
            }
        }

        private ExportFrame RenderOnScheduler(ICoreVariant source, RenderOptions options, CancellationToken token)
        {
            var renderSettings = settings;
            var geometry = options.ForExport ? VariantGeometryTransformer.FromProcessSettings(ref renderSettings) : null;
            var canvas = geometry?.CanvasSize ?? ImageCore.GetCanvasSize(new GeometryProcessSettings(ref renderSettings));
            if (!float.IsFinite(canvas.Width) || !float.IsFinite(canvas.Height) || canvas.Width < 1 || canvas.Height < 1)
                throw new InvalidDataException("Capture One returned an invalid image canvas.");

            // Render the full canvas so spatial effects keep the same neighborhood as the Viewer.
            // Only the delivered frame is cropped, after film and optical rendering.
            var crop = options.ForExport && !(options.FollowRecipeSize && recipe!.IgnoreCrop)
                ? CropInCanvas(geometry!, canvas)
                : new RectangleF(0, 0, canvas.Width, canvas.Height);
            var center = ImageCore.Canvas2Raw(ref renderSettings, new PointF(canvas.Width / 2, canvas.Height / 2));
            var longest = Math.Max(canvas.Width, canvas.Height);
            var pixelLimit = options.Purpose switch
            {
                RenderPurpose.Export or RenderPurpose.NativePreview => RenderFrameLimits.MaxHighDepthPixels,
                RenderPurpose.ScaledPreview => ScaledPreviewEdgeLimit * ScaledPreviewEdgeLimit,
                RenderPurpose.Draft => DraftEdgeLimit * DraftEdgeLimit,
                _ => throw new ArgumentOutOfRangeException(nameof(options)),
            };
            double scale;
            if (options.ForExport)
            {
                scale = options.FollowRecipeSize ? recipe!.Scale : 1;
                if (options.CallerLimitsExportEdge)
                    scale = Math.Min(scale, (double)options.MaxDimension / Math.Max(crop.Width, crop.Height));
                RenderFrameLimits.ValidateExportCanvas(canvas.Width, canvas.Height, scale);
            }
            else if (options.Purpose == RenderPurpose.NativePreview)
            {
                scale = 1;
                if (Math.Ceiling(longest) > options.MaxDimension
                    || Math.Ceiling(canvas.Width) * Math.Ceiling(canvas.Height) > pixelLimit)
                    throw new InvalidOperationException("The native Viewer canvas exceeds 16,384 pixels per side or 100 megapixels. Choose a smaller Viewer resolution.");
            }
            else
            {
                scale = Math.Min(1, options.MaxDimension / longest);
            }
            renderSettings.cropIsRelative = false;
            renderSettings.cropCenterX = center.X;
            renderSettings.cropCenterY = center.Y;
            renderSettings.cropWidth = canvas.Width;
            renderSettings.cropHeight = canvas.Height;
            renderSettings.cropScale = (float)scale;
            renderSettings.cropScaleView = 1;
            if (options.FollowRecipeSize && recipe is { Sharpening: SharpeningOutputControl.Lens or SharpeningOutputControl.Disabled })
            {
                // Apply only the recipe's RAW-development override. Geometry
                // and the working profile belong to the full-canvas film input.
                var adjustments = new FileDestination
                {
                    CropExportMethod = CropExportMethod.Default,
                    SharpeningOutputControl = recipe.Sharpening,
                    Scale = renderSettings.cropScale,
                };
                renderSettings = adjustments.OverrideSettings(renderSettings, argRecipeProofingMode: false);
            }

            var pixels = checked((long)Math.Ceiling(canvas.Width * scale) * (long)Math.Ceiling(canvas.Height * scale));
            using var memory = RenderMemoryBudget.SystemMemory.Reserve(RenderAllocation.NativeDevelopment, pixels,
                options.ForExport ? "Free memory or choose a smaller Export Recipe." : "Free memory or choose a smaller Viewer resolution.");
            using var bitmap = new ImageCoreBitmap();
            var coreImage = source.CoreImage
                ?? throw new InvalidOperationException("The selected variant no longer has a source image.");
            if (options.ForExport) CheckOriginalImage(coreImage, target);
            if (!coreImage.IsFileAvailable)
                throw new InvalidOperationException("Connect the original image for the full-resolution Spektrafilm render.");
            var image = coreImage.Handle
                ?? throw new InvalidOperationException("Capture One did not open this source image.");
            var result = DevelopNativeBitmap(bitmap, image, renderSettings, CancelNativeRender);
            token.ThrowIfCancellationRequested();
            if (result != IC_Result.resOK)
                throw new InvalidOperationException($"Capture One could not develop the {(options.ForExport ? "export source" : "preview")} ({result}).");
            CheckRevision(source, description.VariantId, description.AdjustmentKey, options.ForExport,
                "during image development", nativeIdentity);
            if (options.VerifiedModel is { } modelAfterDevelopment)
                VerifyRetryModel(modelAfterDevelopment);
            if (options.ForExport) CheckOriginalImage(source.CoreImage, target);
            var imageFrame = CopyPixels(bitmap, pixelLimit, token, options.Purpose, description);
            var pixelCrop = options.ForExport ? ViewerGeometry.ScaleCrop(crop, canvas, imageFrame.Width, imageFrame.Height)
                : new Rectangle(0, 0, imageFrame.Width, imageFrame.Height);
            return new ExportFrame(imageFrame, pixelCrop);

            void CancelNativeRender(IC_CallbackType type, int value, ref IC_CallbackReturnValue response, nint context)
            {
                if (type == IC_CallbackType.cbtProgress && token.IsCancellationRequested)
                    response = IC_CallbackReturnValue.cbrvAbort;
            }
        }

        private static IC_Result DevelopNativeBitmap(ImageCoreBitmap bitmap, IImageCoreImage image,
            IC_ProcessSettings renderSettings, ICPStatusCallback status)
        {
            using var emptyMetadata = new UTF8BytyesSafeHandle(string.Empty);
            using var emptyAuthor = BitmapMetadata.RequiresAuthor ? new UTF8BytyesSafeHandle(string.Empty) : null;
            using var processor = new ImageCoreProcessor("Spektrafilm full-resolution development");
            // Use the primary file's open ImageCore handle with no proxy path.
            var result = processor.SetSource(new SourceSettings(image, string.Empty));
            if (result != IC_Result.resOK)
                return result;

            // The native file processor supports a bitmap destination at the requested depth.
            var destination = new IC_FileDestination
            {
                fileType = IC_FileType.ftBitmap,
                bits = 16,
                resolution = 96,
                resolutionUnit = IC_ResolutionUnit.ruInch,
                destBitmapHandle = bitmap.Handle,
                guides = new IC_Guide[128],
                xmpMetadata = emptyMetadata,
                embeddedICCDisplayName = new byte[128],
            };
            result = processor.AddFileDestination(BitmapMetadata.SetUnsigned(destination, emptyAuthor));
            return result == IC_Result.resOK
                ? processor.ProcessToFiles(renderSettings, IC_ProcessQuality.pqBest, status)
                : result;
        }

    }

    private enum RenderPurpose { Draft, ScaledPreview, NativePreview, Export }

    private readonly record struct RenderOptions(int MaxDimension, RenderPurpose Purpose, bool FollowRecipeSize = false,
        bool CallerLimitsExportEdge = false, string? VerifiedModel = null)
    {
        internal bool ForExport => Purpose == RenderPurpose.Export;
    }

    private static RectangleF CropInCanvas(VariantGeometryTransformer geometry, SizeF canvas)
    {
        var native = geometry.CropCanvasConstrained;
        if (!float.IsFinite(native.Left) || !float.IsFinite(native.Top) || !float.IsFinite(native.Width)
            || !float.IsFinite(native.Height) || native.Width < 1 || native.Height < 1)
            throw new InvalidDataException("Capture One returned an invalid crop for this variant.");

        const float tolerance = 1;
        var right = native.Left + native.Width;
        var bottom = native.Top + native.Height;
        if (native.Left < -tolerance || native.Top < -tolerance || right > canvas.Width + tolerance || bottom > canvas.Height + tolerance)
            throw new InvalidOperationException("The selected crop extends outside the image canvas. Move the crop inside the image to export it with Spektrafilm.");
        return RectangleF.FromLTRB(Math.Max(0, native.Left), Math.Max(0, native.Top),
            Math.Min(canvas.Width, right), Math.Min(canvas.Height, bottom));
    }

    private static Frame CopyPixels(ImageCoreBitmap bitmap, int pixelLimit, CancellationToken token, RenderPurpose purpose,
        FrameSourceDescription source)
    {
        var format = (PixelFormat)ImgCoreDll.ICB_GetPixelFormat(bitmap.Handle);
        var destination = purpose switch
        {
            RenderPurpose.Export => "export",
            RenderPurpose.Draft => "draft Viewer",
            RenderPurpose.ScaledPreview => "scaled Viewer",
            _ => "native Viewer",
        };
        StartupHook.Log($"Capture One {destination} source {bitmap.Width} × {bitmap.Height}, native format {format}.");
        var nativeFormat = format switch
        {
            PixelFormat.ipfRGB16 => NativeSourceFormat.Rgb16,
            PixelFormat.ipfRGBA16 => NativeSourceFormat.Rgba16,
            PixelFormat.ipfRGB32F => NativeSourceFormat.Rgb32Float,
            _ => throw new InvalidDataException($"Capture One returned {format} for the high-depth {destination} source."),
        };
        var frame = CopyPixels(bitmap.Width, bitmap.Height, bitmap.Stride, format, bitmap.Data, token, pixelLimit);
        return frame with { Description = new FrameDescription(source, frame.Width, frame.Height, nativeFormat) };
    }

    internal static Frame CopyPixels(int width, int height, int stride, PixelFormat format, nint data, CancellationToken token) =>
        CopyPixels(width, height, stride, format, data, token, RenderFrameLimits.MaxHighDepthPixels);

    private static Frame CopyPixels(int width, int height, int stride, PixelFormat format, nint data,
        CancellationToken token, int pixelLimit)
    {
        var (pixelBytes, depth) = format switch
        {
            PixelFormat.ipfRGB16 => (6, 16),
            PixelFormat.ipfRGBA16 => (8, 16),
            PixelFormat.ipfRGB32F => (12, 32),
            _ => throw new InvalidDataException($"Capture One returned an unsupported high-depth bitmap format ({format})."),
        };
        if (width < 1 || height < 1 || (long)width * height > pixelLimit || stride < (long)width * pixelBytes
            || (long)(height - 1) * stride + (long)width * pixelBytes > int.MaxValue || data == 0)
            throw new InvalidDataException("Capture One returned an invalid or oversized preview bitmap.");

        token.ThrowIfCancellationRequested();
        var pixels = depth == 16
            ? HighDepthSource.Convert16(width, height, stride, data, format == PixelFormat.ipfRGBA16, token)
            : HighDepthSource.ConvertFloat(width, height, stride, data, token);
        return new Frame(width, height, pixels, depth);
    }
}
