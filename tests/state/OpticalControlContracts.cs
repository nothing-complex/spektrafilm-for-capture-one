using System.Globalization;
using System.Reflection;
using System.Text.Json;

internal static class OpticalControlContracts
{
    internal static void VerifyCompiled(string path)
    {
        var renderer = RendererParameters.ReadManifest(path);
        OpticalControls.VerifyRendererChoices(renderer);
        OpticalControls.VerifyRendererToggles(renderer);
        var optics = OpticalControls.Parameters.Values.Select(item => item.Parameter).OfType<OpticalSlider>();
        var film = FilmControls.Development.Values.Append(FilmControls.Exposure).Append(FilmControls.PrintExposure)
            .Where(control => control.WorkerKey is not null);
        var numbers = optics.Select(item => (item.Key, item.Minimum, item.Maximum, item.Initial, item.Increment))
            .Concat(film.Select(item => (item.WorkerKey!, item.Minimum, item.Maximum, item.Initial, item.Increment))).ToArray();
        foreach (var (key, minimum, maximum, initial, increment) in numbers)
        {
            renderer.VerifyNumericControl(key, minimum, maximum, initial, increment);
            foreach (var value in new[] { minimum, initial, maximum })
                renderer.VerifyArguments(["--" + key, RendererParameters.NumberToken(value)]);
        }
        Console.WriteLine($"PASS compiled choices and toggles match the managed definitions. {numbers.Length} numeric controls accept both endpoints and their defaults.");
    }

    internal static void Run(Action<string, bool> check)
    {
        const string opticsSection = "Tool / Spektrafilm optics";
        using var inventory = Assembly.GetExecutingAssembly().GetManifestResourceStream("Spektrafilm.PanelControls.json")!;
        using var document = JsonDocument.Parse(inventory);
        var tracked = document.RootElement.EnumerateArray()
            .Where(item => item.GetProperty("Section").GetString()!.StartsWith(opticsSection, StringComparison.Ordinal)
                && item.GetProperty("Option").GetString() is not null)
            .ToDictionary(item => item.GetProperty("Option").GetString()!, StringComparer.Ordinal);
        check("optical schema covers the tracked native inventory", tracked.Count == OpticalControls.Parameters.Count + 2
            && tracked["input-color"].GetProperty("Label").GetString() == "Input color space"
            && tracked["output-color"].GetProperty("Label").GetString() == "Render output encoding");

        foreach (var (key, binding) in OpticalControls.Parameters)
        {
            var expected = tracked[key];
            var kind = binding.Parameter switch
            {
                OpticalSlider => "slider",
                OpticalChoice => "select",
                OpticalToggle => "toggle",
                _ => throw new InvalidDataException($"Unknown optical control type for {key}."),
            };
            var capability = binding.Parameter is OpticalToggle { SendsToWorker: false } ? "optical-controls" : "vulkan";
            check($"{key} matches its tracked label, control type and worker ownership", expected.GetProperty("Label").GetString() == binding.Parameter.Label
                && expected.GetProperty("Kind").GetString() == kind && expected.GetProperty("Capability").GetString() == capability);
            if (binding.Parameter is OpticalSlider number)
                check($"{key} accepts its bounds and authored initial value", number.Contains(number.Minimum)
                    && number.Contains(number.Initial) && number.Contains(number.Maximum));
            if (binding.Parameter is OpticalChoice choice)
                check($"{key} has a stable default and distinct choice IDs", OpticalControls.HasChoice(key, choice.Default)
                    && choice.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() == choice.Options.Count);
            if (binding.RequiresToggle is { } required)
                check($"{key} is governed by an actual stock override", OpticalControls.Parameters[required].Parameter is OpticalToggle { SendsToWorker: false });
        }
        bool Off(string _) => false;
        bool On(string _) => true;
        check("scan omits exactly the 13 print settings and keeps native negative controls",
            OpticalControls.Parameters.Values.Count(option => option.ShouldSend(true, On) && !option.ShouldSend(false, On)) == 13
            && OpticalControls.Parameters["camera-diffusion-strength"].ShouldSend(false, On)
            && OpticalControls.Parameters["scanner-unsharp-amount"].ShouldSend(false, On));
        check("the 12 stock overrides need their individual switches",
            OpticalControls.Parameters.Values.Count(option => option.ShouldSend(true, On) && !option.ShouldSend(true, Off)) == 12
            && OpticalControls.Parameters["halation-red"].ShouldSend(true, key => key == "custom-halation-rgb")
            && !OpticalControls.Parameters["dir-r-to-g"].ShouldSend(true, key => key == "custom-halation-rgb"));

        var sliders = OpticalControls.Parameters.Values.Select(option => option.Parameter).OfType<OpticalSlider>().ToArray();
        var random = new Random(20261120);
        for (var index = 0; index < 32; index++)
        {
            var numbers = sliders.ToDictionary(slider => slider.Key,
                slider => slider.Minimum + random.NextDouble() * (slider.Maximum - slider.Minimum), StringComparer.Ordinal);
            var copy = PanelFiles.DecodePreset<Dictionary<string, double>>(PanelFiles.EncodeCompactPreset(numbers));
            foreach (var (key, value) in copy) OpticalControls.ValidateNumber(key, value);
            check($"optical look {index} roundtrips every finite setting", numbers.SequenceEqual(copy));
            var mutated = sliders[random.Next(sliders.Length)];
            var invalid = Math.BitIncrement(mutated.Maximum).ToString("R", CultureInfo.InvariantCulture);
            var json = System.Text.Encoding.UTF8.GetBytes($$"""{"{{mutated.Key}}":{{invalid}}}""");
            var valueOutsideRange = PanelFiles.DecodePreset<Dictionary<string, double>>(json)[mutated.Key];
            try
            {
                OpticalControls.ValidateNumber(mutated.Key, valueOutsideRange);
                check($"{mutated.Key} rejects an out-of-range persisted value", false);
            }
            catch (InvalidDataException)
            {
                check($"{mutated.Key} rejects an out-of-range persisted value", true);
            }
        }
    }
}
