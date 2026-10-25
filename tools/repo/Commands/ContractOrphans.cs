using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace RimGovernor.Tools.Workflow.Commands;

/// <summary>
/// The orphan gate over the brain's records: every member of a wire record in <c>RimGovernor.Brain.Contracts</c> and of
/// a record in <c>RimGovernor.Brain.Policy</c> must be read outside its declaration, and an observation (any wire record outside <c>Operations</c>)
/// the native side writes must be read by the brain. It reads the built assemblies, so a reference is the member token the compiler
/// bound (a getter call, an initializer's setter, a positional constructor), never a text match. A reference into the
/// declaring assembly is followed through that assembly's own calls to the backing fields it reaches: a computed
/// property reads what it computes from, a positional constructor writes its parameters, a Policy function reads the
/// records it decides over. Constructing a type reaches its virtual methods (overrides, interface implementations,
/// iterator bodies), which the runtime calls without a token. Record plumbing (equality, hashing, printing, cloning)
/// and the JSON serializer touch every member and count as nothing. The fixture contracts
/// (<c>RimGovernor.Fixtures.Contracts</c>) are checked the same way with the acceptance cases as their readers
/// (<see cref="Fixtures"/>). See docs/developers/architecture/rules.md.
/// </summary>
internal static class ContractOrphans
{
    /// <summary>The wire records: the public types of the wire namespace, enums aside.</summary>
    public static readonly Target Wire = new("Contracts", "RimGovernor.Contracts.Wire", RecordsOnly: false, IntentNamespaces: ["Operations"]);

    /// <summary>Policy's records, of any visibility: IDE0051/IDE0052 see only private members.</summary>
    public static readonly Target PolicyRecords = new("Policy", "RimGovernor.Brain.Policy", RecordsOnly: true);

    /// <summary>
    /// The fixture stage spec and its reply: the public types of <c>RimGovernor.Fixtures.Contracts</c>, enums aside. Their
    /// other end is the fixture build's <c>test/stage</c>, which the gate never builds, so the acceptance cases and their
    /// harness are the readers: a case setting a spec member or reading a reply member uses it.
    /// </summary>
    public static readonly Target Fixtures = new("Fixtures", "RimGovernor.Fixtures.Contracts", RecordsOnly: false, ReferenceIsUse: true);

    /// <summary>Compiler-generated record members that touch every field without being a consumer's read.</summary>
    private static readonly HashSet<string> Plumbing = new(StringComparer.Ordinal)
    {
        "Equals", "GetHashCode", "ToString", "PrintMembers", "op_Equality", "op_Inequality", "<Clone>$", "get_EqualityContract",
    };

    /// <summary>The JSON machinery's base types: the source-generated context and the hand-written converters.</summary>
    private static readonly string[] SerializerBases =
    [
        "System.Text.Json.Serialization.JsonSerializerContext", "System.Text.Json.Serialization.JsonConverter",
        "System.Text.Json.Serialization.JsonConverter`1", "System.Text.Json.Serialization.JsonConverterFactory",
    ];

