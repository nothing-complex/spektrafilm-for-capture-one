using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal abstract record OpticalItem;
internal abstract record OpticalParameter(string Key, string Label) : OpticalItem;
internal sealed record OpticalSlider(string Key, string Label, double Minimum, double Maximum, double Initial,
    double Increment) : OpticalParameter(Key, Label)
{
    internal bool Contains(double value) => double.IsFinite(value) && value >= Minimum && value <= Maximum;
}
internal sealed record OpticalOption(string Id, string Label);
internal sealed record OpticalChoice(string Key, string Label, IReadOnlyList<OpticalOption> Options, string Default) : OpticalParameter(Key, Label);
internal sealed record OpticalToggle(string Key, string Label, bool Initial = false, bool SendsToWorker = true) : OpticalParameter(Key, Label);
internal sealed record OpticalCondition(string Key, string? Choice = null, bool Expected = true)
{
    internal bool IsMet(Func<string, bool> isOn, Func<string, string> selected) =>
        (Choice is { } choice ? selected(Key) == choice : isOn(Key)) == Expected;
}
internal sealed record OpticalRequirement(string Hint, OpticalCondition Condition, OpticalCondition? Alternative = null)
{
    internal bool IsMet(Func<string, bool> isOn, Func<string, string> selected) =>
        Condition.IsMet(isOn, selected) || Alternative?.IsMet(isOn, selected) == true;
}
internal sealed record OpticalSection(string? Key, string Label, IReadOnlyList<OpticalItem> Items,
    bool PrintOnly = false, OpticalRequirement? Requires = null) : OpticalItem;
internal sealed record OpticalInput(PreviewInputSpace Space, string Id, string Label);

internal static class OpticalControls
{
    internal static IReadOnlyList<OpticalInput> Inputs { get; } = Array.AsReadOnly<OpticalInput>([
        new(PreviewInputSpace.Srgb, "srgb", "sRGB"),
        new(PreviewInputSpace.AdobeRgb, "adobe_rgb", "Adobe RGB (1998)"),
        new(PreviewInputSpace.ProPhotoRgb, "prophoto", "ProPhoto RGB"),
        new(PreviewInputSpace.LinearRec2020, "linear_rec2020", "Linear Rec.2020"),
    ]);
    internal static IReadOnlyList<OpticalOption> Outputs { get; } = Array.AsReadOnly<OpticalOption>([
        new("srgb", "sRGB"), new("rec709_gamma24", "Rec.709 gamma 2.4"), new("linear_rec709", "Linear Rec.709"),
    ]);
    private static readonly IReadOnlyList<OpticalOption> Diffusion = Array.AsReadOnly<OpticalOption>([
        new("off", "Off"), new("black_pro_mist", "Black Pro-Mist"), new("glimmerglass", "Glimmerglass"),
        new("pro_mist", "Pro-Mist"), new("cinebloom", "CineBloom"),
    ]);
    private static readonly OpticalRequirement GrainOn =
        new("Choose a grain model to adjust this setting.", new("grain-model", "off", false));
    private static readonly OpticalRequirement ParticleModel =
        new("Choose Preview or Production grain to adjust this setting.", new("grain-model", "synthesis", false));
    private static readonly OpticalRequirement EmulsionBlur =
        new("Enable emulsion sublayers to adjust this setting in Grain synthesis.",
            new("grain-model", "synthesis", false), new("grain-sublayers"));
    private static readonly OpticalRequirement GrainMicrostructure =
        new("Enable emulsion sublayers to adjust this setting.", new("grain-sublayers"));

