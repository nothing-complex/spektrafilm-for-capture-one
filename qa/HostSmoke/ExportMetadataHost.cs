using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

public static partial class StartupHook
{
    private static void TestExportRecipeMetadata(Assembly assembly, object window, object exportHook, string fixture)
    {
        var profile = assembly.GetType("HostProfile", true)!;
        var target = profile.GetMethod("CaptureTarget", Flags)!.Invoke(null, new[] { window })!;
        var source = (string)Prop(target, "SourcePath")!;
        var raw = Path.Combine(fixture, "Raw");
        Check(string.Equals(Path.GetDirectoryName(Path.GetFullPath(source)), raw, StringComparison.OrdinalIgnoreCase)
            && (File.GetAttributes(raw) & FileAttributes.ReparsePoint) == 0
            && (File.GetAttributes(source) & FileAttributes.ReparsePoint) == 0,
            "metadata editing is authorized only for the private fixture RAW copy");
        string SourceHash() { using var bytes = File.OpenRead(source); return Convert.ToHexString(SHA256.HashData(bytes)); }
        var before = SourceHash();
        var selection = profile.GetMethod("ReadSelection", Flags)!.Invoke(null, new[] { window })!;
        var variant = Prop(selection, "Variant")!;
        var host = assembly.GetType("HostImageSource", true)!;
        var read = host.GetMethod("ReadRecipeMetadata", Flags)!;
        var variantType = read.GetParameters()[0].ParameterType;
        var recipeContract = read.GetParameters()[1].ParameterType;
        var destinations = ((Delegate)Field(exportHook, "destinations")!).DynamicInvoke()!;
        var currentRecipe = Prop(destinations, "SelectedDestination")!;
        var recipe = CreateDetachedMetadataRecipe(currentRecipe, recipeContract);
        Check(!ReferenceEquals(recipe, currentRecipe), "metadata settings belong to an unregistered Capture One recipe copy");
        var keywordManager = ((PropertyInfo)host.GetField("KeywordManagerProperty", Flags)!.GetValue(null)!).GetValue(null)
            ?? throw new InvalidOperationException("The licensed keyword manager is unavailable.");
        var shared = Field(keywordManager, "m_sharedKeywordLibraries") as IList
            ?? throw new InvalidOperationException("The licensed shared-library collection is unavailable.");
        const string allowed = "Spektrafilm Export Allowed";
        const string restricted = "Spektrafilm Export Private";
        const string unassigned = "Spektrafilm Export Unassigned";
        var keywordFactory = variantType.Assembly.GetType("P1.C1.Core.Keyword.KeywordLibraryFactory", true)!;
        var import = keywordFactory.GetMethod("ImportCustomLibrary", Flags)!;
        var textList = Path.Combine(folder, "Fixture keywords.txt");
        File.WriteAllLines(textList, new[] { allowed, unassigned });
        var importMode = Enum.Parse(import.GetParameters()[4].ParameterType, "LightroomTextFile");
        var library = import.Invoke(null, new[] { Path.Combine(folder, "Fixture keywords.cokeywords"), textList, "Spektrafilm QA", "1.0", importMode })
            ?? throw new InvalidOperationException("The isolated native keyword library could not be created.");
        var libraryId = (Guid)Prop(library, "UUID")!;
        var saved = new Dictionary<string, object?>();
        var oldKeywords = variantType.GetProperty("Keywords")!.GetValue(variant) is IEnumerable existing
            ? existing.Cast<string>().ToArray() : Array.Empty<string>();
        void Edit(string name, string value)
        {
            var property = variantType.GetProperty(name)!;
            saved.Add(name, property.GetValue(variant));
            property.SetValue(variant, value);
        }
        PropertyInfo RecipeProperty(string name) => recipe.GetType().GetProperty(name, Flags)
            ?? throw new MissingMemberException(recipe.GetType().FullName, name);
        void Recipe(string name, object value) => RecipeProperty(name).SetValue(recipe, value);
        void Mode(string name) => Recipe("MetadataIncludeKeywords", Enum.Parse(RecipeProperty("MetadataIncludeKeywords").PropertyType, name));
        string[] Keywords(string packet)
        {
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
            return XDocument.Parse(packet).Descendants(dc + "subject").Descendants(rdf + "li").Select(node => node.Value).ToArray();
        }
        object Metadata() => read.Invoke(null, new[] { variant, recipe })!;
        string Packet(object metadata) => (string)Call(metadata, "CreateXmp", 3, 2)!;
        try
        {
            shared.Add(library);
            variantType.GetMethod("ResetKeywords")!.Invoke(variant, new object[] { new[] { allowed, restricted } });
            Edit("IptcStatusTitle", "Fixture title 日");
            Edit("IptcHeadline", "Fixture headline");
            Edit("IptcDescription", "Editable fixture caption");
            Edit("IptcCreator", "Fixture creator");
            Edit("IptcCopyrightNotice", "Fixture © 2026");
            Recipe("MetadataIncludeCopyright", true);
            Recipe("MetadataIncludeAllOther", true);
            Recipe("MetadataIncludeCameraData", true);
            Recipe("MetadataIncludeRating", false);
            Recipe("MetadataIncludeGPS", true);
            var whitelist = RecipeProperty("MetadataSelectedLibrariesWhitelist").PropertyType.GenericTypeArguments[0];
            var libraries = Array.CreateInstance(whitelist, 1);
            libraries.SetValue(Activator.CreateInstance(whitelist, libraryId, "Fixture keywords"), 0);
            Recipe("MetadataSelectedLibrariesWhitelist", libraries);
            Mode("All");
            Check(Keywords(Packet(Metadata())).ToHashSet().SetEquals(new[] { allowed, restricted }), "the native All mode exports assigned keywords only");
            Mode("None");
            Check(Keywords(Packet(Metadata())).Length == 0, "the native None mode exports no assigned keywords");
            Mode("Whitelist");
            var captured = Metadata();
            var packet = Packet(captured);
            Check(Keywords(packet).SequenceEqual(new[] { allowed }) && !packet.Contains(restricted, StringComparison.Ordinal)
                && !packet.Contains(unassigned, StringComparison.Ordinal), "native selected keyword library exports only the assigned permitted keyword");
            Check(packet.Contains("Fixture title 日", StringComparison.Ordinal)
                && packet.Contains("Fixture headline", StringComparison.Ordinal)
                && packet.Contains("Editable fixture caption", StringComparison.Ordinal)
                && packet.Contains("Fixture creator", StringComparison.Ordinal)
                && packet.Contains("Fixture © 2026", StringComparison.Ordinal)
                && packet.Contains("Spektrafilm for Capture One", StringComparison.Ordinal)
                && !packet.Contains("GPS", StringComparison.Ordinal), "recipe snapshot preserves edited IPTC fields and provenance with location omitted");
            Recipe("MetadataIncludeAllOther", false);
            Recipe("MetadataIncludeCopyright", false);
            Recipe("MetadataIncludeCameraData", false);
            Recipe("MetadataSelectedLibrariesWhitelist", Array.CreateInstance(whitelist, 0));
            var reduced = Packet(Metadata());
            Check(!reduced.Contains("Fixture", StringComparison.Ordinal) && Keywords(reduced).Length == 0
                && !reduced.Contains("DateTimeOriginal", StringComparison.Ordinal), "recipe filters omit the excluded IPTC, camera date and empty-whitelist keywords");
            variantType.GetProperty("IptcDescription")!.SetValue(variant, "Later catalog edit");
            Check(Packet(captured) == packet, "accepted metadata snapshot remains exact after later recipe and variant edits");
            VerifyMetadataFiles(assembly, source, captured, packet);
        }
        finally
        {
            shared.Remove(library);
            foreach (var (name, value) in saved) variantType.GetProperty(name)!.SetValue(variant, value);
            variantType.GetMethod("ResetKeywords")!.Invoke(variant, new object[] { oldKeywords });
        }
        Check(SourceHash() == before, "metadata tests leave the private RAW bytes unchanged");
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native metadata assertions");
    }

