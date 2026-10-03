// Emits contracts/proto/defs.proto: typed messages mirroring RimWorld's ThingDef
// and TerrainDef, every class their fields reach, and every subclass of a
// polymorphic field type (every CompProperties, ...), by reflecting the game's
// managed assemblies. See contracts/schema-generation.md.
//
// Rules, with no allow-list and no silent skip:
//   * A message holds every public instance field of the class and its bases,
//     except [Unsaved] fields and fields typed as runtime state (a type deriving
//     from Verse.Entity or UnityEngine.Object, implementing Verse.ILoadReferenceable,
//     a delegate or an interface, alone or as a collection element or generic
//     argument). Each such field is listed in the output header. Field names are the
//     CLR names, so native fills a message by protobuf reflection by name alone.
//   * Def references become the def's defName (string); System.Type becomes its
//     full name; enums keep their numeric values.
//   * Nullable<T> becomes a proto3 optional field. List, array and HashSet become
//     repeated fields; a Dictionary becomes repeated key/value entry messages; a
//     collection nested in a collection becomes a synthetic wrapper message with
//     one repeated "items" field. Synthetic messages carry the clr_synthetic option.
//   * A field whose class has subclasses becomes a oneof wrapper ("<Class>Any")
//     over the class and each concrete subclass. An abstract class with no fields
//     and no concrete subclass (DefModExtension in vanilla) becomes an empty
//     message and is listed in the header.
//   * A field that closes a reference cycle is skipped and listed in the header:
//     the graph is walked depth-first from the roots (ThingDef, TerrainDef) with
//     fields in declaration order and subclasses in ordinal order, and a field whose
//     type (or any subclass of it) is still being built is the back edge.
//   * Any other field type the tool cannot represent fails the run
//     and the failure names the declaring class and field. Nothing is written.
using System.Reflection;
using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        var managed = DefaultManagedDir();
        string? output = null;
        var check = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--managed-dir": managed = args[++i]; break;
                case "--output": output = args[++i]; break;
                case "--check": check = true; break;
                default: return Fail($"unknown argument {args[i]}");
            }
        }
        if (managed == null || output == null)
            return Fail("usage: defmirror --output <defs.proto> [--managed-dir <dir with Assembly-CSharp.dll>] [--check]");
        if (!File.Exists(Path.Combine(managed, "Assembly-CSharp.dll")))
            return Fail($"{managed} has no Assembly-CSharp.dll");

        var paths = Directory.GetFiles(managed, "*.dll");
        var resolver = new PathAssemblyResolver(paths);
        using var context = new MetadataLoadContext(resolver, "mscorlib");
        var generator = new Generator(context, managed);
        var text = generator.Run(new[] { "Verse.ThingDef", "Verse.TerrainDef" });
        if (generator.Errors.Count > 0)
        {
            Console.Error.WriteLine($"defmirror: {generator.Errors.Count} field(s) could not be represented; nothing written:");
            foreach (var error in generator.Errors) Console.Error.WriteLine("  " + error);
            return 1;
        }
        var summary = $"{generator.MessageCount} messages, {generator.EnumCount} enums, {generator.SkippedCount} runtime-state fields skipped";
        if (check)
        {
            var current = File.Exists(output) ? File.ReadAllText(output).Replace("\r\n", "\n") : "";
            if (current != text)
                return Fail($"{output} drifted from the game assemblies; run `task defmirror:generate`");
            Console.WriteLine($"defmirror: {output} matches ({summary})");
            return 0;
        }
        File.WriteAllText(output, text, new UTF8Encoding(false));
        Console.WriteLine($"defmirror: wrote {output} ({summary})");
        return 0;
    }

    // The restored Krafs.Rimworld.Ref package (DefMirror.csproj); a game
    // install's Managed directory overrides it with --managed-dir.
    private static string? DefaultManagedDir() =>
        typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RimWorldManagedDir")?.Value;

    private static int Fail(string message)
    {
        Console.Error.WriteLine("defmirror: " + message);
        return 1;
    }
}

