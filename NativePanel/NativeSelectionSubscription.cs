using System;
using System.Reflection;
using P1.C1.Core.ModelEntities;
using P1.C1.Framework.Messaging;

internal static class NativeSelectionSubscription
{
    private static readonly Lazy<Func<IMessageBus, Action<ISimpleCollection>, IDisposable>?> Bind = new(Find);

    internal static IDisposable? Attach(IMessageBus bus, Action<ISimpleCollection> select) => Bind.Value?.Invoke(bus, select);

    private static Func<IMessageBus, Action<ISimpleCollection>, IDisposable>? Find()
    {
        const string prefix = "P1.C1.Core.Messaging.ModelMessages.";
        var core = typeof(ICaptureOneDocument).Assembly;
        var message = core.GetType(prefix + "PrimaryVariantChangedMessage") ?? core.GetType(prefix + "PrimaryVariantChangingMessage");
        if (message is null)
            return null;
        return typeof(NativeSelectionSubscription).GetMethod(nameof(Subscribe), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(message).CreateDelegate<Func<IMessageBus, Action<ISimpleCollection>, IDisposable>>();
    }

    private static IDisposable Subscribe<T>(IMessageBus bus, Action<ISimpleCollection> select)
    {
        var getter = typeof(T).GetProperty("Collection")?.GetMethod
            ?? throw new MissingMemberException(typeof(T).FullName, "Collection");
        var listener = new Handler<T>(bus, select, getter.CreateDelegate<Func<T, ISimpleCollection>>());
        bus.Subscribe<T>(listener);
        return listener;
    }

    private sealed class Handler<T>(IMessageBus bus, Action<ISimpleCollection> select, Func<T, ISimpleCollection> collection) :
        IHandleMessage<T>, IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public void Handle(T message) => select(collection(message));
        public void Dispose() => bus.Unsubscribe<T>(this);
    }
}
