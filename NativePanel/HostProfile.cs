using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using P1.C1.Core;
using P1.C1.Core.Interfaces;
using P1.C1.Core.ModelEntities;
using P1.ModelCoreWrapper;

internal static class HostProfile
{
    internal sealed record Target(Guid VariantId, string SourcePath)
    {
        internal string SourceFormat => Path.GetExtension(SourcePath).TrimStart('.').ToUpperInvariant();
    }

    internal static Target CaptureTarget(object window) => TargetFor(ReadSelection(window).Variant);

    internal static Target TargetFor(ICoreVariant variant)
    {
        var image = variant.CoreImage ?? throw new InvalidOperationException("The selected variant has no source image.");
        return new Target(variant.UUID, image.FullpathFilename);
    }

    internal static (object Variant, Type Type, Guid Id) SelectedVariant(object window)
    {
        var selection = ReadSelection(window);
        return (selection.Variant, typeof(ICoreVariant), selection.Variant.UUID);
    }

    internal static Selection ReadSelection(object window)
    {
        if (window is not Control { IsDisposed: false, IsHandleCreated: true, InvokeRequired: false }
            || HostDocumentAccess.Current() is not { } manager
            || manager.ActiveDocument() is not { } document
            || !ApprovedCatalog.IsApproved(manager.ActivePath(), document.Path)
            || !ReferenceEquals(HostDocumentAccess.MainForm(document), window))
            throw new InvalidOperationException("Activate the catalog selected by the Spektrafilm launcher.");

        if (((ISelectedCollectionsProvider)document).PrimarySelectedCollection is not ISimpleCollection collection
            || collection.PrimaryVariant is not ICoreVariant variant)
            throw new InvalidOperationException("Select an image in the Viewer first.");
        return new Selection(manager.Instance, document, variant);
    }

    internal static Guid[] CaptureSelectedVariants(object window, ICaptureOneDocument expectedDocument) =>
        CaptureSelectionSet(window, expectedDocument).Select(item => item.Variant.UUID).ToArray();

    internal static Selection[] CaptureSelectionSet(object window, ICaptureOneDocument expectedDocument)
    {
        var active = ReadSelection(window);
        if (!ReferenceEquals(active.Document, expectedDocument)
            || ((ISelectedCollectionsProvider)active.Document).PrimarySelectedCollectionSelection is not { } selection
            || selection.PrimaryVariant?.UUID != active.Variant.UUID || selection.SelectedVariants is not { } selected)
            throw new InvalidOperationException("Capture One’s selection changed. Select the variants again.");

        var variants = selected.OfType<ICoreVariant>().DistinctBy(variant => variant.UUID).Take(VariantLookBatch.MaxVariants + 1).ToArray();
        if (variants.Length == 0 || selected.Any(variant => variant is not ICoreVariant)
            || variants.Any(variant => variant.UUID == Guid.Empty) || !variants.Any(variant => variant.UUID == active.Variant.UUID))
            throw new InvalidOperationException("Capture One did not return a complete variant selection.");
        if (variants.Length > VariantLookBatch.MaxVariants)
            throw new InvalidOperationException($"Select up to {VariantLookBatch.MaxVariants:N0} variants at a time.");
        return variants.Select(variant => active with { Variant = variant }).ToArray();
    }

    internal readonly record struct Selection(object Manager, ICaptureOneDocument Document, ICoreVariant Variant);
}