internal sealed class Unsupported : Exception
{
    public Unsupported(string message) : base(message) { }
}

// A message the generator invents for a shape protobuf lacks: a dictionary
// entry (Key, Value) or a wrapper around a collection nested in a collection
// (Value is the repeated element).
internal sealed record Synth(bool Entry, Ref? Key, Ref Value);

// How a field's element appears on the wire.
internal sealed record Ref(string? Scalar, Type? Clr, bool IsEnum, bool IsAny, Synth? Synthetic = null);

internal sealed class Field
{
    public required string Name;
    public required int Number;
    public bool Repeated;
    public bool Optional;
    public Ref? Element;
    public required FieldInfo Info;
}

internal sealed class Message
{
    public required Type Clr;
    public List<Field> Fields = new();
}

internal sealed class Generator
{
    private const string Package = "rimgovernor.defs.v1";
    private static readonly string[] DefAssemblies = { "Assembly-CSharp", "Assembly-CSharp-firstpass" };
    private const BindingFlags InstanceFields =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;

    private readonly MetadataLoadContext context;
    private readonly string managed;
    private readonly Dictionary<Type, List<Type>> subclasses = new();
    private readonly Dictionary<Type, Message> messages = new();
    private readonly HashSet<Type> anys = new(); // classes with subclasses: a "<Class>Any" wrapper
    private readonly HashSet<Type> emptyAbstract = new();
    private readonly HashSet<Type> enums = new();
    private readonly HashSet<Synth> synths = new();
    private readonly Dictionary<Type, int> open = new(); // types on the depth-first stack
    private readonly SortedSet<string> errors = new(StringComparer.Ordinal);
    private readonly SortedSet<string> skipped = new(StringComparer.Ordinal);
    private Dictionary<Type, string> names = new();

    public Generator(MetadataLoadContext context, string managed)
    {
        this.context = context;
        this.managed = managed;
    }

    public IReadOnlyCollection<string> Errors => errors;
    public int MessageCount => messages.Count + anys.Count + synths.Count;
    public int EnumCount => enums.Count;
    public int SkippedCount => skipped.Count;