    internal static IReadOnlyList<OpticalSection> Sections { get; } = Array.AsReadOnly<OpticalSection>([
        new("optics-metering", "Exposure metering", [
            new OpticalToggle("auto-exposure", "Automatic film exposure"),
            new OpticalSection(null, "", [
                new OpticalChoice("auto-exposure-meter", "Metering", [new("center_weighted", "Center weighted"), new("median", "Median")], "center_weighted"),
            ], Requires: new("Enable automatic film exposure to choose a metering method.", new("auto-exposure"))),
        ]),
        new("optics-halation", "Halation and scatter", [
            new OpticalSlider("halation-amount", "Halation amount", 0, 4, 0, .05),
            new OpticalSlider("halation-scale", "Halation spread", 0, 4, 1, .05),
            new OpticalSlider("scatter-amount", "Emulsion scatter", 0, 2, 0, .05),
            new OpticalSlider("scatter-scale", "Emulsion scatter spread", 0, 4, 1, .05),
            new OpticalSlider("halation-boost", "Highlight boost (EV)", 0, 20, 0, .1),
            new OpticalSlider("halation-boost-range", "Highlight boost range", 0, 1, .3, .01),
            new OpticalSlider("halation-protect", "Highlight protection (EV)", 0, 10, 4, .1),
            new OpticalToggle("custom-halation-rgb", "Override stock halation channels", SendsToWorker: false),
            new OpticalSection(null, "", [
                new OpticalSlider("halation-red", "Red strength", 0, 1, .05, .005),
                new OpticalSlider("halation-green", "Green strength", 0, 1, .015, .005),
                new OpticalSlider("halation-blue", "Blue strength", 0, 1, 0, .005),
            ], Requires: new("Enable the stock halation override to adjust individual channels.", new("custom-halation-rgb"))),
        ]),
        DiffusionSection("camera", "Camera diffusion"),
        DiffusionSection("print", "Print diffusion", printOnly: true),
        new("optics-grain", "Spektrafilm grain", [
            new OpticalChoice("grain-model", "Grain model", [
                new("off", "Off"), new("preview", "Preview"), new("production", "Production"), new("synthesis", "Grain synthesis"),
            ], "off"),
            new OpticalSection(null, "", [
                new OpticalSection(null, "", [
                    new OpticalSlider("grain-amount", "Grain amount", 0, 2, 1, .05),
                    new OpticalSlider("grain-saturation", "Grain saturation", 0, 1, 1, .05),
                ], Requires: ParticleModel),
                new OpticalSlider("grain-final-blur", "Final blur (µm)", 0, 25, 7.17, .01),
            ], Requires: GrainOn),
            new OpticalChoice("film-format", "Film format", [
                new("8", "8 mm"), new("super8", "Super 8"), new("16", "16 mm"), new("super16", "Super 16"),
                new("35", "35 mm"), new("super35", "Super 35"), new("65", "65 mm"), new("70", "IMAX 70"),
            ], "35"),
            new OpticalSection(null, "", [
                new OpticalSlider("grain-seed", "Seed", 0, 32767, 1, 1),
                new OpticalSection("optics-particle-grain", "Particle grain", [
                    new OpticalToggle("grain-sublayers", "Use emulsion sublayers", true),
                    new OpticalSection(null, "", [
                        new OpticalSection(null, "", [
                            new OpticalSlider("grain-sublayer-count", "Sublayers", 1, 8, 1, 1),
                        ], Requires: new("Turn off Use emulsion sublayers to set the fallback count.", new("grain-sublayers", Expected: false))),
                    ], Requires: ParticleModel),
                    new OpticalSection(null, "", [
                        new OpticalSlider("grain-particle-area", "Particle area (µm²)", .01, 5, .1, .01),
                    ], Requires: EmulsionBlur),
                    new OpticalSection(null, "", [
                        new OpticalSlider("grain-dye-cloud-blur", "Dye cloud blur (µm)", 0, 10, 1, .1),
                    ], Requires: new("Enable emulsion sublayers to adjust dye cloud blur in this model.",
                        new("grain-model", "preview"), new("grain-sublayers"))),
                    new OpticalSection(null, "", [
                        new OpticalSection(null, "", [
                            new OpticalSlider("grain-microstructure", "Microstructure scale", 0, 4, .2, .05),
                            new OpticalSlider("grain-microstructure-sigma", "Microstructure width (nm)", 0, 500, 30, 1),
                        ], Requires: GrainMicrostructure),
                    ], Requires: new("Choose Production or Grain synthesis to tune microstructure.", new("grain-model", "preview", false))),
                    new OpticalSection(null, "", [
                        .. Channels("grain-particle-scale", "Particle scale", .01, 20, (1.2, 1, 2.5), .01),
                        .. Channels("grain-density-min", "Minimum density", 0, 1, (.04, .05, .06), .01),
                    ], Requires: EmulsionBlur),
                    new OpticalSection(null, "", [
                        .. Channels("grain-uniformity", "Uniformity", 0, 1, (.99, .97, .98), .01),
                    ], Requires: ParticleModel),
                    new OpticalSection(null, "", [
                        new OpticalSection(null, "", [.. GrainLayers("grain-layer-scale")], Requires: GrainMicrostructure),
                    ], Requires: new("Choose Production or Grain synthesis to adjust layer scales.", new("grain-model", "preview", false))),
                ]),
            ], Requires: GrainOn),
            new OpticalSection("optics-grain-synthesis", "Grain synthesis", [
                new OpticalToggle("grain-synthesis-layered", "Layered grain", true),
                new OpticalSlider("grain-synthesis-size", "Size", .25, 4, 1, .05),
                new OpticalSlider("grain-synthesis-amount", "Amount", 0, 3, 1, .05),
                new OpticalSlider("grain-synthesis-sharpness", "Sharpness", .25, 4, 1, .05),
                new OpticalSlider("grain-synthesis-quality", "Quality", .25, 4, 1, .05),
                new OpticalSlider("grain-synthesis-samples", "Samples", 1, 2048, 128, 1),
                new OpticalSlider("grain-synthesis-radius", "Mean radius (µm)", .05, 10, .25, .01),
                new OpticalSlider("grain-synthesis-radius-sd", "Radius variation", 0, 1, 0, .01),
                new OpticalSlider("grain-synthesis-observation", "Observation width (µm)", 0, 20, 1, .1),
                new OpticalSlider("grain-synthesis-cell", "Cell size ratio", .25, 2, 1, .05),
                new OpticalSlider("grain-synthesis-radius-quantile", "Radius quantile", .95, .9999, .999, .0001),
                new OpticalSlider("grain-synthesis-epsilon", "Coverage tolerance", .000001, .01, .0001, .000001),
                new OpticalSlider("grain-synthesis-cell-cap", "Max grains per cell", 1, 128, 32, 1),
                .. Channels("grain-synthesis-radius-scale", "Radius scale", .01, 20, (1.2, 1, 2.5), .01),
                new OpticalSection(null, "", [.. GrainLayers("grain-synthesis-layer-scale")],
                    Requires: new("Enable layered grain to adjust individual layer scales.", new("grain-synthesis-layered"))),
            ], Requires: new("Choose the Grain synthesis model to adjust this setting.", new("grain-model", "synthesis"))),
        ]),
        new("optics-scanning", "Scanning", [
            new OpticalToggle("scanner-enabled", "Enable scanner controls"),
            new OpticalSection(null, "", [
                new OpticalToggle("scanner-white", "Correct white level"),
                new OpticalSection(null, "", [
                    new OpticalSlider("scanner-white-level", "White point", 0, 1, .98, .01),
                ], Requires: new("Enable white-level correction to adjust the white point.", new("scanner-white"))),
                new OpticalToggle("scanner-black", "Correct black level"),
                new OpticalSection(null, "", [
                    new OpticalSlider("scanner-black-level", "Black point", 0, 1, .01, .01),
                ], Requires: new("Enable black-level correction to adjust the black point.", new("scanner-black"))),
                new OpticalSection(null, "", [
                    new OpticalSlider("scanner-glare", "Scanner glare", 0, .2, .03, .001),
                    new OpticalSlider("scanner-glare-roughness", "Glare roughness", 0, 4, .7, .05),
                    new OpticalSlider("scanner-glare-blur", "Glare blur", 0, 32, .5, .1),
                ], PrintOnly: true),
                new OpticalSlider("scanner-mtf", "Scanner MTF50 (lp/mm)", 0, 300, 60, 1),
                new OpticalSlider("scanner-unsharp-radius", "Unsharp radius (µm)", 0, 100, 5, .5),
                new OpticalSlider("scanner-unsharp-amount", "Unsharp amount", 0, 4, .7, .05),
            ], Requires: new("Enable scanner controls to adjust this setting.", new("scanner-enabled"))),
        ]),
        new("optics-enlarger", "Enlarger positioning", [
            new OpticalSlider("enlarger-scale", "Enlarger scale", 1, 32, 1, .1),
            new OpticalSlider("enlarger-x", "Horizontal offset (%)", -100, 100, 0, 1),
            new OpticalSlider("enlarger-y", "Vertical offset (%)", -100, 100, 0, 1),
        ]),
        new("optics-couplers", "Coupler diffusion", [
            new OpticalSlider("dir-diffusion", "Diffusion width (µm)", 0, 100, 20, .5),
            new OpticalSlider("dir-tail-size", "Tail width (µm)", 0, 1000, 200, 1),
            new OpticalSlider("dir-tail-weight", "Tail weight", 0, 1, .06, .01),
            new OpticalToggle("custom-dir-rgb", "Override stock coupler channels", SendsToWorker: false),
            new OpticalSection(null, "", [
                .. Channels("dir-gamma", "Same-layer gamma", 0, 1, (.341, .319, .273), .001),
                new OpticalSlider("dir-r-to-g", "R → G coupling", 0, 1, .353, .001),
                new OpticalSlider("dir-r-to-b", "R → B coupling", 0, 1, .302, .001),
                new OpticalSlider("dir-g-to-r", "G → R coupling", 0, 1, .154, .001),
                new OpticalSlider("dir-g-to-b", "G → B coupling", 0, 1, .353, .001),
                new OpticalSlider("dir-b-to-r", "B → R coupling", 0, 1, .168, .001),
                new OpticalSlider("dir-b-to-g", "B → G coupling", 0, 1, .226, .001),
            ], Requires: new("Enable the stock coupler override to adjust individual channels.", new("custom-dir-rgb"))),
        ]),
        new("optics-color-adaptation", "Color adaptation", [
            new OpticalToggle("color-adaptation", "Color adaptation"),
            new OpticalSection(null, "", [
                new OpticalToggle("color-input-compression", "Input spectral gamut compression", true),
                new OpticalToggle("color-curve-smoothing", "Density curve smoothing", true),
                new OpticalToggle("color-output-lightness-compression", "Output lightness compression", true),
                new OpticalToggle("color-output-chroma-compression", "Output chroma compression", true),
            ], Requires: new("Enable color adaptation to adjust this setting.", new("color-adaptation"))),
        ]),
    ]);

