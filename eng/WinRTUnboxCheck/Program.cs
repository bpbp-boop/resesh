using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Fails the build when shipped code unboxes a WinRT struct or enum that Native AOT can't unbox.
//
// WinUI hands a boxed value (DependencyObject.GetValue, a resource lookup) back as a WinRT
// IReference<T>. C#/WinRT finds T by name at run time, which under Native AOT only works if
// something roots T for reflection. Otherwise the value arrives as a bare WinRT.IInspectable:
// a cast throws InvalidCastException and `is T` quietly answers false. Nothing warns at build
// time, and the unbox is often in a library (the toolkit's GridSplitter did it on every drag).
//
// This scans the IL of the app and everything it references for unbox, unbox.any and isinst
// of WinRT value types, and requires each such type to be listed with
// [assembly: GeneratedWinRTExposedExternalType(typeof(T))] in the app, which roots it.
//
// Usage: WinRTUnboxCheck <app assembly> <list file to name in errors> <file of reference paths>

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: WinRTUnboxCheck <app.dll> <list file> <reference list>");
    return 2;
}

var appPath = Path.GetFullPath(args[0]);
var listFile = args[1];
var assemblies = File.ReadAllLines(args[2]).Select(p => p.Trim()).Where(p => p.Length > 0)
    .Select(Path.GetFullPath).Prepend(appPath)
    .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();

var readers = new List<(string Path, PEReader Pe, MetadataReader Md)>();
foreach (var path in assemblies)
{
    var pe = new PEReader(File.OpenRead(path));
    if (!pe.HasMetadata)
    {
        pe.Dispose();
        continue;
    }
    readers.Add((path, pe, pe.GetMetadataReader()));
}

// WinRT value types are the projections' structs and enums marked [WindowsRuntimeType].
var winrtValueTypes = new HashSet<string>(StringComparer.Ordinal);
var projections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var (path, _, md) in readers)
{
    foreach (var handle in md.TypeDefinitions)
    {
        var type = md.GetTypeDefinition(handle);
        if (!IsWinRTType(md, type)) continue;
        projections.Add(path);
        if (BaseTypeName(md, type) is "System.ValueType" or "System.Enum")
            winrtValueTypes.Add(FullName(md, handle));
    }
}

var rooted = RootedTypes(readers.Single(r => r.Path == appPath).Md);

// Type name -> places that unbox it.
var unboxes = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
foreach (var (path, pe, md) in readers)
{
    // Projection code marshals its own types; WinRT.Runtime is C#/WinRT itself.
    if (projections.Contains(path) || Path.GetFileName(path).Equals("WinRT.Runtime.dll", StringComparison.OrdinalIgnoreCase))
        continue;

    var assemblyName = Path.GetFileNameWithoutExtension(path);
    foreach (var methodHandle in md.MethodDefinitions)
    {
        var method = md.GetMethodDefinition(methodHandle);
        if (method.RelativeVirtualAddress == 0) continue;
        var declaring = method.GetDeclaringType();

        // The XAML compiler's type info names every type it unboxes with typeof, which roots it.
        if (IsXamlTypeInfo(md, declaring)) continue;

        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
        while (il.RemainingBytes > 0)
        {
            var opcode = ReadOpCode(ref il);
            if (opcode is 0x79 /* unbox */ or 0xA5 /* unbox.any */ or 0x75 /* isinst */)
            {
                var token = MetadataTokens.EntityHandle(il.ReadInt32());
                var name = UnboxedTypeName(md, token);
                if (name is null || !winrtValueTypes.Contains(name) || rooted.Contains(name)) continue;
                if (!unboxes.TryGetValue(name, out var sites)) unboxes[name] = sites = new(StringComparer.Ordinal);
                sites.Add($"{assemblyName}: {FullName(md, declaring)}.{md.GetString(method.Name)}");
            }
            else
            {
                SkipOperand(ref il, opcode);
            }
        }
    }
}

foreach (var reader in readers) reader.Pe.Dispose();

foreach (var (type, sites) in unboxes)
{
    var where = string.Join(", ", sites.Take(3)) + (sites.Count > 3 ? $" and {sites.Count - 3} more" : "");
    // MSBuild turns "<file>: error <code>: <text>" lines into build errors.
    Console.WriteLine(
        $"{listFile}: error RESESH0001: Native AOT can't unbox {type}, which {where} unboxes. " +
        $"Root it here with [assembly: GeneratedWinRTExposedExternalType(typeof({type}))].");
}
return unboxes.Count == 0 ? 0 : 1;

static bool IsWinRTType(MetadataReader md, TypeDefinition type)
{
    foreach (var handle in type.GetCustomAttributes())
    {
        if (AttributeTypeName(md, md.GetCustomAttribute(handle)) == "WinRT.WindowsRuntimeTypeAttribute")
            return true;
    }
    return false;
}