    public string Run(string[] roots)
    {
        var assemblies = DefAssemblies.Select(n => context.LoadFromAssemblyPath(Path.Combine(managed, n + ".dll"))).ToArray();
        foreach (var assembly in assemblies)
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsGenericTypeDefinition || !type.IsClass) continue;
                for (var b = type.BaseType; b != null; b = b.BaseType)
                {
                    if (!subclasses.TryGetValue(b, out var list)) subclasses[b] = list = new List<Type>();
                    list.Add(type);
                }
            }

        foreach (var list in subclasses.Values) list.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
        foreach (var root in roots)
        {
            var type = assemblies.Select(a => a.GetType(root)).FirstOrDefault(t => t != null)
                ?? throw new InvalidOperationException($"{root} not found in {string.Join(", ", DefAssemblies)}");
            Need(type);
        }
        if (errors.Count > 0) return "";
        AssignNames();
        if (errors.Count > 0) return "";
        var text = Emit(assemblies);
        return errors.Count > 0 ? "" : text;
    }

    // ---- type mapping ----

    private static bool IsDef(Type t)
    {
        for (var b = t; b != null; b = b.BaseType)
            if (b.FullName == "Verse.Def") return true;
        return false;
    }

    private static bool IsNullable(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1";

    private static bool IsCollection(Type t) =>
        t.IsArray || (t.IsGenericType && t.GetGenericTypeDefinition().FullName is
            "System.Collections.Generic.List`1" or "System.Collections.Generic.HashSet`1" or "System.Collections.Generic.Dictionary`2");

    // The first type in t (itself, an array element or a generic argument) that
    // is runtime state: a Verse.Entity, a Verse.ILoadReferenceable, a delegate,
    // an interface or a UnityEngine.Object (Material, Texture, ...).
    private static Type? RuntimeState(Type t)
    {
        if (t.IsArray) return RuntimeState(t.GetElementType()!);
        if (t.IsInterface) return t;
        for (var b = t; b != null; b = b.BaseType)
            if (b.FullName is "Verse.Entity" or "System.MulticastDelegate" or "System.Delegate" or "UnityEngine.Object") return t;
        if (t.FullName == "Verse.ILoadReferenceable" || t.GetInterfaces().Any(i => i.FullName == "Verse.ILoadReferenceable")) return t;
        if (t.IsGenericType)
            foreach (var a in t.GetGenericArguments())
                if (RuntimeState(a) is { } inner) return inner;
        return null;
    }

    private static string? ScalarOf(Type t) => t.FullName switch
    {
        "System.Boolean" => "bool",
        "System.SByte" or "System.Int16" or "System.Int32" => "int32",
        "System.Byte" or "System.UInt16" or "System.UInt32" => "uint32",
        "System.Int64" => "int64",
        "System.UInt64" => "uint64",
        "System.Single" => "float",
        "System.Double" => "double",
        "System.String" => "string",
        "System.Type" => "string", // full name, resolved by the native fill
        _ => null,
    };

    private bool InDefAssembly(Type t) => DefAssemblies.Contains(t.Assembly.GetName().Name);

    private Ref Resolve(Type t)
    {
        if (ScalarOf(t) is { } scalar) return new Ref(scalar, null, false, false);
        if (t.IsEnum)
        {
            enums.Add(t);
            return new Ref(null, t, true, false);
        }
        if (t.IsPointer || t.IsByRef) throw new Unsupported($"{t} is a pointer or reference");
        if (t.IsGenericParameter) throw new Unsupported($"{t} is an open generic parameter");
        if (t.IsGenericType) throw new Unsupported($"unsupported generic {t}");
        if (t.IsArray) throw new Unsupported($"{t} is a multidimensional array");
        if (IsDef(t)) return new Ref("string", null, false, false); // defName
        if (t.IsValueType)
        {
            if (t.FullName!.StartsWith("System.", StringComparison.Ordinal)) throw new Unsupported($"unsupported value type {t.FullName}");
            Need(t);
            return new Ref(null, t, false, false);
        }
        if (!InDefAssembly(t)) throw new Unsupported($"class {t.FullName} is outside the game's def assemblies");
        var polymorphic = subclasses.ContainsKey(t);
        if (!polymorphic && t.IsAbstract)
        {
            if (DataFields(t).Count > 0) throw new Unsupported($"abstract class {t.FullName} has fields but no concrete subclass");
            emptyAbstract.Add(t);
        }
        Need(t);
        return new Ref(null, t, false, polymorphic);
    }

    // Depth-first from the roots, fields in declaration order and subclasses in
    // ordinal order, so the same assemblies always yield the same output. A type
    // being built is "open"; a polymorphic class opens together with all its
    // subclasses, because its "<Class>Any" wrapper holds each of them.
    private void Need(Type t)
    {
        if (messages.ContainsKey(t)) return;
        messages[t] = new Message { Clr = t };
        var group = new List<Type> { t };
        List<Type>? subs = null;
        if (t.IsClass && subclasses.TryGetValue(t, out subs))
        {
            group.AddRange(subs);
            anys.Add(t);
        }
        foreach (var g in group) open[g] = open.GetValueOrDefault(g) + 1;
        Build(t);
        if (subs != null) foreach (var s in subs) Need(s);
        foreach (var g in group) open[g]--;
    }

    // The types a field's type needs a message for: the payload of a Nullable,
    // the elements of a collection, each generic argument.
    private IEnumerable<Type> MessageTargets(Type t)
    {
        if (t.IsArray) return MessageTargets(t.GetElementType()!);
        if (t.IsGenericType) return t.GetGenericArguments().SelectMany(MessageTargets);
        if (ScalarOf(t) != null || t.IsEnum || t.IsPointer || t.IsByRef || t.IsGenericParameter || IsDef(t)) return Enumerable.Empty<Type>();
        if (t.IsValueType ? t.FullName!.StartsWith("System.", StringComparison.Ordinal) : !InDefAssembly(t)) return Enumerable.Empty<Type>();
        return new[] { t };
    }

    // The open type a field's type reaches (directly, or through a "<Class>Any"
    // wrapper's subclasses): the field closes a reference cycle.
    private Type? ClosesCycle(Type fieldType)
    {
        foreach (var target in MessageTargets(fieldType))
        {
            if (open.GetValueOrDefault(target) > 0) return target;
            if (target.IsClass && subclasses.TryGetValue(target, out var subs))
                foreach (var s in subs)
                    if (open.GetValueOrDefault(s) > 0) return s;
        }
        return null;
    }

    private static bool HasUnsaved(FieldInfo f) =>
        f.CustomAttributes.Any(a => a.AttributeType.Name == "UnsavedAttribute");

    private static List<FieldInfo> DataFields(Type t)
    {
        var chain = new List<Type>();
        for (var c = t; c != null && c.FullName is not ("System.Object" or "System.ValueType"); c = c.BaseType)
            chain.Insert(0, c);
        var fields = new List<FieldInfo>();
        foreach (var c in chain)
            foreach (var f in c.GetFields(InstanceFields).OrderBy(f => f.MetadataToken))
            {
                if (f.IsStatic || f.IsLiteral || HasUnsaved(f)) continue;
                if (f.Name.Contains('<')) continue; // compiler-generated backing fields
                var hidden = fields.FindIndex(x => x.Name == f.Name);
                if (hidden >= 0) fields[hidden] = f; else fields.Add(f);
            }
        return fields;
    }

    // A type as C# spells it, without assembly identities.
    private static string TypeText(Type t) =>
        t.IsArray ? TypeText(t.GetElementType()!) + "[]"
        : t.IsGenericType ? t.GetGenericTypeDefinition().FullName!.Split('`')[0] + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeText)) + ">"
        : t.FullName!;

    private void Build(Type t)
    {
        var message = messages[t];
        var number = 0;
        var keys = new Dictionary<string, string>();
        foreach (var f in DataFields(t))
        {
            var where = $"{f.DeclaringType!.FullName}.{f.Name}";
            if (RuntimeState(f.FieldType) is { } state)
            {
                skipped.Add($"{where}: {TypeText(f.FieldType)} (runtime state {TypeText(state)})");
                continue;
            }
            if (ClosesCycle(f.FieldType) is { } cycle)
            {
                skipped.Add($"{where}: {TypeText(f.FieldType)} (reference cycle back to {cycle.FullName})");
                continue;
            }
            var key =char.ToUpperInvariant(f.Name[0]) + f.Name.Substring(1).Replace("_", "");
            if (keys.TryGetValue(key, out var other))
            {
                errors.Add($"{where}: name collides with {other} after protobuf name normalisation");
                continue;
            }
            keys[key] = f.Name;
            try
            {
                var field = new Field { Name = f.Name, Number = ++number, Info = f };
                var ft = f.FieldType;
                if (IsNullable(ft))
                {
                    field.Optional = true;
                    field.Element = ResolveElement(ft.GetGenericArguments()[0]);
                }
                else if (IsCollection(ft))
                {
                    field.Repeated = true;
                    field.Element = CollectionElement(ft);
                }
                else
                {
                    field.Element = Resolve(ft);
                }
                message.Fields.Add(field);
            }
            catch (Unsupported u)
            {
                errors.Add($"{where}: {u.Message}");
            }
        }
    }

    // The repeated element of a collection: the item, or a key/value entry.
    private Ref CollectionElement(Type t)
    {
        if (t.IsArray)
        {
            if (t.GetArrayRank() != 1) throw new Unsupported($"{t} is a multidimensional array");
            return ResolveElement(t.GetElementType()!);
        }
        var args = t.GetGenericArguments();
        if (args.Length == 2) return Synthetic(new Synth(true, ResolveElement(args[0]), ResolveElement(args[1])));
        return ResolveElement(args[0]);
    }

    // An item of a collection or a Nullable payload: protobuf cannot nest
    // collections or repeat an optional, so a nested collection is wrapped.
    private Ref ResolveElement(Type t)
    {
        if (IsNullable(t)) throw new Unsupported($"nullable {t} inside a collection");
        if (IsCollection(t)) return Synthetic(new Synth(false, null, CollectionElement(t)));
        return Resolve(t);
    }

    private Ref Synthetic(Synth s)
    {
        synths.Add(s);
        return new Ref(null, null, false, false, s);
    }

    // ---- naming ----

    private static string SimpleName(Type t)
    {
        var parts = new List<string>();
        for (var c = t; c != null; c = c.DeclaringType) parts.Insert(0, c.Name);
        return string.Join("_", parts);
    }

    private void AssignNames()
    {
        var all = messages.Keys.Concat(enums).ToList();
        var groups = all.GroupBy(SimpleName).ToList();
        names = new Dictionary<Type, string>();
        foreach (var g in groups)
            foreach (var t in g)
                names[t] = g.Count() == 1 ? g.Key : (t.Namespace ?? "").Replace('.', '_') + "_" + g.Key;
        var used = new Dictionary<string, Type>();
        foreach (var (t, n) in names)
        {
            if (used.TryGetValue(n, out var other))
                errors.Add($"{t.FullName}: message name {n} collides with {other.FullName}");
            used[n] = t;
        }
        foreach (var t in anys)
            if (used.ContainsKey(names[t] + "Any")) errors.Add($"{t.FullName}: wrapper name {names[t]}Any is taken");
    }

    private string Part(Ref r) =>
        r.Synthetic != null ? SynthName(r.Synthetic) : r.Scalar ?? names[r.Clr!] + (r.IsAny ? "Any" : "");

    private string SynthName(Synth s) =>
        s.Entry ? "Entry_" + Part(s.Key!) + "_" + Part(s.Value) : "List_" + Part(s.Value);

    // ---- emit ----

    private static string UpperSnake(string s)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '_') { sb.Append('_'); continue; }
            if (i > 0 && char.IsUpper(c) && s[i - 1] != '_' && (char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1]) || (i + 1 < s.Length && char.IsLower(s[i + 1]))))
                sb.Append('_');
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    private string TypeName(Ref r) =>
        r.Scalar ?? "." + Package + "." + Part(r);

    private string Emit(Assembly[] assemblies)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append('\n');
        L("// Code generated by tools/defmirror from the game's managed assemblies; DO NOT EDIT.");
        L("// Regenerate with `task defmirror:generate`; `task build` fails on drift.");
        foreach (var a in assemblies) L("// Source: " + a.GetName().Name + " " + a.GetName().Version);
        L("//");
        L("// Public instance fields only, minus [Unsaved] fields. Def references are the");
        L("// defName, System.Type is its full name, Nullable<T> is optional, a Dictionary is");
        L("// repeated Entry_ messages (key, value), a collection nested in a collection is a");
        L("// List_ message (items). Both carry the clr_synthetic option.");
        L("//");
        L("// Abstract classes without fields or concrete subclass (empty messages):");
        foreach (var t in emptyAbstract.OrderBy(t => t.FullName, StringComparer.Ordinal)) L("//   " + t.FullName);
        L("//");
        L("// Fields skipped (runtime state, not def data): a type deriving from Verse.Entity or");
        L("// UnityEngine.Object, implementing Verse.ILoadReferenceable, or a delegate or interface,");
        L("// alone or as a collection element or generic argument; or a field that closes a");
        L("// reference cycle in the type graph (depth-first from ThingDef, TerrainDef; a");
        L("// polymorphic class holds its subclasses), which protobuf could only mirror as a");
        L("// recursive message.");
        foreach (var s in skipped) L("//   " + s);
        L();
        L("syntax = \"proto3\";");
        L("package " + Package + ";");
        L("option csharp_namespace = \"RimGovernor.Protocol.Defs\";");
        L("option go_package = \"github.com/davidarcher/RimGovernor/go/internal/wire/defspb;defspb\";");
        L("import \"google/protobuf/descriptor.proto\";");
        L();
        L("extend google.protobuf.MessageOptions {");
        L("  // The CLR class a message mirrors; native instantiates it by this name.");
        L("  string clr_type = 50100;");
        L("  // \"entry\" (a Dictionary entry: key, value) or \"list\" (a nested collection: items).");
        L("  string clr_synthetic = 50101;");
        L("}");

        foreach (var e in enums.OrderBy(t => names[t], StringComparer.Ordinal))
        {
            var name = names[e];
            var prefix = UpperSnake(name);
            var members = new List<(string Name, long Value)>();
            foreach (var f in e.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                try { members.Add((f.Name, Convert.ToInt64(f.GetRawConstantValue()))); }
                catch (OverflowException) { errors.Add($"{e.FullName}.{f.Name}: value outside int64"); }
            }
            L();
            L("// " + e.FullName + (e.CustomAttributes.Any(a => a.AttributeType.Name == "FlagsAttribute") ? " (flags: the field holds the bitmask)" : ""));
            L("enum " + name + " {");
            foreach (var m in members.Where(m => m.Value is < int.MinValue or > int.MaxValue))
                errors.Add($"{e.FullName}.{m.Name}: value {m.Value} outside int32");
            if (!members.Any(m => m.Value == 0)) L("  " + prefix + "_UNSPECIFIED = 0;");
            var seen = new HashSet<long>();
            if (members.Any(m => !seen.Add(m.Value))) L("  option allow_alias = true;");
            var memberNames = new HashSet<string>();
            // Proto3 requires the zero member first.
            foreach (var m in members.OrderBy(m => m.Value == 0 ? 0 : 1).ThenBy(m => m.Value).ThenBy(m => m.Name, StringComparer.Ordinal))
            {
                var mn = prefix + "_" + UpperSnake(m.Name);
                if (!memberNames.Add(mn)) errors.Add($"{e.FullName}.{m.Name}: member name collides after normalisation");
                L("  " + mn + " = " + m.Value + ";");
            }
            L("}");
        }

        foreach (var (t, m) in messages.OrderBy(p => names[p.Key], StringComparer.Ordinal))
        {
            L();
            L("// " + t.FullName);
            L("message " + names[t] + " {");
            L("  option (clr_type) = \"" + t.FullName + "\";");
            foreach (var f in m.Fields)
                L($"  {(f.Repeated ? "repeated " : f.Optional ? "optional " : "")}{TypeName(f.Element!)} {f.Name} = {f.Number};");
            L("}");
        }

        foreach (var s in synths.OrderBy(SynthName, StringComparer.Ordinal))
        {
            L();
            L("message " + SynthName(s) + " {");
            if (s.Entry)
            {
                L("  option (clr_synthetic) = \"entry\";");
                L($"  {TypeName(s.Key!)} key = 1;");
                L($"  {TypeName(s.Value)} value = 2;");
            }
            else
            {
                L("  option (clr_synthetic) = \"list\";");
                L($"  repeated {TypeName(s.Value)} items = 1;");
            }
            L("}");
        }

        foreach (var t in anys.OrderBy(t => names[t], StringComparer.Ordinal))
        {
            var variants = new[] { t }.Concat(subclasses[t]).Where(v => !v.IsAbstract && messages.ContainsKey(v))
                .OrderBy(v => names[v], StringComparer.Ordinal).ToList();
            L();
            L("// One " + t.FullName + " or a concrete subclass.");
            L("message " + names[t] + "Any {");
            L("  oneof value {");
            var n = 0;
            foreach (var v in variants) L($"    .{Package}.{names[v]} {names[v]} = {++n};");
            L("  }");
            L("}");
        }
        return sb.ToString();
    }
}