    private static readonly Dictionary<ushort, OperandType> Operands = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => unchecked((ushort)o.Value), o => o.OperandType);

    /// <summary>
    /// Which members of an assembly are checked. <paramref name="Name"/> prefixes every finding; members are the
    /// public types under <paramref name="Namespace"/> or, with <paramref name="RecordsOnly"/>, its records. With
    /// <paramref name="ReferenceIsUse"/>, a consumer's write uses a member as a read does.
    /// A member under one of <paramref name="IntentNamespaces"/> (sub-namespaces of the target, such as <c>Operations</c>) is a
    /// brain-to-native intent record: the native side reads it, and may construct its own copy to compare against, so
    /// "written by the native side, never read by the brain" applies only to the rest, the native-to-brain observations.
    /// <paramref name="IntentRecords"/> applies that direction to the whole target assembly when orders have their own project.
    /// </summary>
    internal sealed record Target(string Name, string Namespace, bool RecordsOnly, bool ReferenceIsUse = false,
        IReadOnlyList<string>? IntentNamespaces = null, bool IntentRecords = false)
    {
        public bool IsIntent(string member) =>
            IntentRecords || IntentNamespaces?.Any(n => member.StartsWith(n + ".", StringComparison.Ordinal)) == true;
    }

    /// <summary>How the brain and the native side use one member.</summary>
    private sealed class Use
    {
        public bool BrainRead { get; set; }

        public bool BrainWrite { get; set; }

        public bool NativeRead { get; set; }

        public bool NativeWrite { get; set; }
    }

    /// <summary>
    /// The orphans of <paramref name="target"/> declared in <paramref name="declaring"/>, sorted, each a
    /// <c>Name|Type.Member</c> key and its reason. <paramref name="checkedCount"/> is how many members it declares.
    /// The consumers are every other brain assembly and the native mod's own.
    /// </summary>
    public static IReadOnlyList<(string Key, string Reason)> Find(
        Target target, string declaring, IEnumerable<string> brain, IEnumerable<string> native, out int checkedCount)
    {
        var model = new Declared(target, declaring);
        var uses = model.Members.ToDictionary(m => m, _ => new Use(), StringComparer.Ordinal);
        foreach (var (file, isNative) in brain.Select(f => (f, false)).Concat(native.Select(f => (f, true))))
        {
            foreach (var (member, write) in model.Effects(Consumer.Roots(file, model.AssemblyName)))
            {
                var use = uses[member];
                if (isNative)
                {
                    use.NativeRead |= !write;
                    use.NativeWrite |= write;
                }
                else
                {
                    use.BrainRead |= !write;
                    use.BrainWrite |= write;
                }
            }
        }
        checkedCount = uses.Count;
        return [.. uses
            .Select(u => (Key: $"{target.Name}|{u.Key}", Reason: target.ReferenceIsUse && u.Value.BrainWrite ? null : Verdict(u.Value, target.IsIntent(u.Key))))
            .Where(u => u.Reason is not null)
            .Select(u => (u.Key, u.Reason!))
            .OrderBy(u => u.Key, StringComparer.Ordinal)];
    }

    // A member the brain writes and the native side reads is a request field in use, even outside the intent
    // namespaces and when the native side builds its own request too (a frame's scope).
    private static string? Verdict(Use use, bool intent) =>
        use.BrainWrite && use.NativeRead ? null
        : use.NativeWrite && !use.BrainRead && !intent ? "written by the native side, never read by the brain"
        : use.BrainRead || use.NativeRead ? null
        : use.BrainWrite || use.NativeWrite ? "written, never read"
        : "not referenced outside its declaration";

    /// <summary>A method a consumer calls (<paramref name="New"/> when it constructs) or a field it touches.</summary>
    private readonly record struct Root(string Key, bool IsField, bool Write, bool New);

    /// <summary>The declaring assembly: its members, and what each of its methods reads, writes and calls.</summary>
    private sealed class Declared
    {
        private const string Constructed = "new:";
        private const string Initialized = "cctor:";

        private readonly Dictionary<string, string> fieldMembers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Body> bodies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<(string Member, bool Write)>> reached = new(StringComparer.Ordinal);

        private sealed record Body(List<(string Member, bool Write)> Fields, List<string> Calls);

        public Declared(Target target, string path)
        {
            using var pe = new PEReader(File.OpenRead(path));
            var md = pe.GetMetadataReader();
            AssemblyName = md.GetString(md.GetAssemblyDefinition().Name);
            var names = new Names(md, null);
            var members = new SortedSet<string>(StringComparer.Ordinal);
            var accessorMembers = new Dictionary<MethodDefinitionHandle, (string Member, bool Write)>();
            var serializer = md.TypeDefinitions.Where(t => DerivesFromSerializer(md, names, t)).ToHashSet();
            foreach (var handle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(handle);
                var full = names.TypeDef(handle);
                if (!full.StartsWith(target.Namespace + ".", StringComparison.Ordinal) || serializer.Contains(handle)
                    || (target.RecordsOnly ? !IsRecord(md, type) : !IsPublic(md, handle) || IsEnum(names, type)))
                {
                    continue;
                }
                var shown = full[(target.Namespace.Length + 1)..].Replace('/', '.');
                foreach (var p in type.GetProperties())
                {
                    var property = md.GetPropertyDefinition(p);
                    var name = md.GetString(property.Name);
                    if (name == "EqualityContract")
                    {
                        continue;
                    }
                    members.Add($"{shown}.{name}");
                    // An accessor touches its own property, which matters for one computed from others (no backing field).
                    var accessors = property.GetAccessors();
                    if (!accessors.Getter.IsNil)
                    {
                        accessorMembers[accessors.Getter] = ($"{shown}.{name}", false);
                    }
                    if (!accessors.Setter.IsNil)
                    {
                        accessorMembers[accessors.Setter] = ($"{shown}.{name}", true);
                    }
                }
                foreach (var f in type.GetFields())
                {
                    var field = md.GetFieldDefinition(f);
                    var name = md.GetString(field.Name);
                    var key = $"{full}::{name}";
                    if (name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                    {
                        var property = $"{shown}.{name[1..name.IndexOf('>', StringComparison.Ordinal)]}";
                        if (members.Contains(property))
                        {
                            fieldMembers[key] = property;
                        }
                    }
                    else if ((field.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public
                        && (field.Attributes & (FieldAttributes.Literal | FieldAttributes.Static)) == 0)
                    {
                        members.Add($"{shown}.{name}");
                        fieldMembers[key] = $"{shown}.{name}";
                    }
                }
            }
            Members = members;
            foreach (var handle in md.TypeDefinitions)
            {
                if (serializer.Contains(handle))
                {
                    continue;
                }
                var type = md.GetTypeDefinition(handle);
                var typeKey = names.TypeDef(handle);
                // What constructing the type reaches: its virtual methods and its base's, and its static constructor.
                var constructed = new Body([], []);
                if (!type.BaseType.IsNil && type.BaseType.Kind == HandleKind.TypeDefinition)
                {
                    constructed.Calls.Add(Constructed + names.TypeDef((TypeDefinitionHandle)type.BaseType));
                }
                bodies[Constructed + typeKey] = constructed;
                // Touching a static member runs the type's static constructor, whose field initializers hold the lambdas
                // (a Lazy factory, a table of delegates) that no call token reaches.
                var initializer = new Body([], []);
                bodies[Initialized + typeKey] = initializer;
                foreach (var m in type.GetMethods())
                {
                    var method = md.GetMethodDefinition(m);
                    var key = names.MethodDef(m);
                    if (IsPlumbing(md, names, handle, method))
                    {
                        continue;
                    }
                    // An abstract accessor still names a real property read by its callers.
                    if (method.RelativeVirtualAddress == 0)
                    {
                        if (accessorMembers.TryGetValue(m, out var abstractAccessor))
                        {
                            bodies[key] = new Body([abstractAccessor], []);
                        }
                        continue;
                    }
                    if ((method.Attributes & MethodAttributes.Virtual) != 0 || md.GetString(method.Name) == ".cctor")
                    {
                        constructed.Calls.Add(key);
                    }
                    if (md.GetString(method.Name) == ".cctor")
                    {
                        initializer.Calls.Add(key);
                    }
                    var body = new Body([], []);
                    if (accessorMembers.TryGetValue(m, out var own))
                    {
                        body.Fields.Add(own);
                    }
                    foreach (var (op, token) in Il.Tokens(pe.GetMethodBody(method.RelativeVirtualAddress)))
                    {
                        if (Il.IsField(op))
                        {
                            if (names.Field(token) is { } field && fieldMembers.TryGetValue(field, out var member))
                            {
                                body.Fields.Add((member, Il.IsStore(op)));
                            }
                            if (op is ILOpCode.Ldsfld or ILOpCode.Ldsflda or ILOpCode.Stsfld && names.Field(token) is { } staticField)
                            {
                                body.Calls.Add(Initialized + TypeOf(staticField));
                            }
                        }
                        else if (Il.IsCall(op) && names.Method(token) is { } callee)
                        {
                            body.Calls.Add(callee);
                            body.Calls.Add(Initialized + TypeOf(callee));
                            if (op == ILOpCode.Newobj)
                            {
                                body.Calls.Add(Constructed + TypeOf(callee));
                            }
                        }
                    }
                    bodies[key] = body;
                }
            }
        }

        public string AssemblyName { get; }

        public IReadOnlyCollection<string> Members { get; }

        /// <summary>Every member a consumer's references reach, each as read or written.</summary>
        public IEnumerable<(string Member, bool Write)> Effects(IEnumerable<Root> roots)
        {
            foreach (var root in roots)
            {
                if (root.IsField)
                {
                    if (fieldMembers.TryGetValue(root.Key, out var member))
                    {
                        yield return (member, root.Write);
                    }
                    continue;
                }
                // A parameterless constructor writes the type's initializers, not a value the consumer chose.
                var initializersOnly = root.Key.EndsWith("::.ctor`0()Void", StringComparison.Ordinal);
                foreach (var effect in Reach(root.Key).Concat(root.New ? Reach(Constructed + TypeOf(root.Key)) : []))
                {
                    if (!(initializersOnly && effect.Write))
                    {
                        yield return effect;
                    }
                }
            }
        }

        private static string TypeOf(string method) => method[..method.IndexOf("::", StringComparison.Ordinal)];

        private HashSet<(string Member, bool Write)> Reach(string method)
        {
            if (reached.TryGetValue(method, out var known))
            {
                return known;
            }
            var effects = new HashSet<(string Member, bool Write)>();
            var seen = new HashSet<string>(StringComparer.Ordinal) { method };
            var pending = new Stack<string>([method]);
            while (pending.TryPop(out var next))
            {
                if (!bodies.TryGetValue(next, out var body))
                {
                    continue;
                }
                effects.UnionWith(body.Fields);
                foreach (var call in body.Calls.Where(seen.Add))
                {
                    pending.Push(call);
                }
            }
            reached[method] = effects;
            return effects;
        }

        private static bool IsPlumbing(MetadataReader md, Names names, TypeDefinitionHandle type, MethodDefinition method)
        {
            var name = md.GetString(method.Name);
            if (Plumbing.Contains(name))
            {
                return true;
            }
            // The record copy constructor `.ctor(Self original)` that `with` clones through.
            if (name != ".ctor")
            {
                return false;
            }
            var signature = method.DecodeSignature(names, null);
            return signature.ParameterTypes.Length == 1 && signature.ParameterTypes[0] == names.TypeDef(type);
        }

        /// <summary>A record class or record struct: the compiler gives both a PrintMembers.</summary>
        private static bool IsRecord(MetadataReader md, TypeDefinition type) =>
            type.GetMethods().Any(m => md.GetString(md.GetMethodDefinition(m).Name) == "PrintMembers");

        private static bool IsEnum(Names names, TypeDefinition type) => !type.BaseType.IsNil && names.Type(type.BaseType) == "System.Enum";

        private static bool IsPublic(MetadataReader md, TypeDefinitionHandle handle)
        {
            for (var type = md.GetTypeDefinition(handle); ; type = md.GetTypeDefinition(type.GetDeclaringType()))
            {
                var visibility = type.Attributes & TypeAttributes.VisibilityMask;
                if (visibility == TypeAttributes.Public)
                {
                    return true;
                }
                if (visibility != TypeAttributes.NestedPublic)
                {
                    return false;
                }
            }
        }

        private static bool DerivesFromSerializer(MetadataReader md, Names names, TypeDefinitionHandle handle)
        {
            for (var depth = 0; depth < 16 && !handle.IsNil; depth++)
            {
                var type = md.GetTypeDefinition(handle);
                var declaring = type.GetDeclaringType();
                if (!declaring.IsNil && DerivesFromSerializer(md, names, declaring))
                {
                    return true;
                }
                var baseType = type.BaseType;
                if (baseType.IsNil)
                {
                    return false;
                }
                var baseName = names.Type(baseType);
                if (SerializerBases.Any(b => baseName == b || baseName.StartsWith(b + "<", StringComparison.Ordinal)))
                {
                    return true;
                }
                handle = baseType.Kind == HandleKind.TypeDefinition ? (TypeDefinitionHandle)baseType : default;
            }
            return false;
        }
    }

    /// <summary>A consumer assembly's references into the declaring assembly.</summary>
    private static class Consumer
    {
        public static List<Root> Roots(string path, string declaring)
        {
            using var pe = new PEReader(File.OpenRead(path));
            var md = pe.GetMetadataReader();
            var names = new Names(md, declaring);
            var roots = new List<Root>();
            foreach (var m in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(m);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }
                foreach (var (op, token) in Il.Tokens(pe.GetMethodBody(method.RelativeVirtualAddress)))
                {
                    if (Il.IsField(op))
                    {
                        if (names.Field(token) is { } field)
                        {
                            roots.Add(new Root(field, IsField: true, Il.IsStore(op), New: false));
                        }
                    }
                    else if (Il.IsCall(op) && names.Method(token) is { } callee)
                    {
                        roots.Add(new Root(callee, IsField: false, Write: false, New: op == ILOpCode.Newobj));
                    }
                }
            }
            return roots;
        }
    }


    /// <summary>
    /// Names types, methods and fields the same way in every assembly: <c>Namespace.Outer/Inner</c> and
    /// <c>Type::name(parameters)returns</c>. With a <c>target</c> assembly name, only references into that assembly
    /// are named (the rest come back null); without one, only the reader's own definitions are.
    /// </summary>
    private sealed class Names(MetadataReader md, string? target) : ISignatureTypeProvider<string, object?>
    {
        public string TypeDef(TypeDefinitionHandle handle)
        {
            var type = md.GetTypeDefinition(handle);
            var declaring = type.GetDeclaringType();
            var name = md.GetString(type.Name);
            return declaring.IsNil ? Qualify(md.GetString(type.Namespace), name) : $"{TypeDef(declaring)}/{name}";
        }

        public string TypeRef(TypeReferenceHandle handle, out string? assembly)
        {
            var type = md.GetTypeReference(handle);
            var name = md.GetString(type.Name);
            var scope = type.ResolutionScope;
            if (scope.Kind == HandleKind.TypeReference)
            {
                return $"{TypeRef((TypeReferenceHandle)scope, out assembly)}/{name}";
            }
            assembly = scope.Kind == HandleKind.AssemblyReference ? md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) : null;
            return Qualify(md.GetString(type.Namespace), name);
        }

        public string Type(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => TypeDef((TypeDefinitionHandle)handle),
            HandleKind.TypeReference => TypeRef((TypeReferenceHandle)handle, out _),
            HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null),
            _ => "?",
        };

        public string MethodDef(MethodDefinitionHandle handle)
        {
            var method = md.GetMethodDefinition(handle);
            return Key(TypeDef(method.GetDeclaringType()), md.GetString(method.Name), method.DecodeSignature(this, null));
        }

        /// <summary>A call target's key, when it is declared where this reader names things.</summary>
        public string? Method(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                    return target is null ? MethodDef((MethodDefinitionHandle)handle) : null;
                case HandleKind.MethodSpecification:
                    return Method(md.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
                case HandleKind.MemberReference:
                    var member = md.GetMemberReference((MemberReferenceHandle)handle);
                    if (member.GetKind() != MemberReferenceKind.Method || Owner(member.Parent) is not { } owner)
                    {
                        return null;
                    }
                    return Key(owner, md.GetString(member.Name), member.DecodeMethodSignature(this, null));
                default:
                    return null;
            }
        }

        /// <summary>A field access's key, when the field is declared where this reader names things.</summary>
        public string? Field(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.FieldDefinition when target is null:
                    var field = md.GetFieldDefinition((FieldDefinitionHandle)handle);
                    return $"{TypeDef(field.GetDeclaringType())}::{md.GetString(field.Name)}";
                case HandleKind.MemberReference:
                    var member = md.GetMemberReference((MemberReferenceHandle)handle);
                    return member.GetKind() == MemberReferenceKind.Field && Owner(member.Parent) is { } owner
                        ? $"{owner}::{md.GetString(member.Name)}"
                        : null;
                default:
                    return null;
            }
        }

        /// <summary>The declaring type of a member reference, generic instantiations reduced to their definition.</summary>
        private string? Owner(EntityHandle parent)
        {
            switch (parent.Kind)
            {
                case HandleKind.TypeReference:
                    var name = TypeRef((TypeReferenceHandle)parent, out var assembly);
                    return target is not null && assembly == target ? name : null;
                case HandleKind.TypeDefinition:
                    return target is null ? TypeDef((TypeDefinitionHandle)parent) : null;
                case HandleKind.TypeSpecification:
                    var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                    if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                    {
                        return null;
                    }
                    blob.ReadSignatureTypeCode();
                    return Owner(blob.ReadTypeHandle());
                default:
                    return null;
            }
        }

        private static string Key(string type, string name, MethodSignature<string> signature) =>
            $"{type}::{name}`{signature.GenericParameterCount}({string.Join(",", signature.ParameterTypes)}){signature.ReturnType}";

        private static string Qualify(string ns, string name) => ns.Length == 0 ? name : $"{ns}.{name}";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeDef(handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => TypeRef(handle, out _);

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPinnedType(string elementType) => elementType;

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            $"{genericType}<{string.Join(",", typeArguments)}>";

        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";

        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";

        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    }

    /// <summary>
    /// Walks a method body's instructions and yields the metadata tokens of calls and field accesses.
    /// </summary>
    private static class Il
    {
        public static bool IsCall(ILOpCode op) => op is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Ldftn or ILOpCode.Ldvirtftn;

        public static bool IsField(ILOpCode op) => op is ILOpCode.Ldfld or ILOpCode.Ldflda or ILOpCode.Ldsfld or ILOpCode.Ldsflda or ILOpCode.Stfld or ILOpCode.Stsfld;

        public static bool IsStore(ILOpCode op) => op is ILOpCode.Stfld or ILOpCode.Stsfld;

        public static List<(ILOpCode Op, EntityHandle Token)> Tokens(MethodBodyBlock body)
        {
            var il = body.GetILReader();
            var found = new List<(ILOpCode Op, EntityHandle Token)>();
            while (il.RemainingBytes > 0)
            {
                ushort value = il.ReadByte();
                if (value == 0xFE)
                {
                    value = (ushort)(0xFE00 | il.ReadByte());
                }
                var op = (ILOpCode)value;
                switch (Operands[value])
                {
                    case OperandType.InlineMethod or OperandType.InlineField:
                        var token = il.ReadInt32();
                        if (IsCall(op) || IsField(op))
                        {
                            found.Add((op, MetadataTokens.EntityHandle(token)));
                        }
                        break;
                    case OperandType.InlineSwitch:
                        var targets = il.ReadInt32();
                        il.Offset += 4 * targets;
                        break;
                    case OperandType.InlineI8 or OperandType.InlineR:
                        il.Offset += 8;
                        break;
                    case OperandType.InlineBrTarget or OperandType.InlineI or OperandType.InlineString or OperandType.InlineSig or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR:
                        il.Offset += 4;
                        break;
                    case OperandType.InlineVar:
                        il.Offset += 2;
                        break;
                    case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                        il.Offset += 1;
                        break;
                    default:
                        break;
                }
            }
            return found;
        }
    }
}
