using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class NativeRecipeScale
{
    internal static Func<TGeometry, TImage, TRecipe, int, bool, TResult> Bind<TGeometry, TImage, TRecipe, TResult>(Type helpers)
    {
        const string name = "CreateScaleCalcTakingQuickProofIntoAccount";
        var arguments = new[] { typeof(TImage), typeof(TRecipe), typeof(int) };
        var methods = helpers.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == name && method.ReturnType == typeof(TResult))
            .Select(method => (Method: method, Parameters: method.GetParameters()))
            .Where(entry => entry.Parameters.Length is 5 or 6 && entry.Parameters.Skip(1).Take(3)
                .Select(parameter => parameter.ParameterType).SequenceEqual(arguments)).ToArray();
        var legacy = methods.SingleOrDefault(entry => entry.Parameters.Length == 5
            && entry.Parameters[0].ParameterType == typeof(TGeometry) && entry.Parameters[4].ParameterType == typeof(bool)).Method;
        if (legacy is not null)
            return legacy.CreateDelegate<Func<TGeometry, TImage, TRecipe, int, bool, TResult>>();

        var (current, parameters) = methods.SingleOrDefault(entry => entry.Parameters.Length == 6
            && entry.Parameters[0].ParameterType.Name == "IVariantGeometryTransformer"
            && entry.Parameters[4].ParameterType is { IsEnum: true, Name: "ScaleByArea" }
            && entry.Parameters[5].ParameterType.Name == "ICProcessSettings" && entry.Parameters[5].HasDefaultValue);
        if (current is null)
            throw new MissingMethodException(helpers.FullName, name);
        var area = parameters[4].ParameterType;
        var activeCrop = Enum.Parse(area, "ActiveCrop");
        var fullCrop = Enum.Parse(area, "FullCrop");
        var defaultSettings = parameters[5].DefaultValue;
        return (geometry, image, recipe, preview, ignoreCrop) => (TResult)current.Invoke(null,
            new object?[] { geometry, image, recipe, preview, ignoreCrop ? fullCrop : activeCrop, defaultSettings })!;
    }
}

internal sealed class NativeBitmapMetadata<TDestination>
{
    private readonly FieldInfo? author;
    private readonly FieldInfo? sign;
    private readonly FieldInfo? withEdits;

    internal NativeBitmapMetadata()
    {
        var destination = typeof(TDestination);
        author = destination.GetField("c2paAuthor");
        sign = destination.GetField("c2paSign");
        withEdits = destination.GetField("c2paWithEdits");
        if (author is null && sign is null && withEdits is null)
            return;
        if (author is null || !typeof(SafeHandle).IsAssignableFrom(author.FieldType)
            || sign?.FieldType != typeof(bool) || withEdits?.FieldType != typeof(bool))
            throw new InvalidOperationException("Capture One’s bitmap metadata fields have an unsupported shape.");
    }

    internal bool RequiresAuthor => author is not null;

    internal TDestination SetUnsigned(TDestination destination, SafeHandle? emptyAuthor)
    {
        if (author is null)
            return destination;
        ArgumentNullException.ThrowIfNull(emptyAuthor);
        object boxed = destination!;
        author.SetValue(boxed, emptyAuthor);
        sign!.SetValue(boxed, false);
        withEdits!.SetValue(boxed, false);
        return (TDestination)boxed;
    }
}
