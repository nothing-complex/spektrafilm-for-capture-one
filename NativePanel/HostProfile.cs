using System;
using System.IO;
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

    internal static Target CaptureTarget(object window)
    {
        var variant = ReadSelection(window).Variant;
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

    internal readonly record struct Selection(object Manager, ICaptureOneDocument Document, ICoreVariant Variant);
}
