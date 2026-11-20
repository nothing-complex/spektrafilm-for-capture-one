using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ImgCore;
using P1.C1;
using P1.C1.Common.ColorProfiles;
using P1.C1.Components.IcScheduler;
using P1.C1.Core;
using P1.C1.Core.DestinationProcessing;
using P1.C1.Core.Interfaces;
using P1.C1.Core.Keyword;
using P1.C1.Core.ModelEntities;
using P1.C1.Framework;
using P1.C1.ImageCoreWrapper.CoordinateSystem;
using P1.C1.ImageCoreWrapper.ImageCore;
using P1.C1.ViewModels.ProxyProcessing;
using P1.ModelCoreWrapper;

internal static class HostImageSource
{
    private const int PixelLimit = 1024 * 1024;
    private const int DraftEdgeLimit = 1200;
    private const int ScaledPreviewEdgeLimit = 4096;
    private const int NativePreviewEdgeLimit = RenderFrameLimits.MaxHighDepthEdge;
    private static readonly TimeSpan PreviewScheduleTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NativePreviewScheduleTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ExportScheduleTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ProxyRetryDelay = TimeSpan.FromMilliseconds(300);
    private static readonly SemaphoreSlim SchedulerSlot = new(1, 1);
    private static readonly Type ProgramType = typeof(ImageCoreScheduler).Assembly.GetType("P1.C1.Program", throwOnError: true)!;
    private static readonly PropertyInfo SchedulerProperty = HostProperty("ImageCoreScheduler");
    private static readonly PropertyInfo? KeywordManagerProperty = ProgramType.GetProperty("KeywordLibraryManager",
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly PropertyInfo ColorProfilesProperty = HostProperty("ColorProfileLibrary");
    private static readonly MethodInfo? GetProxyManager = HostProperty("DocumentManager").PropertyType
        .GetMethod("GetDocumentProxyManager", new[] { typeof(ICaptureOneDocument) });
    private static readonly MethodInfo? GetDestinationManager = HostProperty("DocumentManager").PropertyType
        .GetMethod("GetDestinationDocumentManager", new[] { typeof(ICaptureOneDocument) });
    private static readonly float[] Bgra8Values = CreateByteLookup();

    internal sealed record Frame(int Width, int Height, byte[] Codes, byte[] FloatBytes, int BitDepth, FrameDescription? Description = null);
    internal sealed record ExportFrame(Frame Image, Rectangle Crop);
    internal sealed record RecipeInfo(string Name, string FormatId, string ColorProfileName, string ColorProfilePath,
        string CropMode, string RootFolderMode, string CustomRootFolder, string Subfolder, string Filename,
        string Suffix, int TiffBits, int JpegQuality, int Width, int Height, double Scale, bool IgnoreCrop,
        double Resolution, string ResolutionUnit, ExportMetadata Metadata);

    internal static (Guid VariantId, NativeImageRevision AdjustmentKey) GetSelectedRevision(object window)
    {
        var selected = HostProfile.ReadSelection(window).Variant;
        return (selected.UUID, ReadRevision(selected));
    }

    internal static NativeImageRevision GetRevision(object window, Guid variantId) => ReadRevision(RequireSelection(window, variantId).Variant);

    internal static RecipeInfo? ActiveRecipe(object window)
    {
        var selection = HostProfile.ReadSelection(window);
        var settings = selection.Variant.GetICProcessSettings();
        return ReadRecipe(selection, ref settings);
    }

    internal static Snapshot Capture(object window, HostProfile.Target target,
        PreviewInputSpace inputSpace = PreviewInputSpace.Srgb, bool captureRecipe = false)
    {
        var selection = RequireSelection(window, target.VariantId);
        var variant = selection.Variant;
        var image = variant.CoreImage;
        if (image is null || !image.IsFileAvailable || !image.IsICProcessable || image.IsMovie || image.IsCorrupt)
            throw new InvalidOperationException("Capture One cannot develop the selected source image.");

        var revision = ReadRevision(variant);
        var settings = variant.GetICProcessSettings();
        if (ReadRevision(variant) != revision)
            throw new OperationCanceledException("Capture One settings changed while acquiring the preview source.");

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
        var proxyManager = GetProxyManager?.Invoke(selection.Manager, new object[] { selection.Document }) as IProxyManager;
        var recipe = captureRecipe ? ReadRecipe(selection, ref settings) : null;
        var host = StartupHook.VerifiedHost ?? throw new InvalidOperationException("The Capture One image source requires a verified host.");
        var description = new FrameSourceDescription(target.VariantId, target.SourceFormat, inputSpace, profileSha256, revision,
            host.FileVersion, host.AssemblySha256);
        return new Snapshot(scheduler, selection.Document, variant, target.SourcePath, profilePath, settings, description, proxyManager, recipe);
    }

    private static RecipeInfo? ReadRecipe(HostProfile.Selection selection, ref IC_ProcessSettings settings)
    {
        var manager = GetDestinationManager?.Invoke(selection.Manager, new object[] { selection.Document }) as IDestinationDocumentManager;
        var recipe = manager?.SelectedDestination;
        if (recipe is null)
            return null;

        var ignoreCrop = recipe.CropExportMethod is CropExportMethod.Ignore or CropExportMethod.AsPath;
        var geometry = VariantGeometryTransformer.FromProcessSettings(ref settings);
        var scale = ScaleCalcHelpers.CreateScaleCalcTakingQuickProofIntoAccount(geometry, selection.Variant.CoreImage,
            recipe, ApplicationSettings.Default.PreviewSizeInPx, ignoreCrop);
        var variant = selection.Variant;
        var metadata = ReadRecipeMetadata(variant, recipe);
        return new RecipeInfo(recipe.Name, recipe.FileType.ToString(), recipe.ColorProfileName, recipe.ColorProfilePath,
            recipe.CropExportMethod.ToString(), recipe.DestinationRootFolder.ToString(), recipe.DestinationCustomRootFolder,
            recipe.DestinationSubFolder, recipe.DestinationFilename, recipe.DestinationSubName, recipe.TiffBits, recipe.JpegQuality,
            scale.ScaledPixelWidth, scale.ScaledPixelHeight, scale.Scale, ignoreCrop, recipe.Resolution, recipe.ResolutionUnit.ToString(), metadata);
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

    private static HostProfile.Selection RequireSelection(object window, Guid variantId)
    {
        var selection = HostProfile.ReadSelection(window);
        return selection.Variant.UUID == variantId
            ? selection
            : throw new OperationCanceledException("The selected Capture One image changed.");
    }

    private static NativeImageRevision ReadRevision(ICoreVariant variant)
    {
        var counters = variant.Settings.VariantProcessCounters;
        return new NativeImageRevision(counters.HighResCounter, counters.LowResCounter, counters.SyncCounter);
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
        string sourcePath, string profilePath, IC_ProcessSettings settings, FrameSourceDescription description, IProxyManager? proxyManager, RecipeInfo? recipe)
    {
        internal NativeImageRevision AdjustmentKey => description.AdjustmentKey;
        internal FrameSourceDescription SourceIdentity => description;
        internal RecipeInfo? Recipe => recipe;

        internal Frame Render(int maxDimension, CancellationToken token) =>
            RenderWithRetry(new RenderOptions(maxDimension, RenderPurpose.Preview), token).Image;

        internal Frame RenderNativePreview(CancellationToken token) =>
            RenderWithRetry(new RenderOptions(NativePreviewEdgeLimit, RenderPurpose.NativePreview), token).Image;

        internal Frame RenderScaledPreview(int maxLongEdge, CancellationToken token) =>
            RenderWithRetry(new RenderOptions(Math.Min(maxLongEdge, ScaledPreviewEdgeLimit), RenderPurpose.ScaledPreview), token).Image;

        internal Frame RenderDraft(int maxLongEdge, CancellationToken token) =>
            RenderWithRetry(new RenderOptions(Math.Min(maxLongEdge, DraftEdgeLimit), RenderPurpose.Draft), token).Image;

        internal ExportFrame RenderExport(int? maxLongEdge, CancellationToken token, bool followRecipeSize = false) =>
            RenderWithRetry(new RenderOptions(maxLongEdge ?? RenderFrameLimits.MaxHighDepthEdge, RenderPurpose.Export, followRecipeSize, maxLongEdge is not null), token);

        private ExportFrame RenderWithRetry(RenderOptions options, CancellationToken token)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxDimension);
            if (options.FollowRecipeSize && recipe is null)
                throw new InvalidOperationException("Select a Capture One export recipe and capture it before rendering at its size.");

            token.ThrowIfCancellationRequested();
            // Keep this captured profile stable while Capture One reads it from its pathname.
            using var sourceProfile = new FileStream(profilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(sourceProfile)), description.ProfileSha256, StringComparison.Ordinal))
                throw new IOException("The input colour profile changed after the Capture One source was captured. Try the render again.");
            var requestProxyCheck = true;
            for (var attemptsLeft = 12; ; attemptsLeft--)
            {
                try
                {
                    return ScheduleRender(options, requestProxyCheck, token);
                }
                catch (ProxyPendingException) when (attemptsLeft > 0)
                {
                    requestProxyCheck = false;
                    Task.Delay(ProxyRetryDelay, token).GetAwaiter().GetResult();
                }
            }
        }

        private ExportFrame ScheduleRender(RenderOptions options, bool requestProxyCheck, CancellationToken token)
        {
            var timeout = options.Purpose switch
            {
                RenderPurpose.Export => ExportScheduleTimeout,
                RenderPurpose.NativePreview or RenderPurpose.ScaledPreview => NativePreviewScheduleTimeout,
                _ => PreviewScheduleTimeout,
            };
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
                    if (source.UUID != description.VariantId || ReadRevision(source) != description.AdjustmentKey)
                        throw new OperationCanceledException("Capture One settings changed before the image could render.");
                    return RenderOnScheduler(source, options, requestProxyCheck, workToken);
                }, () => SchedulerSlot.Release());
                releaseSlot = false; // The request now owns release, including a running native callback.
                try
                {
                    var listener = scheduler.AddListener(null, null, null);
                    try
                    {
                        scheduler.RequestUpdates(new ImageCoreScheduler.JobAndListener(
                            variant, document, IcSchedulerHandle.ProcessingJobTypes.PreviewForPrint, listener, TimeSpan.Zero, request.Run));
                        return request.Result.WaitAsync(workToken).GetAwaiter().GetResult();
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
                throw new TimeoutException($"Capture One did not finish developing the image in {timeout.TotalSeconds:0} seconds.");
            }
            finally
            {
                work.Cancel();
                if (releaseSlot)
                    SchedulerSlot.Release();
            }
        }

        private ExportFrame RenderOnScheduler(ICoreVariant source, RenderOptions options, bool requestProxyCheck, CancellationToken token)
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
                _ => PixelLimit,
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
            else if (options.Purpose is RenderPurpose.Draft or RenderPurpose.ScaledPreview)
            {
                scale = Math.Min(1, options.MaxDimension / longest);
            }
            else
            {
                var edgeScale = options.MaxDimension / longest;
                var areaScale = Math.Sqrt((pixelLimit - 2 * Math.Sqrt(pixelLimit)) / ((double)canvas.Width * canvas.Height));
                scale = Math.Min(1, Math.Min(edgeScale, areaScale));
            }
            renderSettings.cropIsRelative = false;
            renderSettings.cropCenterX = center.X;
            renderSettings.cropCenterY = center.Y;
            renderSettings.cropWidth = canvas.Width;
            renderSettings.cropHeight = canvas.Height;
            renderSettings.cropScale = options.UsesNativeDepth ? (float)scale : 1;
            renderSettings.cropScaleView = options.UsesNativeDepth ? 1 : (float)scale;

            var pixels = checked((long)Math.Ceiling(canvas.Width * scale) * (long)Math.Ceiling(canvas.Height * scale));
            using var memory = RenderMemoryBudget.SystemMemory.Reserve(RenderAllocation.NativeDevelopment, pixels,
                options.ForExport ? "Free memory or choose a smaller Export Recipe." : "Free memory or choose a smaller Viewer resolution.");
            using var bitmap = new ImageCoreBitmap();
            var coreImage = source.CoreImage
                ?? throw new InvalidOperationException("The selected variant no longer has a source image.");
            if (options.UsesNativeDepth && !coreImage.IsFileAvailable)
                throw new InvalidOperationException("Connect the original image for the full-resolution Spektrafilm render.");
            var image = coreImage.Handle
                ?? throw new InvalidOperationException("Capture One did not open this source image.");
            IC_Result result;
            if (options.UsesNativeDepth)
            {
                result = DevelopNativeBitmap(bitmap, image, renderSettings, CancelNativeRender);
            }
            else
            {
                using var targetId = new ReferenceCountedIDisposable<ImageCoreTargetID>(ImageCoreTargetID.CreateImageCoreTargetId(), true);
                result = ImageCoreProcessor.ProcessToBitmap(Path.GetFileName(sourcePath), bitmap, image, ref renderSettings,
                    ProcessQuality.Best, targetId, CancelNativeRender, false);
            }
            token.ThrowIfCancellationRequested();
            if (!options.UsesNativeDepth && IsProxyPending(result, coreImage))
            {
                if (requestProxyCheck && coreImage is IImage previewImage)
                    proxyManager?.AddToProxyCheck(new[] { previewImage });
                throw new ProxyPendingException(result);
            }
            if (result != IC_Result.resOK)
                throw new InvalidOperationException($"Capture One could not develop the {(options.ForExport ? "export source" : "preview")} ({result}).");
            if (ReadRevision(source) != description.AdjustmentKey)
                throw new OperationCanceledException("Capture One settings changed during image development.");
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
            result = processor.AddFileDestination(destination);
            return result == IC_Result.resOK
                ? processor.ProcessToFiles(renderSettings, IC_ProcessQuality.pqBest, status)
                : result;
        }

        private static bool IsProxyPending(IC_Result result, ICoreImage image) =>
            result is IC_Result.resProxyNotFound or IC_Result.resInvalidProxyFile or IC_Result.resProxyOpenError
                or IC_Result.resProxyTooOld or IC_Result.resProxyDoesNotMatchRAW or IC_Result.resCreatingProxy
                or IC_Result.resProxyGenerationInProgress
            || (result == IC_Result.resFileOpenError && !image.ProxyExists);
    }

    private enum RenderPurpose { Preview, Draft, ScaledPreview, NativePreview, Export }

    private readonly record struct RenderOptions(int MaxDimension, RenderPurpose Purpose, bool FollowRecipeSize = false,
        bool CallerLimitsExportEdge = false)
    {
        internal bool ForExport => Purpose == RenderPurpose.Export;
        internal bool UsesNativeDepth => Purpose != RenderPurpose.Preview;
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

    internal sealed class ProxyPendingException(IC_Result result) :
        InvalidOperationException($"Capture One’s native preview is not available yet ({result}).");

    private static Frame CopyPixels(ImageCoreBitmap bitmap, int pixelLimit, CancellationToken token, RenderPurpose purpose,
        FrameSourceDescription source)
    {
        var format = (PixelFormat)ImgCoreDll.ICB_GetPixelFormat(bitmap.Handle);
        var nativeDepth = purpose != RenderPurpose.Preview;
        if (nativeDepth)
        {
            var destination = purpose switch
            {
                RenderPurpose.Export => "export",
                RenderPurpose.Draft => "draft Viewer",
                RenderPurpose.ScaledPreview => "scaled Viewer",
                _ => "native Viewer",
            };
            StartupHook.Log($"Capture One {destination} source {bitmap.Width} × {bitmap.Height}, native format {format}.");
            if (format is not (PixelFormat.ipfRGB16 or PixelFormat.ipfRGBA16 or PixelFormat.ipfRGB32F))
                throw new InvalidDataException($"Capture One returned {format} for the high-depth {destination} source.");
        }
        var frame = CopyPixels(bitmap.Width, bitmap.Height, bitmap.Stride, format, bitmap.Data, token, pixelLimit,
            includePreviewCodes: !nativeDepth);
        var nativeFormat = format switch
        {
            PixelFormat.ipfARGB8 or PixelFormat.ipfBGRA8 => NativeSourceFormat.PremultipliedBgra8,
            PixelFormat.ipfRGB8 => NativeSourceFormat.Rgb8,
            PixelFormat.ipfRGB16 => NativeSourceFormat.Rgb16,
            PixelFormat.ipfRGBA16 => NativeSourceFormat.Rgba16,
            PixelFormat.ipfRGB32F => NativeSourceFormat.Rgb32Float,
            _ => throw new InvalidDataException($"Capture One returned an unsupported bitmap format ({format})."),
        };
        return frame with { Description = new FrameDescription(source, frame.Width, frame.Height, nativeFormat) };
    }

    internal static Frame CopyPixels(int width, int height, int stride, PixelFormat format, nint data, CancellationToken token) =>
        CopyPixels(width, height, stride, format, data, token, PixelLimit, includePreviewCodes: true);

    private static Frame CopyPixels(int width, int height, int stride, PixelFormat format, nint data,
        CancellationToken token, int pixelLimit, bool includePreviewCodes)
    {
        var (pixelBytes, depth) = format switch
        {
            PixelFormat.ipfARGB8 or PixelFormat.ipfBGRA8 => (4, 8),
            PixelFormat.ipfRGB8 => (3, 8),
            PixelFormat.ipfRGB16 => (6, 16),
            PixelFormat.ipfRGBA16 => (8, 16),
            PixelFormat.ipfRGB32F => (12, 32),
            _ => throw new InvalidDataException($"Capture One returned an unsupported bitmap format ({format})."),
        };
        if (width < 1 || height < 1 || (long)width * height > pixelLimit || stride < (long)width * pixelBytes
            || (long)(height - 1) * stride + (long)width * pixelBytes > int.MaxValue || data == 0)
            throw new InvalidDataException("Capture One returned an invalid or oversized preview bitmap.");

        token.ThrowIfCancellationRequested();
        if (!includePreviewCodes && depth == 16)
            return new Frame(width, height, Array.Empty<byte>(),
                HighDepthSource.Convert16(width, height, stride, data, format == PixelFormat.ipfRGBA16, token), depth);
        if (!includePreviewCodes && depth == 32)
            return new Frame(width, height, Array.Empty<byte>(), HighDepthSource.ConvertFloat(width, height, stride, data, token), depth);
        if (pixelBytes == 4)
            return CopyBgra8(data, width, height, stride, token);

        var rowBytes = width * pixelBytes;
        var channelCount = checked(width * height * 4);
        var codes = includePreviewCodes ? new byte[channelCount] : Array.Empty<byte>();
        var bytes = new byte[checked(channelCount * sizeof(float))];
        var floats = MemoryMarshal.Cast<byte, float>(bytes);
        var row = new byte[rowBytes];
        var pixelOffset = 0;
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            Marshal.Copy(data + y * stride, row, 0, rowBytes);
            for (var x = 0; x < rowBytes; x += pixelBytes)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var color = depth switch
                    {
                        8 => row[x + channel] / 255f,
                        16 => BitConverter.ToUInt16(row, x + channel * sizeof(ushort)) / 65535f,
                        32 => BitConverter.ToSingle(row, x + channel * sizeof(float)),
                        _ => throw new InvalidDataException("Unsupported preview channel depth."),
                    };
                    if (!float.IsFinite(color))
                        throw new InvalidDataException("Capture One returned a non-finite preview pixel.");
                    floats[pixelOffset + channel] = color;
                    if (includePreviewCodes)
                        codes[pixelOffset + 2 - channel] = (byte)MathF.Round(255 * Math.Clamp(color, 0, 1));
                }
                var alpha = format == PixelFormat.ipfRGBA16
                    ? BitConverter.ToUInt16(row, x + 6) / 65535f
                    : 1;
                floats[pixelOffset + 3] = alpha;
                if (includePreviewCodes)
                    codes[pixelOffset + 3] = (byte)MathF.Round(255 * alpha);
                pixelOffset += 4;
            }
        }
        return new Frame(width, height, codes, bytes, depth);
    }

    private static Frame CopyBgra8(nint data, int width, int height, int stride, CancellationToken token)
    {
        var rowBytes = width * 4;
        var codes = GC.AllocateUninitializedArray<byte>(rowBytes * height);
        var bytes = GC.AllocateUninitializedArray<byte>(codes.Length * sizeof(float));
        var floats = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());

        if (stride == rowBytes)
            Marshal.Copy(data, codes, 0, codes.Length);
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var rowStart = y * rowBytes;
            if (stride != rowBytes)
                Marshal.Copy(data + y * stride, codes, rowStart, rowBytes);

            for (var i = rowStart; i < rowStart + rowBytes; i += 4)
            {
                var alpha = codes[i + 3];
                if (alpha == 255)
                {
                    floats[i] = Bgra8Values[codes[i + 2]];
                    floats[i + 1] = Bgra8Values[codes[i + 1]];
                    floats[i + 2] = Bgra8Values[codes[i]];
                }
                else
                {
                    // Capture One's four-channel Viewer buffers are premultiplied BGRA.
                    for (var channel = 0; channel < 3; channel++)
                    {
                        var index = i + channel;
                        var color = alpha == 0 ? 0 : Math.Min(1, codes[index] / (float)alpha);
                        floats[i + 2 - channel] = color;
                        codes[index] = (byte)MathF.Round(255 * color);
                    }
                }
                floats[i + 3] = 1;
            }
        }
        return new Frame(width, height, codes, bytes, 8);
    }

    private static float[] CreateByteLookup()
    {
        var values = new float[256];
        for (var i = 0; i < values.Length; i++)
            values[i] = i / 255f;
        return values;
    }
}
