using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

if (args is not [var baseline, var candidate])
{
    Console.Error.WriteLine("Usage: HostReferenceCheck <baseline NativePanel.dll> <candidate NativePanel.dll>");
    return 2;
}

var before = ReadReferences(baseline);
var after = ReadReferences(candidate);
if (before.Assemblies.Count == 0 || after.Assemblies.Count == 0)
    throw new InvalidDataException("Both assemblies must reference Capture One.");
var removed = before.References.Except(after.References).Order(StringComparer.Ordinal).ToArray();
var added = after.References.Except(before.References).Order(StringComparer.Ordinal).ToArray();
var missing = before.Assemblies.Except(after.Assemblies).Order(StringComparer.Ordinal).ToArray();
var newAssemblies = after.Assemblies.Except(before.Assemblies).Order(StringComparer.Ordinal).ToArray();
Console.WriteLine(JsonSerializer.Serialize(new
{
    BaselineAssemblyVersions = before.Versions,
    CandidateAssemblyVersions = after.Versions,
    BaselineReferences = before.References.Count,
    CandidateReferences = after.References.Count,
    BaselineOnlyAssemblies = missing,
    CandidateOnlyAssemblies = newAssemblies,
    BaselineOnlyReferences = removed,
    CandidateOnlyReferences = added,
}, new JsonSerializerOptions { WriteIndented = true }));
return removed.Length + added.Length + missing.Length + newAssemblies.Length == 0 ? 0 : 1;

static (HashSet<string> Assemblies, SortedDictionary<string, string> Versions, HashSet<string> References) ReadReferences(string assemblyPath)
{
    using var input = File.OpenRead(assemblyPath);
    using var image = new PEReader(input);
    var metadata = image.GetMetadataReader();
    var names = new SignatureNames();
    var hostNames = new HashSet<string>(StringComparer.Ordinal);
    var assemblies = new HashSet<string>(StringComparer.Ordinal);
    var versions = new SortedDictionary<string, string>(StringComparer.Ordinal);
    var references = new HashSet<string>(StringComparer.Ordinal);
    foreach (var handle in metadata.AssemblyReferences)
    {
        var reference = metadata.GetAssemblyReference(handle);
        var name = metadata.GetString(reference.Name);
        if (name != "CaptureOne" && !name.StartsWith("P1.", StringComparison.Ordinal))
            continue;
        hostNames.Add(name);
        assemblies.Add($"{name} [{metadata.GetString(reference.Culture)}:{Convert.ToHexString(metadata.GetBlobBytes(reference.PublicKeyOrToken))}]");
        versions.Add(name, reference.Version.ToString());
    }
    void AddIfHost(string symbol)
    {
        if (hostNames.Any(name => symbol.Contains($"[{name}]", StringComparison.Ordinal)))
            references.Add(symbol);
    }
    foreach (var handle in metadata.TypeReferences)
        AddIfHost(names.GetTypeFromReference(metadata, handle, 0));
    foreach (var handle in metadata.MemberReferences)
        AddIfHost(names.GetMember(metadata, handle));
    for (var row = 1; row <= metadata.GetTableRowCount(TableIndex.TypeSpec); row++)
        AddIfHost(names.GetTypeFromSpecification(metadata, null, MetadataTokens.TypeSpecificationHandle(row), 0));
    for (var row = 1; row <= metadata.GetTableRowCount(TableIndex.MethodSpec); row++)
    {
        var generic = metadata.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(row));
        var method = generic.Method.Kind switch
        {
            HandleKind.MemberReference => names.GetMember(metadata, (MemberReferenceHandle)generic.Method),
            HandleKind.MethodDefinition => names.GetMethod(metadata, (MethodDefinitionHandle)generic.Method),
            _ => throw new BadImageFormatException("Unexpected generic method reference."),
        };
        AddIfHost(method + "<" + string.Join(",", generic.DecodeSignature(names, null)) + ">");
    }
    return (assemblies, versions, references);
}

internal sealed class SignatureNames : ISignatureTypeProvider<string, object?>
{
    internal string GetMember(MetadataReader reader, MemberReferenceHandle handle)
    {
        var member = reader.GetMemberReference(handle);
        var signature = member.GetKind() == MemberReferenceKind.Field ? member.DecodeFieldSignature(this, null)
            : Format(member.DecodeMethodSignature(this, null));
        return $"{GetOwner(reader, member.Parent)}.{reader.GetString(member.Name)} {signature}";
    }

    internal string GetMethod(MetadataReader reader, MethodDefinitionHandle handle)
    {
        var method = reader.GetMethodDefinition(handle);
        return $"{GetTypeFromDefinition(reader, method.GetDeclaringType(), 0)}.{reader.GetString(method.Name)} " +
            Format(method.DecodeSignature(this, null));
    }

    public static string Format(MethodSignature<string> signature) =>
        $"{signature.Header.RawValue:X2}:{signature.GenericParameterCount}:{signature.RequiredParameterCount} " +
        $"{signature.ReturnType}({string.Join(",", signature.ParameterTypes)})";

    private string GetOwner(MetadataReader reader, EntityHandle owner) => owner.Kind switch
    {
        HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)owner, 0),
        HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)owner, 0),
        HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)owner, 0),
        _ => owner.Kind.ToString(),
    };

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        var scope = type.ResolutionScope;
        if (scope.Kind == HandleKind.TypeReference)
            return Kind(rawTypeKind) + GetTypeFromReference(reader, (TypeReferenceHandle)scope, 0) + "+" + name;
        var prefix = scope.Kind == HandleKind.AssemblyReference
            ? $"[{reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)}]" : "";
        return Kind(rawTypeKind) + prefix + Qualify(reader.GetString(type.Namespace), name);
    }

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var parent = type.GetDeclaringType();
        return Kind(rawTypeKind) + (parent.IsNil ? Qualify(reader.GetString(type.Namespace), name)
            : GetTypeFromDefinition(reader, parent, 0) + "+" + name);
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, context);

    private static string Qualify(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
    private static string Kind(byte kind) => (SignatureTypeKind)kind switch
    {
        SignatureTypeKind.Class => "class ",
        SignatureTypeKind.ValueType => "valuetype ",
        _ => "",
    };
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape) =>
        $"{elementType}[rank={shape.Rank};sizes={string.Join(",", shape.Sizes)};lower={string.Join(",", shape.LowerBounds)}]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPinnedType(string elementType) => elementType + " pinned";
    public string GetGenericTypeParameter(object? context, int index) => "!" + index;
    public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        $"{genericType}<{string.Join(",", typeArguments)}>";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
        $"{unmodifiedType} mod{(isRequired ? "req" : "opt")}({modifier})";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr " + Format(signature);
}