    internal sealed record Binding(OpticalParameter Parameter, bool PrintOnly, OpticalRequirement[] Requirements)
    {
        internal const string PrintOnlyHint = "Available in Print Simulation mode.";

        internal string? UnavailableReason(bool printing, Func<string, bool> isOn, Func<string, string> selected)
        {
            if (PrintOnly && !printing)
                return PrintOnlyHint;
            foreach (var requirement in Requirements)
                if (!requirement.IsMet(isOn, selected))
                    return requirement.Hint;
            return null;
        }

        internal bool ShouldSend(bool printing, Func<string, bool> isOn, Func<string, string> selected) =>
            Parameter is not OpticalToggle { SendsToWorker: false } && UnavailableReason(printing, isOn, selected) is null;
    }

    internal static IReadOnlyDictionary<string, Binding> Parameters { get; } =
        Sections.SelectMany(section => Bindings(section, false, [])).ToDictionary(x => x.Parameter.Key, StringComparer.Ordinal);

    internal static IReadOnlyList<Binding> ConditionalParameters { get; } =
        Parameters.Values.Where(binding => binding.PrintOnly || binding.Requirements.Length > 0).ToArray();

    internal static void ValidateNumber(string key, double value)
    {
        if (!Parameters.TryGetValue(key, out var binding) || binding.Parameter is not OpticalSlider slider || !slider.Contains(value))
            throw new InvalidDataException($"Unsupported saved optical value: {key}.");
    }

