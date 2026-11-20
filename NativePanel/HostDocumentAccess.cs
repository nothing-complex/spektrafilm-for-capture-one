using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using P1.C1.Core.ModelEntities;
using P1.C1.Controls.Inspector.WPF;
using P1.C1.ViewModels.Controls.Inspector;
using P1.C1.ViewModels.DocumentManagement;
using Forms = System.Windows.Forms;

internal static class HostDocumentAccess
{
    private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
    private static readonly Type Program = Assembly.Load("CaptureOne").GetType("P1.C1.Program", throwOnError: true)!;
    private static readonly Func<object?> ReadManager = Getter(Program, "DocumentManager", PublicStatic).CreateDelegate<Func<object?>>();
    private static readonly Func<ICaptureOneDocument, Forms.Form?> ReadMainForm =
        (Program.GetMethod("GetMainForm", PublicStatic, null, new[] { typeof(ICaptureOneDocument) }, null)
            ?? throw new MissingMethodException(Program.FullName, "GetMainForm"))
        .CreateDelegate<Func<ICaptureOneDocument, Forms.Form?>>();
    private static readonly ConditionalWeakTable<Forms.Form, Func<InspectorPaletteViewModel?>> Palettes = new();
    private static Manager? cachedManager;

    // These host properties are read on the owning UI thread.
    internal static Manager? Current()
    {
        if (ReadManager() is not { } instance)
        {
            cachedManager = null;
            return null;
        }
        if (cachedManager is not { } cached || !ReferenceEquals(cached.Instance, instance))
            cachedManager = cached = new Manager(instance);
        return cached;
    }

    internal static Forms.Form? MainForm(ICaptureOneDocument? document) =>
        document is null ? null : ReadMainForm(document);

    internal static InspectorPaletteViewModel? Palette(Forms.Form? form) =>
        form is { IsHandleCreated: true, IsDisposed: false, Disposing: false }
        ? Palettes.GetValue(form, static owner => Getter(owner.GetType(), "InspectorPaletteViewModel")
            .CreateDelegate<Func<InspectorPaletteViewModel?>>(owner))()
        : null;

    private static MethodInfo Getter(Type type, string name, BindingFlags flags = BindingFlags.Instance | BindingFlags.Public) =>
        type.GetProperty(name, flags)?.GetMethod ?? throw new MissingMemberException(type.FullName, name);

    internal sealed class Manager(object instance)
    {
        internal object Instance { get; } = instance;
        internal Func<ICaptureOneDocument?> ActiveDocument { get; } =
            Getter(instance.GetType(), "ActiveDocument").CreateDelegate<Func<ICaptureOneDocument?>>(instance);
        internal Func<string?> ActivePath { get; } = Getter(instance.GetType(), "ActiveDocumentPath").CreateDelegate<Func<string?>>(instance);

        internal IDisposable? ObserveChanges(Action changed)
        {
            var eventInfo = Instance.GetType().GetEvent("ActiveDocumentChanged", BindingFlags.Instance | BindingFlags.Public);
            if (eventInfo?.EventHandlerType != typeof(EventHandler<DocumentManagerEventArgs>)) return null;
            EventHandler<DocumentManagerEventArgs> handler = (_, _) => changed();
            eventInfo.AddEventHandler(Instance, handler);
            return new DocumentSubscription(Instance, eventInfo, handler);
        }
    }

    private sealed class DocumentSubscription(object manager, EventInfo change, Delegate handler) : IDisposable
    {
        public void Dispose() => change.RemoveEventHandler(manager, handler);
    }
}
