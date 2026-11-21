using System;
using System.Collections.Generic;
using System.Linq;

internal sealed record FilmSlider(string StateKey, string? WorkerKey, string Label, double Minimum, double Maximum,
    double Initial, double Increment, string Unit = "", double DisplayScale = 1)
{
    internal bool Contains(double value) => double.IsFinite(value) && value >= Minimum && value <= Maximum;
}

internal static class FilmControls
{
    internal static readonly FilmSlider Exposure = new("Exposure", "film-exposure", "Film exposure", -8, 8, 0, .1, "EV");
    internal static readonly FilmSlider PrintExposure = new("PrintExposure", "print-exposure", "Print exposure", -5, 5, 0, .1, "EV");
    internal static readonly FilmSlider Strength = new("Strength", null, "Look strength", 0, 100, 100, 1, "%");

    internal static IReadOnlyList<FilmSlider> Negative { get; } = Array.AsReadOnly<FilmSlider>([
        new("film_gamma", "film-gamma", "Negative gamma", .1, 2, 1, .05),
        new("film_push_pull", "film-push-pull", "Film push / pull", -2, 2, 0, .1, "EV"),
        new("negative_bleach_bypass", "negative-bleach-bypass", "Negative bleach bypass", 0, 1, 0, .01, "%", 100),
        new("negative_leuco_cyan", "negative-leuco-cyan", "Negative leuco cyan", 0, 2, 1, .05),
        new("uv_cutoff", "uv-cutoff", "UV cutoff", 380, 450, 380, 1, "nm"),
        new("ir_cutoff", "ir-cutoff", "IR cutoff", 600, 780, 780, 1, "nm"),
    ]);

    internal static IReadOnlyList<FilmSlider> Printing { get; } = Array.AsReadOnly<FilmSlider>([
        new("print_gamma", "print-gamma", "Print gamma", .1, 2, 1, .05),
        new("print_push_pull", "print-push-pull", "Print push / pull", -2, 2, 0, .1, "EV"),
        new("print_shadows", "print-shadow-shape", "Print shadow shape", -1, 1, 0, .05),
        new("print_highlights", "print-highlight-shape", "Print highlight shape", -1, 1, 0, .05),
        new("print_exhaustion", null, "Developer exhaustion", 0, 1, 0, .01, "%", 100),
        new("print_bleach_bypass", "print-bleach-bypass", "Print bleach bypass", 0, 1, 0, .01, "%", 100),
        new("cyan_filter", "cyan-filter", "Cyan filtration", 0, 120, 0, 1, "CC"),
        new("magenta_filter", "magenta-filter", "Magenta filter shift", -60, 60, 0, 1, "CC"),
        new("yellow_filter", "yellow-filter", "Yellow filter shift", -60, 60, 0, 1, "CC"),
        new("preflash_exposure", "preflash", "Preflash exposure", 0, 1, 0, .01),
        new("preflash_magenta", "preflash-magenta", "Preflash magenta", -60, 60, 0, 1, "CC"),
        new("preflash_yellow", "preflash-yellow", "Preflash yellow", -60, 60, 0, 1, "CC"),
    ]);

    internal static IReadOnlyList<FilmSlider> Couplers { get; } = Array.AsReadOnly<FilmSlider>([
        new("dir_amount", "dir-amount", "DIR coupler amount", 0, 2, 1, .05),
        new("dir_same_layer", "dir-same-layer", "Same-layer inhibition", 0, 2, 1, .05),
        new("dir_interlayer", "dir-interlayer", "Interlayer inhibition", 0, 2, 1, .05),
    ]);

    internal static IReadOnlyDictionary<string, FilmSlider> Development { get; } =
        Negative.Concat(Printing).Concat(Couplers).ToDictionary(item => item.StateKey, StringComparer.Ordinal);

    internal static IReadOnlyList<(string StateKey, string WorkerKey)> DevelopmentRendererParameters { get; } =
        Array.AsReadOnly(Development.Values.Where(item => item.WorkerKey is not null)
            .Select(item => (item.StateKey, item.WorkerKey!)).ToArray());

    internal static void AppendRendererOptions(LookSettings look, bool printing, List<string> arguments)
    {
        if (printing && look.Development.GetValueOrDefault("print_exhaustion") != 0)
            throw new InvalidOperationException("Set Developer exhaustion to zero before rendering Print Simulation.");

        AddNumber(Exposure.WorkerKey!, look.Exposure);
        if (printing)
            AddNumber(PrintExposure.WorkerKey!, look.PrintExposure);
        AddDevelopment(Negative);
        if (printing)
            AddDevelopment(Printing);
        AddDevelopment(Couplers);
        AddFilter("uv_cutoff", "uv-enabled");
        AddFilter("ir_cutoff", "ir-enabled");

        void AddDevelopment(IReadOnlyList<FilmSlider> definitions)
        {
            foreach (var definition in definitions)
                if (definition.WorkerKey is { } key && look.Development.TryGetValue(definition.StateKey, out var value))
                    AddNumber(key, value);
        }

        void AddFilter(string stateKey, string workerKey)
        {
            var initial = Development[stateKey].Initial;
            Add(workerKey, look.Development.GetValueOrDefault(stateKey, initial) != initial ? "true" : "false");
        }

        void AddNumber(string key, double value) => Add(key, RendererParameters.NumberToken(value));

        void Add(string key, string value)
        {
            arguments.Add("--" + key);
            arguments.Add(value);
        }
    }
}
