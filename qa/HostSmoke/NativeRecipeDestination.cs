using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static partial class StartupHook
{
    private static async Task TestNativeRecipeDestination(object bridge, object panel, object spatial, object window)
    {
        var recipe = ReadSelectedRecipeForTest(bridge, out var notifications);
        var saved = new[] { "FileType", "TiffBits", "DestinationRootFolder", "DestinationCustomRootFolder",
            "DestinationSubFolder", "DestinationFilename", "FileOverwriteAction", "CropExportMethod", "WatermarkType",
            "SharpeningOutputControl", "IncludeAnnotations", "IncludeOverlay", "IncludeGuides" }
            .Select(name => recipe.GetType().GetProperty(name, Flags)!)
            .ToDictionary(property => property.Name, property => (Property: property, Value: property.GetValue(recipe)));
        var output = Directory.CreateDirectory(Path.Combine(folder, "native-confirmed-output")).FullName;
        var proof = Directory.CreateDirectory(Path.Combine(output, "Proofs")).FullName;
        var normal = Field(Field(bridge, "normal")!, "command")!;
        var previous = Field(Field(bridge, "previous")!, "command")!;
        var journal = Field(spatial, "exportJobs")!;
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var exposure = Field(panel, "exposure")!;
        var exposureValue = exposure.GetType().GetProperty("Value", Flags)!;
        var priorExposure = exposureValue.GetValue(exposure);
        var wasEnabled = toggle.IsChecked;
        var fixture = new byte[] { 2, 9, 5 };
        notifications.SetValue(recipe, true);
        try
        {
            toggle.IsChecked = true;
            exposureValue.SetValue(exposure, 1.15);
            Choose("FileType", "Tiff");
            Set("TiffBits", 16);
            Choose("DestinationRootFolder", "CustomPath");
            Set("DestinationCustomRootFolder", output);
            Set("DestinationSubFolder", "Proofs");
            Set("DestinationFilename", "Native proof");
            Choose("FileOverwriteAction", "Rename");
            Choose("CropExportMethod", "Default");
            Choose("WatermarkType", "None");
            Choose("SharpeningOutputControl", "LensAndSharpening");
            Set("IncludeAnnotations", false);
            Set("IncludeOverlay", false);
            Set("IncludeGuides", false);
            var occupied = Path.Combine(proof, "Native proof.tif");
            var next = Path.Combine(proof, "Native proof 1.tif");
            File.WriteAllBytes(occupied, fixture);
            var originalReceipt = Call(journal, "ReadLatest");
            ShowNativeExportDialog(bridge, window, normal, () =>
            {
                if (Prop(bridge, "Dialog") is not Window { IsVisible: true } owner) return false;
                owner.Close();
                return true;
            });
            Check(Equals(Call(journal, "ReadLatest"), originalReceipt) && !File.Exists(next),
                "closing Capture One's Export window before confirmation creates no photo job");

            var panelAssembly = panel.GetType().Assembly;
            object CurrentRecipe() => panelAssembly.GetType("HostImageSource", true)!
                .GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
            var confirmedRecipe = CurrentRecipe();
            Check(Equals(Prop(confirmedRecipe, "ExactDirectory"), proof)
                && Equals(Prop(confirmedRecipe, "SuggestedFilename"), "Native proof")
                && Prop(confirmedRecipe, "Omissions")?.ToString() == "None",
                "the selected native recipe resolves an exact filename, directory and supported delivery");
            var profile = (string)Prop(confirmedRecipe, "ColorProfilePath")!;
            var icc = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(profile)));
            ShowNativeExportDialog(bridge, window, normal, () =>
            {
                if (Prop(bridge, "Dialog") is not Window { IsVisible: true } owner
                    || owner.DataContext is not { } model || Prop(model, "ExportCommand") is not ICommand confirm
                    || !confirm.CanExecute(null))
                    return false;
                confirm.Execute(null);
                return true;
            });
            var tiff = await WaitForReceipt(next, "Completed", originalReceipt);
            Check(File.ReadAllBytes(occupied).SequenceEqual(fixture),
                "native Add Suffix keeps the first file and writes the first free filename without a second dialog");
            var dimensions = VerifyDepth(next, PixelFormats.Rgb48, icc);

            Choose("FileType", "Png");
            Choose("FileOverwriteAction", "Skip");
            var png = Path.Combine(proof, "Native proof.png");
            ExecuteNativeExportCommand(previous);
            var pngReceipt = await WaitForReceipt(png, "Completed", tiff);
            Check(VerifyDepth(png, PixelFormats.Rgba64, icc) == dimensions,
                "Previous Settings writes the same selected high-depth geometry and profile directly");
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(png)));
            ExecuteNativeExportCommand(previous);
            var skipped = await WaitForReceipt(png, "Skipped", pngReceipt);
            Check(Prop(skipped, "Width") is 0 && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(png))) == digest,
                "repeating Previous Settings honours Skip and preserves the existing PNG bytes");

            Choose("FileType", "Psd");
            Set("TiffBits", 16);
            var psdRecipe = CurrentRecipe();
            Check(Prop(psdRecipe, "FormatId") is "Psd" && Prop(psdRecipe, "TiffBits") is 16
                && Prop(psdRecipe, "Omissions")?.ToString() == "None",
                "the native Photoshop recipe explicitly requests 16-bit delivery after the format change");
            var photoshop = Path.Combine(proof, "Native proof.psd");
            ExecuteNativeExportCommand(previous);
            var photoshopReceipt = await WaitForReceipt(photoshop, "Completed", skipped);
            using (var stream = File.OpenRead(photoshop))
            {
                var document = PsdFixture.Read(stream);
                Check(document.Version == 1 && (document.Width, document.Height) == dimensions
                    && Convert.ToHexString(SHA256.HashData(document.Resources[1039])) == icc,
                    "Previous Settings also saves the supported 16-bit Photoshop recipe directly");
            }
            foreach (var item in new[] { tiff, pngReceipt, photoshopReceipt })
            {
                var id = ((Guid)Prop(item, "JobId")!).ToString();
                Check(File.ReadLines(Path.Combine(folder, "panel.log")).Any(line =>
                    line.Contains("export.performance.v2", StringComparison.Ordinal)
                    && line.Contains(id, StringComparison.OrdinalIgnoreCase)
                    && RenderedFilm(line)),
                    "the native confirmation ran the film pipeline for the directly saved recipe file");
            }
            File.WriteAllText(Path.Combine(folder, "native-recipe-destination.json"), JsonSerializer.Serialize(new
            {
                nativeExport = next,
                previousSettings = png,
                photoshop,
                occupiedUnchanged = true,
                skippedUnchanged = true,
                dimensions.Width, dimensions.Height, icc,
            }));
            File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native recipe destination assertions");
        }
        finally
        {
            try { foreach (var (property, value) in saved.Values) property.SetValue(recipe, value); }
            finally
            {
                notifications.SetValue(recipe, false);
                toggle.IsChecked = wasEnabled;
                exposureValue.SetValue(exposure, priorExposure);
            }
        }

        void Set(string name, object value) => saved[name].Property.SetValue(recipe, value);
        void Choose(string name, string value) => Set(name, Enum.Parse(saved[name].Property.PropertyType, value));

        static bool RenderedFilm(string line)
        {
            using var log = JsonDocument.Parse(line);
            return log.RootElement.GetProperty("Details").GetProperty("FilmRenderAndTransportMs") is
                { ValueKind: JsonValueKind.Number } time && time.GetDouble() > 0;
        }

        async Task<object> WaitForReceipt(string path, string stage, object? before)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (Call(journal, "ReadLatest") is { } current && !Equals(current, before)
                    && Equals(Prop(current, "Destination"), path) && Prop(current, "Stage")?.ToString() == stage)
                    return current;
                await Task.Delay(50);
            }
            throw new InvalidOperationException($"Capture One did not record {stage} for the confirmed recipe path.");
        }

        (int Width, int Height) VerifyDepth(string path, PixelFormat format, string expectedIcc)
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames.Single();
            Check(frame.Format == format && frame.ColorContexts is { Count: 1 },
                "the directly saved recipe file reopens as 16-bit with an embedded profile");
            using var iccStream = frame.ColorContexts![0].OpenProfileStream();
            Check(Convert.ToHexString(SHA256.HashData(iccStream)) == expectedIcc, "the output contains the exact selected recipe ICC");
            return (frame.PixelWidth, frame.PixelHeight);
        }
    }
}
