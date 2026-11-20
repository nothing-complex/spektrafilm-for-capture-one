using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static partial class StartupHook
{
    private sealed record TiffComparison(string Source, string Path, string ProfileSha256, int Width, int Height, int Samples,
        int DifferentSamples, int MaximumCodeDifference, double MeanCodeDifference);

    private static TiffComparison CompareNativeTiff(Assembly panel, Type coreVariant, object variant, object captured,
        string source, string directory)
    {
        var wrapper = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "P1.C1.ImageCoreWrapper");
        var core = coreVariant.Assembly;
        var processorType = wrapper.GetType("P1.C1.ImageCoreWrapper.ImageCore.ImageCoreProcessor", true)!;
        var bitmapType = wrapper.GetType("P1.C1.ImageCoreWrapper.ImageCore.ImageCoreBitmap", true)!;
        var sourceType = wrapper.GetType("P1.C1.ImageCoreWrapper.ImageCore.SourceSettings", true)!;
        var destinationType = wrapper.GetType("ImgCore.IC_FileDestination", true)!;
        var metadataType = core.GetType("P1.C1.Core.DestinationProcessing.UTF8BytyesSafeHandle", true)!;
        var settings = captured.GetType().GetFields(Flags).Single(field => field.FieldType.Name == "IC_ProcessSettings").GetValue(captured)!;
        var file = source + "-native-16bit.tif";
        var path = Path.Combine(directory, file);
        Set(settings, "cropScale", .08f);
        Set(settings, "cropScaleView", 1f);
        using var processor = (IDisposable)Activator.CreateInstance(processorType, "Spektrafilm native TIFF parity QA")!;
        using var bitmap = (IDisposable)Activator.CreateInstance(bitmapType)!;
        using var unusedTiffBitmap = (IDisposable)Activator.CreateInstance(bitmapType)!;
        using var metadata = (IDisposable)Activator.CreateInstance(metadataType, string.Empty)!;
        var coreImage = coreVariant.GetProperty("CoreImage")!.GetValue(variant)!;
        var image = Prop(coreImage, "Handle")!;
        var input = Activator.CreateInstance(sourceType, image, string.Empty)!;
        RequireOk(processorType.GetMethod("SetSource", Flags)!.Invoke(processor, new[] { input }), "native source");

        var bitmapHandle = Prop(bitmap, "Handle")!;
        var add = processorType.GetMethod("AddFileDestination", Flags)!;
        var toBitmap = CreateDestination("ftBitmap");
        Set(toBitmap, "destBitmapHandle", bitmapHandle);
        RequireOk(add.Invoke(processor, new[] { toBitmap }), "native bitmap destination");
        var process = processorType.GetMethod("ProcessToFiles", Flags)!;
        var quality = Enum.Parse(process.GetParameters()[1].ParameterType, "pqBest");
        RequireOk(process.Invoke(processor, new[] { settings, quality, null }), "native bitmap development");
        processorType.GetMethod("ClearFileDestinations", Flags)!.Invoke(processor, null);
        var toTiff = CreateDestination("ftTIFF");
        // The wrapper marshals a SafeHandle for every destination, including TIFF.
        Set(toTiff, "destBitmapHandle", Prop(unusedTiffBitmap, "Handle")!);
        var filenameType = destinationType.GetField("fileName", Flags)!.FieldType;
        var filename = filenameType.GetMethod("Create", Flags, new[] { typeof(string) })!.Invoke(null, new object[] { path })!;
        if (!Equals(Prop(filename, "Value"), path)) throw new InvalidDataException("Native TIFF path could not be represented exactly.");
        Set(toTiff, "fileName", filename);
        RequireOk(add.Invoke(processor, new[] { toTiff }), "native TIFF destination");
        RequireOk(process.Invoke(processor, new[] { settings, quality, null }), "native TIFF development");

        var bitmapWidth = (int)Prop(bitmap, "Width")!;
        var bitmapHeight = (int)Prop(bitmap, "Height")!;
        var bitmapStride = (int)Prop(bitmap, "Stride")!;
        var bitmapData = Prop(bitmap, "Data")!;
        var dll = wrapper.GetType("ImgCore.ImgCoreDll", true)!;
        var formatId = dll.GetMethod("ICB_GetPixelFormat", Flags)!.Invoke(null, new[] { bitmapHandle })!;
        var nativeFormat = Enum.ToObject(wrapper.GetType("ImgCore.PixelFormat", true)!, formatId);
        var sourceHost = panel.GetType("HostImageSource", true)!;
        var copy = sourceHost.GetMethods(Flags).Single(method => method.IsStatic && method.Name == "CopyPixels" && method.GetParameters().Length == 6);
        var pixels = copy.Invoke(null, new[] { (object)bitmapWidth, bitmapHeight, bitmapStride, nativeFormat, bitmapData, CancellationToken.None })!;
        Check(Prop(pixels, "BitDepth") is 16 && bitmapWidth > 100 && bitmapHeight > 100,
            $"{source} uses Capture One's high-depth ImageCore bitmap destination");
        var lowDepth = Enum.Parse(nativeFormat.GetType(), "ipfBGRA8");
        try
        {
            copy.Invoke(null, new[] { (object)1, 1, bitmapStride, lowDepth, bitmapData, CancellationToken.None });
            throw new InvalidOperationException("An 8-bit bitmap was accepted by the high-depth Capture One copy path.");
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidDataException failure
            && failure.Message.Contains("high-depth", StringComparison.Ordinal))
        {
            Check(true, $"{source} cannot silently fall back to a Capture One 8-bit bitmap");
        }

        using var stream = File.OpenRead(path);
        var tiff = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad).Frames[0];
        Check(tiff.PixelWidth == bitmapWidth && tiff.PixelHeight == bitmapHeight && tiff.Format == PixelFormats.Rgb48
            && tiff.ColorContexts is { Count: > 0 }, $"{source} native TIFF retains the same RGB16 dimensions and embeds its profile");
        var description = Prop(captured, "SourceIdentity")!;
        using var icc = tiff.ColorContexts![0].OpenProfileStream();
        var iccSha256 = Convert.ToHexString(SHA256.HashData(icc));
        Check(Equals(Prop(description, "ProfileSha256"), iccSha256),
            $"{source} native TIFF embeds the exact selected Linear Rec.2020 ICC bytes");
        var rowStride = checked(bitmapWidth * 6);
        var tiffBytes = new byte[rowStride * bitmapHeight];
        tiff.CopyPixels(tiffBytes, rowStride, 0);
        var integer = MemoryMarshal.Cast<byte, ushort>(tiffBytes.AsSpan());
        var floats = MemoryMarshal.Cast<byte, float>(((byte[])Prop(pixels, "FloatBytes")!).AsSpan());
        var different = 0;
        var maximum = 0;
        long total = 0;
        for (var p = 0; p < bitmapWidth * bitmapHeight; p++)
        for (var c = 0; c < 3; c++)
        {
            var delta = Math.Abs((int)MathF.Round(65535 * floats[p * 4 + c]) - integer[p * 3 + c]);
            if (delta != 0) different++;
            maximum = Math.Max(maximum, delta);
            total += delta;
        }
        Check(different == 0, $"{source} has identical 16-bit samples between native TIFF and same-stage bitmap");
        return new TiffComparison(source, file, iccSha256, bitmapWidth, bitmapHeight, integer.Length, different, maximum, total / (double)integer.Length);

        object CreateDestination(string format)
        {
            var destination = Activator.CreateInstance(destinationType)!;
            Set(destination, "fileType", Enum.Parse(destinationType.GetField("fileType", Flags)!.FieldType, format));
            Set(destination, "bits", 16);
            Set(destination, "resolution", 96);
            Set(destination, "resolutionUnit", Enum.Parse(destinationType.GetField("resolutionUnit", Flags)!.FieldType, "ruInch"));
            Set(destination, "guides", Array.CreateInstance(destinationType.GetField("guides", Flags)!.FieldType.GetElementType()!, 128));
            Set(destination, "xmpMetadata", metadata);
            Set(destination, "embeddedICCDisplayName", new byte[128]);
            return destination;
        }

        static void Set(object item, string name, object value)
        {
            var field = item.GetType().GetField(name, Flags)!;
            field.SetValue(item, field.FieldType.IsInstanceOfType(value) ? value : Convert.ChangeType(value, field.FieldType, CultureInfo.InvariantCulture));
        }

        static void RequireOk(object? value, string stage)
        {
            if (value?.ToString() != "resOK") throw new InvalidOperationException($"{stage} failed with {value}.");
        }
    }
}