    internal static bool HasChoice(string key, string id) => key switch
    {
        "input-color" => Inputs.Any(x => x.Id == id),
        "output-color" => Outputs.Any(x => x.Id == id),
        _ => Parameters.TryGetValue(key, out var binding) && binding.Parameter is OpticalChoice choice
            && choice.Options.Any(option => option.Id == id),
    };

    internal static void VerifyRendererChoices(RendererParameters renderer)
    {
        renderer.VerifyOptions("input-color", Inputs.Select(option => option.Id));
        renderer.VerifyOptions("output-color", Outputs.Select(option => option.Id));
        foreach (var choice in Parameters.Values.Select(binding => binding.Parameter).OfType<OpticalChoice>())
            renderer.VerifyOptions(choice.Key, choice.Options.Select(option => option.Id));
    }

    internal static void VerifyRendererToggles(RendererParameters renderer)
    {
        foreach (var toggle in Parameters.Values.Select(binding => binding.Parameter).OfType<OpticalToggle>())
            if (toggle.SendsToWorker)
                renderer.VerifyBooleanControl(toggle.Key);
    }

    private static IEnumerable<Binding> Bindings(OpticalSection section, bool printOnly, OpticalRequirement[] requirements)
    {
        printOnly |= section.PrintOnly;
        if (section.Requires is { } requirement)
            requirements = [.. requirements, requirement];
        foreach (var item in section.Items)
        {
            if (item is OpticalSection child)
            {
                foreach (var binding in Bindings(child, printOnly, requirements))
                    yield return binding;
            }
            else if (item is OpticalParameter parameter)
                yield return new Binding(parameter, printOnly, requirements);
        }
    }