    private static object CreateDetachedMetadataRecipe(object selected, Type contract)
    {
        var type = selected.GetType();
        var capabilities = Field(selected, "m_capabilities")!;
        var copy = type.GetConstructors(Flags).Single(item => item.GetParameters() is
            [{ ParameterType: var first }, { ParameterType: var second }]
            && first == contract && second.IsInstanceOfType(capabilities));
        return copy.Invoke(new[] { selected, capabilities });
    }

    private static void VerifyMetadataFiles(Assembly assembly, string sourcePath, object metadata, string packet)
    {
        var approved = assembly.GetType("OutputPathGuard", true)!.GetMethod("Approve", Flags)!;
        var save = assembly.GetType("SpektraExport", true)!.GetMethod("SaveApproved", Flags)!;
        foreach (var (extension, format) in new[] { (".png", PixelFormats.Rgba64), (".tif", PixelFormats.Rgb48) })
        {
            var stride = 3 * format.BitsPerPixel / 8;
            var pixels = Enumerable.Range(0, stride * 2).Select(i => (byte)(i * 7 + 1)).ToArray();
            var image = BitmapSource.Create(3, 2, 300, 180, format, null, pixels, stride);
            var output = Path.Combine(folder, "native-recipe-metadata" + extension);
            var destination = approved.Invoke(null, new[] { output, sourcePath })!;
            save.Invoke(null, new[] { destination, image, (object)CancellationToken.None, metadata, null });
            var bytes = File.ReadAllBytes(output);
            Check(bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(packet)) >= 0,
                "exact native recipe metadata snapshot reopens in " + extension);
            using var stream = new MemoryStream(bytes);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames[0];
            var reopened = new byte[pixels.Length];
            frame.CopyPixels(reopened, stride, 0);
            Check(frame.Format == format && reopened.SequenceEqual(pixels) && frame.ColorContexts is { Count: 1 }
                && Math.Abs(frame.DpiX - 300) < .04 && Math.Abs(frame.DpiY - 180) < .04 && frame.PixelWidth == 3 && frame.PixelHeight == 2,
                "native metadata export preserves normalized dimensions, non-square DPI, ICC and exact 16-bit pixels in " + extension);
        }
    }
}