// Types the app roots with [assembly: GeneratedWinRTExposedExternalType(typeof(T))].
static HashSet<string> RootedTypes(MetadataReader md)
{
    var types = new HashSet<string>(StringComparer.Ordinal);
    foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
    {
        var attribute = md.GetCustomAttribute(handle);
        if (AttributeTypeName(md, attribute) != "WinRT.GeneratedWinRTExposedExternalTypeAttribute") continue;
        var blob = md.GetBlobReader(attribute.Value);
        if (blob.ReadUInt16() != 1) continue; // prolog
        var typeName = blob.ReadSerializedString();
        if (typeName is null) continue;
        var comma = typeName.IndexOf(',');
        types.Add(comma < 0 ? typeName : typeName[..comma]);
    }
    return types;
}

static bool IsXamlTypeInfo(MetadataReader md, TypeDefinitionHandle handle)
{
    for (var type = md.GetTypeDefinition(handle); ; )
    {
        if (md.GetString(type.Namespace).EndsWith("_XamlTypeInfo", StringComparison.Ordinal)) return true;
        var outer = type.GetDeclaringType();
        if (outer.IsNil) return false;
        type = md.GetTypeDefinition(outer);
    }
}

// The type an unbox/isinst token names: T itself, or T from Nullable<T>.
static string? UnboxedTypeName(MetadataReader md, EntityHandle token)
{
    if (token.Kind is HandleKind.TypeReference or HandleKind.TypeDefinition) return TypeName(md, token);
    if (token.Kind != HandleKind.TypeSpecification) return null;

    var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)token).Signature);
    if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
    blob.ReadSignatureTypeCode(); // valuetype or class
    if (TypeName(md, blob.ReadTypeHandle()) != "System.Nullable`1") return null;
    if (blob.ReadCompressedInteger() != 1) return null;
    var argument = blob.ReadSignatureTypeCode();
    return argument == SignatureTypeCode.TypeHandle ? TypeName(md, blob.ReadTypeHandle()) : null;
}

static string AttributeTypeName(MetadataReader md, CustomAttribute attribute) =>
    attribute.Constructor.Kind == HandleKind.MemberReference
        ? TypeName(md, md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent) ?? ""
        : FullName(md, md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType());

static string? BaseTypeName(MetadataReader md, TypeDefinition type) =>
    type.BaseType.IsNil ? null : TypeName(md, type.BaseType);

static string? TypeName(MetadataReader md, EntityHandle handle) => handle.Kind switch
{
    HandleKind.TypeDefinition => FullName(md, (TypeDefinitionHandle)handle),
    HandleKind.TypeReference => RefName(md, (TypeReferenceHandle)handle),
    _ => null,
};

static string FullName(MetadataReader md, TypeDefinitionHandle handle)
{
    var type = md.GetTypeDefinition(handle);
    var outer = type.GetDeclaringType();
    return outer.IsNil
        ? Join(md.GetString(type.Namespace), md.GetString(type.Name))
        : FullName(md, outer) + "+" + md.GetString(type.Name);
}

static string RefName(MetadataReader md, TypeReferenceHandle handle)
{
    var type = md.GetTypeReference(handle);
    return type.ResolutionScope.Kind == HandleKind.TypeReference
        ? RefName(md, (TypeReferenceHandle)type.ResolutionScope) + "+" + md.GetString(type.Name)
        : Join(md.GetString(type.Namespace), md.GetString(type.Name));
}

static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

static int ReadOpCode(ref BlobReader il)
{
    int first = il.ReadByte();
    return first == 0xFE ? 0xFE00 | il.ReadByte() : first;
}

// Operand sizes from ECMA-335 partition III.
static void SkipOperand(ref BlobReader il, int opcode)
{
    switch (opcode)
    {
        case 0x45: // switch
            var targets = il.ReadInt32(); // read first: `Offset +=` would use the offset before it
            il.Offset += targets * 4;
            return;
        case 0x21 or 0x23: // ldc.i8, ldc.r8
            il.Offset += 8;
            return;
        case >= 0x0E and <= 0x13: // ldarg.s .. stloc.s
        case 0x1F: // ldc.i4.s
        case >= 0x2B and <= 0x37: // short branches
        case 0xDE: // leave.s
        case 0xFE12 or 0xFE19: // unaligned., no.
            il.Offset += 1;
            return;
        case >= 0xFE09 and <= 0xFE0E: // ldarg .. stloc
            il.Offset += 2;
            return;
        case 0x20 or 0x22: // ldc.i4, ldc.r4
        case 0x27 or 0x28 or 0x29: // jmp, call, calli
        case >= 0x38 and <= 0x44: // branches
        case 0x6F or 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75: // callvirt .. isinst
        case 0x79: // unbox
        case >= 0x7B and <= 0x81: // field ops, stobj
        case 0x8C or 0x8D or 0x8F: // box, newarr, ldelema
        case 0xA3 or 0xA4 or 0xA5: // ldelem, stelem, unbox.any
        case 0xC2 or 0xC6 or 0xD0 or 0xDD: // refanyval, mkrefany, ldtoken, leave
        case 0xFE06 or 0xFE07 or 0xFE15 or 0xFE16 or 0xFE1C: // ldftn, ldvirtftn, initobj, constrained., sizeof
            il.Offset += 4;
            return;
    }
}