    private static OpticalSection DiffusionSection(string stage, string label, bool printOnly = false)
    {
        var prefix = stage + "-diffusion-";
        return new OpticalSection("optics-" + stage + "-diffusion", label, [
            new OpticalChoice(prefix + "family", "Filter", Diffusion, "off"),
            new OpticalSection(null, "", [
                new OpticalSlider(prefix + "strength", "Filter strength", 0, 2, .5, .125),
                new OpticalSlider(prefix + "scale", "Spatial scale", 0, 4, 1, .05),
                new OpticalSlider(prefix + "warmth", "Halo warmth", -1.5, 1.5, 0, .05),
                new OpticalSlider(prefix + "core-intensity", "Core intensity", 0, 4, 1, .05),
                new OpticalSlider(prefix + "core-size", "Core size", .1, 4, 1, .05),
                new OpticalSlider(prefix + "halo-intensity", "Halo intensity", 0, 4, 1, .05),
                new OpticalSlider(prefix + "halo-size", "Halo size", .1, 4, 1, .05),
                new OpticalSlider(prefix + "bloom-intensity", "Bloom intensity", 0, 4, 1, .05),
                new OpticalSlider(prefix + "bloom-size", "Bloom size", .1, 4, 1, .05),
            ], Requires: new("Choose a diffusion filter to adjust this setting.", new(prefix + "family", "off", false))),
        ], PrintOnly: printOnly);
    }

    private static OpticalSlider[] Channels(string key, string label, double min, double max,
        (double Red, double Green, double Blue) initial, double step) => [
        new(key + "-r", label + " · R", min, max, initial.Red, step),
        new(key + "-g", label + " · G", min, max, initial.Green, step),
        new(key + "-b", label + " · B", min, max, initial.Blue, step),
    ];

    private static OpticalSlider[] GrainLayers(string key) => [
        new(key + "-0", "Layer 1 scale", .01, 20, 6, .01),
        new(key + "-1", "Layer 2 scale", .01, 20, 1, .01),
        new(key + "-2", "Layer 3 scale", .01, 20, .4, .01),
    ];
}
