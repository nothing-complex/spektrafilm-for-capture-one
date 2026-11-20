using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using P1.C1.Components.CommandsLogic;
using P1.C1.Core;
using P1.C1.Core.Interfaces;
using P1.C1.Core.ModelEntities;
using P1.C1.Core.Selection;
using P1.C1.ViewModels.Commands;
using Forms = System.Windows.Forms;

internal sealed class NativeVariantCloneHook : IDisposable
{
    private readonly Forms.Form window;
    private readonly ICaptureOneDocument document;
    private readonly Func<Guid, VariantLook?> captureLook;
    private readonly Action<Guid, Guid, VariantLook> saveLook;
    private readonly Action<string> report;
    private readonly ExportHook.CommandBinding binding;
    private bool running;
    private bool disposed;

    internal NativeVariantCloneHook(Forms.Form window, ICaptureOneDocument document, Func<Guid, VariantLook?> captureLook,
        Action<Guid, Guid, VariantLook> saveLook, Action<string> report)
    {
        this.window = window;
        this.document = document;
        this.captureLook = captureLook;
        this.saveLook = saveLook;
        this.report = report;
        var commands = window.GetType().GetField("mCommands", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) as Commands
            ?? throw new InvalidOperationException("Capture One’s variant commands are unavailable.");
        binding = new ExportHook.CommandBinding(commands.CloneVariant, window, "OnCloneVariantOnOnExecuteMultiple", Clone);
    }

    private void Clone(Command command, bool editMultiple)
    {
        if (disposed || running)
        {
            binding.Original(command, editMultiple);
            return;
        }
        running = true;
        try
        {
            var snapshots = new List<CloneSource>();
            bool incomplete = false;
            try { CaptureSources(editMultiple, snapshots, ref incomplete); }
            catch (Exception error)
            {
                incomplete = true;
                StartupHook.Log("Could not capture a source look before native Clone: " + error.GetBaseException().Message);
            }

            // The verified command creates native clones and selects them
            // synchronously. Preserve its result and exception semantics.
            binding.Original(command, editMultiple);
            if (disposed || snapshots.Count == 0 && !incomplete)
                return;
            try
            {
                var active = HostProfile.ReadSelection(window);
                if (!ReferenceEquals(active.Document, document))
                    return;
                var selected = ActiveSelection?.SelectedVariants?.Select(item => item.UUID).ToHashSet() ?? [];
                int inherited = 0;
                foreach (var source in snapshots)
                {
                    try
                    {
                        var added = source.Image.CoreVariants.Where(variant => !source.Existing.Contains(variant.UUID)).Take(2).ToArray();
                        if (added.Length != 1 || added[0].UUID == Guid.Empty || !selected.Contains(added[0].UUID)
                            || added[0].CoreImage?.UUID != source.Image.UUID)
                        {
                            incomplete = true;
                            continue;
                        }
                        saveLook(added[0].UUID, source.Image.UUID, source.Look);
                        inherited++;
                    }
                    catch (Exception error)
                    {
                        incomplete = true;
                        StartupHook.Log("Could not persist a native clone’s Spektrafilm look: " + error.GetBaseException().Message);
                    }
                }
                if (inherited > 0)
                    StartupHook.Log("clone.looks.inherited.v1", new { Count = inherited });
                if (incomplete)
                    report("Some clones could not inherit their Spektrafilm look. Use Copy selected settings and Apply copied settings.");
            }
            catch (Exception error)
            {
                StartupHook.Log("Could not verify native clone lineage: " + error.GetBaseException().Message);
            }
        }
        finally { running = false; }
    }

    private ISelection? ActiveSelection => ((ISelectedCollectionsProvider)document).PrimarySelectedCollectionSelection;

    private void CaptureSources(bool editMultiple, List<CloneSource> snapshots, ref bool incomplete)
    {
        if (!ReferenceEquals(HostProfile.ReadSelection(window).Document, document))
            return;
        var affected = SelectionHelper.GetAffectedVariants(document, editMultiple, false)
            .OfType<ICoreVariant>().Take(VariantLookBatch.MaxVariants + 1).ToArray();
        if (affected.Length > VariantLookBatch.MaxVariants)
        {
            incomplete = true;
            return;
        }
        foreach (var group in affected.Where(variant => variant.CoreImage is not null).GroupBy(variant => variant.CoreImage!.UUID))
        {
            // Distinct RAWs identify their own clone. Two source siblings of
            // one RAW require an explicit host-provided source-to-clone pair.
            var sources = group.Take(2).ToArray();
            if (sources.Length != 1 || group.Key == Guid.Empty)
            {
                incomplete = true;
                continue;
            }
            var source = sources[0];
            try
            {
                if (captureLook(source.UUID) is not { } look || source.CoreImage is not { } image)
                    continue;
                var existing = image.CoreVariants.Select(variant => variant.UUID).ToHashSet();
                if (source.UUID == Guid.Empty || !existing.Contains(source.UUID))
                    incomplete = true;
                else
                    snapshots.Add(new(image, existing, look));
            }
            catch (Exception error)
            {
                incomplete = true;
                StartupHook.Log("Could not read a clone’s source look: " + error.GetBaseException().Message);
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        binding.Dispose();
    }

    private sealed record CloneSource(ICoreImage Image, HashSet<Guid> Existing, VariantLook Look);
}
