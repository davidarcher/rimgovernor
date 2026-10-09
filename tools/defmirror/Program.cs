// Emits contracts/proto/defs.proto: typed messages mirroring every concrete
// Verse.Def class of the game, every class their fields reach, and every subclass of a
// polymorphic field type (every CompProperties, ...), by reflecting the game's
// managed assemblies. See contracts/schema-generation.md.
//
// Rules, with no allow-list and no silent skip:
//   * A message holds every instance field of the class and its bases, whatever its
//     visibility (the game's XML loader fills private fields as well), except
//     [Unsaved] fields and fields typed as runtime state (a type deriving from
//     Verse.Entity or UnityEngine.Object, implementing Verse.ILoadReferenceable, a
//     delegate, an interface, System.Object (untyped) or any other class outside the
//     game's def assemblies, alone or as a collection element or generic argument;
//     also a non-public field of a generic type the mapping has no shape for). Each
//     such field is listed in the output header. Field names are the CLR names, so
//     native fills a message by protobuf reflection by name alone.
//   * A class carries def data only if the XML loader can build it, so these are
//     runtime state as well, and a field of one is skipped like any other:
//       - a class with a concrete subclass but no instantiable one, or a concrete class
//         without a public parameterless constructor (a worker or handler built with its
//         def or map as an argument);
//       - a non-public field holding one instance of a class family (abstract, or with
//         subclasses) that no public field reaches: a lazily built worker, graphic or
//         noise module (the data a non-public field holds is a collection or a struct);
//       - the classes of RuntimeClasses (with their subclasses), the explicit table of
//         what no structural rule separates, each with its reason.
//   * Def references become the def's defName (string); System.Type becomes its
//     full name; enums keep their numeric values; RimWorld.QuestGen.SlateRef<T>
//     becomes the string it holds (its XML text, a literal or a "$variable").
//   * Every concrete Verse.Def class is a root. DefSets holds one repeated field
//     per root, but ThingDef and TerrainDef (DefinitionCatalog fields 8 and 9).
//   * Nullable<T> becomes a proto3 optional field. List, array and HashSet become
//     repeated fields; a Dictionary becomes repeated key/value entry messages; a
//     collection nested in a collection becomes a synthetic wrapper message with
//     one repeated "items" field. Synthetic messages carry the clr_synthetic option.
//   * A repeated element that is a reference type mirrored as a message (a class,
//     a "<Class>Any" wrapper, a nested collection) is wrapped in an "Opt_<Element>"
//     message with one optional "value" field, unset for a null element, because the
//     game uses null list entries positionally. Structs, enums and string-mapped
//     elements (defName, Type) are not wrapped. A dictionary value is a message
//     field of its entry, unset for null.
//   * A field whose class has subclasses becomes a oneof wrapper ("<Class>Any")
//     over the class and each concrete subclass. An abstract class with no fields
//     and no concrete subclass (DefModExtension in vanilla) becomes an empty
//     message and is listed in the header.
//   * A field that reaches a class being built (a tree node holding its children)
//     is a recursive message. The type graph is walked depth-first from the roots
//     (ThingDef, TerrainDef, then every other concrete Verse.Def class in ordinal
//     order), fields in declaration order and subclasses in ordinal order.
//   * Every Def message ends with the derived field modPackageId (clr_path option
//     "modContentPack.PackageId"): Def.modContentPack is [Unsaved], so it is not
//     reflected and native reads the path.
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
        string? report = null;
        var check = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--managed-dir": managed = args[++i]; break;
                case "--output": output = args[++i]; break;
                case "--check": check = true; break;
                case "--report": report = args[++i]; break;
                default: return Fail($"unknown argument {args[i]}");
            }
        }
        if (managed == null || (output == null && report == null))
            return Fail("usage: defmirror (--output <defs.proto> [--check] | --report <file>) [--managed-dir <dir with Assembly-CSharp.dll>]");
        if (!File.Exists(Path.Combine(managed, "Assembly-CSharp.dll")))
            return Fail($"{managed} has no Assembly-CSharp.dll");

        var paths = Directory.GetFiles(managed, "*.dll");
        var resolver = new PathAssemblyResolver(paths);
        using var context = new MetadataLoadContext(resolver, "mscorlib");
        var generator = new Generator(context, managed);
        if (report != null)
        {
            File.WriteAllText(report, generator.Report(), new UTF8Encoding(false));
            Console.WriteLine($"defmirror: wrote {report}");
            return 0;
        }
        var text = generator.Run();
        if (generator.Errors.Count > 0)
        {
            Console.Error.WriteLine($"defmirror: {generator.Errors.Count} field(s) could not be represented; nothing written:");
            foreach (var error in generator.Errors) Console.Error.WriteLine("  " + error);
            return 1;
        }
        var summary = $"{generator.MessageCount} messages, {generator.EnumCount} enums, {generator.SkippedCount} runtime-state fields skipped, {generator.HolderCount} constant classes with {generator.ConstantFieldCount} members ({generator.UnrepresentedCount} not carried)";
        if (check)
        {
            var current = File.Exists(output) ? File.ReadAllText(output).Replace("\r\n", "\n") : "";
            if (current != text)
                return Fail($"{output} drifted from the game assemblies; run `task defmirror:generate`");
            Console.WriteLine($"defmirror: {output} matches ({summary})");
            return 0;
        }
        File.WriteAllText(output!, text, new UTF8Encoding(false));
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
internal sealed record Synth(bool Entry, Ref? Key, Ref Value, bool Optional = false);

// How a field's element appears on the wire.
internal sealed record Ref(string? Scalar, Type? Clr, bool IsEnum, bool IsAny, Synth? Synthetic = null);

internal sealed class Field
{
    public required string Name;
    public required int Number;
    public bool Repeated;
    public bool Optional;
    public Ref? Element;
    public FieldInfo? Info;
    public string? Path; // a derived field: the member path native reads, not a CLR field
    public string? DefRef; // a field of defNames (single or repeated): the Def class it references
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
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private readonly MetadataLoadContext context;
    private readonly string managed;
    private readonly Dictionary<Type, List<Type>> subclasses = new();
    private readonly Dictionary<Type, Message> messages = new();
    private readonly HashSet<Type> anys = new(); // classes with subclasses: a "<Class>Any" wrapper
    private readonly HashSet<Type> emptyAbstract = new();
    private readonly HashSet<Type> enums = new();
    private readonly HashSet<Synth> synths = new();
    private readonly SortedSet<string> errors = new(StringComparer.Ordinal);
    private readonly SortedSet<string> skipped = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, Message> holders = new(); // classes holding carried static members (GameConstants)
    private readonly SortedSet<string> unrepresented = new(StringComparer.Ordinal);
    private Dictionary<Type, string> names = new();
    private Dictionary<Type, string> holderStems = new(); // a holder's root field name; its message is <stem>Constants
    private List<Type> roots = new();

    // The namespaces whose static members GameConstants carries. Widening is an edit here.
    private static readonly string[] ConstantNamespaces =
        { "RimWorld", "Verse", "Verse.AI", "Verse.AI.Group", "RimWorld.Planet", "RimWorld.QuestGen", "RimWorld.BaseGen" };

    public Generator(MetadataLoadContext context, string managed)
    {
        this.context = context;
        this.managed = managed;
    }

    public IReadOnlyCollection<string> Errors => errors;
    public int MessageCount => messages.Count + anys.Count + synths.Count + holders.Count + 1;
    public int EnumCount => enums.Count;
    public int SkippedCount => skipped.Count;
    public int HolderCount => holders.Count;
    public int ConstantFieldCount => holders.Values.Sum(h => h.Fields.Count);
    public int UnrepresentedCount => unrepresented.Count;

    // DefinitionCatalog carries these two roots in its own fields (8 and 9);
    // every other root is a repeated field of DefSets. They walk first.
    private static readonly string[] CarriedRoots = { "Verse.ThingDef", "Verse.TerrainDef" };

    public string Run()
    {
        var assemblies = Prepare();
        foreach (var root in roots) Need(root);
        CollectConstants(assemblies);
        if (errors.Count > 0) return "";
        AssignNames();
        if (errors.Count > 0) return "";
        var text = Emit(assemblies);
        return errors.Count > 0 ? "" : text;
    }

    private static readonly HashSet<string> GameplayNamespaces = new()
    {
        "RimWorld", "Verse", "Verse.AI", "RimWorld.Planet", "RimWorld.QuestGen", "Verse.AI.Group",
    };

    // The audit behind cmd/catalogaudit: one tab-separated line per game member
    // outside the def rows. "const" is a const or static readonly scalar or enum
    // and "curve" a static SimpleCurve, both in the gameplay namespaces of
    // Assembly-CSharp; "unsaved" is an [Unsaved] data field (not runtime state)
    // declared by a Verse.Def class. Columns: kind, declaring class, member.
    public string Report()
    {
        var assemblies = Prepare();
        var sb = new StringBuilder();
        foreach (var type in assemblies[0].GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.IsGenericTypeDefinition || type.FullName!.Contains('<')) continue;
            const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var f in type.GetFields(all).OrderBy(f => f.MetadataToken))
            {
                if (f.Name.Contains('<')) continue;
                if (f.IsStatic && GameplayNamespaces.Contains(type.Namespace ?? ""))
                {
                    if (f.FieldType.FullName == "Verse.SimpleCurve") sb.Append($"curve\t{type.FullName}\t{f.Name}\n");
                    else if ((f.IsLiteral || f.IsInitOnly) && (f.FieldType.IsEnum || ScalarOf(f.FieldType) != null && f.FieldType.FullName != "System.Type"))
                        sb.Append($"const\t{type.FullName}\t{f.Name}\n");
                }
                else if (!f.IsStatic && IsDef(type) && HasUnsaved(f) && RuntimeState(f.FieldType) == null)
                    sb.Append($"unsaved\t{type.FullName}\t{f.Name}\n");
            }
        }
        return sb.ToString();
    }

    private Assembly[] Prepare()
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
        // Every concrete Verse.Def class is a root; the abstract ones are reached
        // through their subclasses' bases only.
        var defs = assemblies.SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition && IsDef(t) && t.FullName != "Verse.Def")
            .OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
        foreach (var carried in CarriedRoots)
            if (!defs.Any(t => t.FullName == carried)) throw new InvalidOperationException($"{carried} not found in {string.Join(", ", DefAssemblies)}");
        roots = defs.OrderBy(t => Array.IndexOf(CarriedRoots, t.FullName) is var i && i >= 0 ? i : CarriedRoots.Length)
            .ThenBy(t => t.FullName, StringComparer.Ordinal).ToList();
        publicReach = PublicReach();
        return assemblies;
    }

    // ---- type mapping ----

    private static bool IsDef(Type t)
    {
        for (var b = t; b != null; b = b.BaseType)
            if (b.FullName == "Verse.Def") return true;
        return false;
    }

    // RimWorld.QuestGen.SlateRef<T>: a struct whose only data is the private
    // string slateRef, the field's XML text (a literal or a "$variable").
    private static bool IsSlateRef(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition().FullName == "RimWorld.QuestGen.SlateRef`1";

    private static bool IsNullable(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1";

    private static bool IsCollection(Type t) =>
        t.IsArray || (t.IsGenericType && t.GetGenericTypeDefinition().FullName is
            "System.Collections.Generic.List`1" or "System.Collections.Generic.HashSet`1" or "System.Collections.Generic.Dictionary`2");

    // The first type in t (itself, an array element or a generic argument) that
    // is runtime state: a Verse.Entity, a Verse.ILoadReferenceable, a delegate,
    // an interface, System.Object (untyped), a UnityEngine.Object (Material, Texture, ...)
    // or any other class outside the game's def assemblies (BCL, Unity, Steam).
    private Type? RuntimeState(Type t)
    {
        if (t.IsArray) return RuntimeState(t.GetElementType()!);
        if (t.IsInterface) return t;
        if (t.FullName == "System.Object") return t; // untyped: no message can hold it
        if (IsSlateRef(t)) return null; // its XML text, whatever it is typed to hold
        for (var b = t; b != null; b = b.BaseType)
            if (b.FullName is "Verse.Entity" or "System.MulticastDelegate" or "System.Delegate" or "UnityEngine.Object") return t;
        // A class of the BCL, Unity or Steam (a MaterialPropertyBlock, a StringBuilder): the game's def data never uses one.
        if (!t.IsGenericType && !t.IsValueType && ScalarOf(t) == null && !DefAssemblies.Contains(t.Assembly.GetName().Name)) return t;
        // A class the loader cannot build, or one named in RuntimeClasses: not def data.
        if (!t.IsGenericType && !IsDef(t) && (InDefAssembly(t) && !Loadable(t) || InRuntimeClasses(t))) return t;
        if (!InDataClasses(t) && (t.FullName == "Verse.ILoadReferenceable" || t.GetInterfaces().Any(i => i.FullName == "Verse.ILoadReferenceable"))) return t;
        if (t.IsGenericType)
            foreach (var a in t.GetGenericArguments())
                if (RuntimeState(a) is { } inner) return inner;
        return null;
    }

    // Classes the loader can build: a concrete class with a public parameterless
    // constructor, or a base class with such a subclass. A worker or handler built
    // with its def or map as a constructor argument is runtime state.
    private readonly Dictionary<Type, bool> loadable = new();

    // Classes (and their subclasses) with no structural mark that are runtime
    // state anyway, each with its reason. See the defs.proto header.
    private static readonly SortedDictionary<string, string> RuntimeClasses = new(StringComparer.Ordinal)
    {
        ["RimWorld.Alert"] = "an alert instance a scenario part caches; the game builds it, XML never names one",
        ["RimWorld.BossgroupWorker"] = "worker built from BossgroupDef.workerClass; no data of its own",
        ["RimWorld.Difficulty"] = "the saved game's difficulty settings, cached by CostListForDifficulty",
        ["RimWorld.Planet.SitePart"] = "a world site's part, created when a site spawns (GenStepParams.sitePart)",
        ["RimWorld.Reward"] = "a quest reward generated at quest time (QuestNode_GiveRewards.generatedRewards)",
        ["RimWorld.RoyalTitleInheritanceWorker"] = "worker built from RoyalTitleDef.inheritanceWorkerOverrideClass; no data of its own",
        ["RimWorld.SketchThing"] = "a thing of the sketch being resolved (SketchResolver scratch collections)",
        ["Steamworks.PublishedFileId_t"] = "the workshop id assigned at upload, not read from XML (Scenario.publishedFileIdInt)",
        ["Verse.District"] = "a computed map district; no def data",
        ["Verse.GenStepParams"] = "the parameters of the map generation in progress (GenStep_ConditionCauser.currentParams)",
        ["Verse.PawnRenderSubWorker"] = "worker built from PawnRenderNodeProperties.subworkerClasses; no data of its own",
        ["Verse.Room"] = "a computed map room; no def data",
    };

    // Classes (and their subclasses) that implement Verse.ILoadReferenceable and are def
    // data anyway: XML builds them, the interface only lets a lord job save its copy.
    private static readonly SortedDictionary<string, string> DataClasses = new(StringComparer.Ordinal)
    {
        ["RimWorld.RitualRole"] = "the role slots XML names in RitualBehaviorDef.roles; ILoadReferenceable only so a ritual's lord job can save its copy",
    };

    private static bool InDataClasses(Type t)
    {
        for (var b = t; b != null; b = b.BaseType)
            if (b.FullName != null && DataClasses.ContainsKey(b.FullName)) return true;
        return false;
    }

    private bool Loadable(Type t)
    {
        if (t.IsValueType) return true;
        if (loadable.TryGetValue(t, out var known)) return known;
        var has = subclasses.TryGetValue(t, out var subs);
        // An abstract class nothing concrete in the game extends (ResearchMod, DefModExtension)
        // is an extension point a mod fills, so XML can name it.
        var ok = Instantiable(t) || (has && subs!.Any(Loadable)) || (t.IsAbstract && (!has || subs!.All(s => s.IsAbstract)));
        return loadable[t] = ok;
    }

    // The non-def classes and structs the roots reach through public fields alone
    // (a collection element, a generic argument and every mirrored subclass included).
    private HashSet<Type> publicReach = new();

    private HashSet<Type> PublicReach()
    {
        var reach = new HashSet<Type>();
        var queue = new Queue<Type>();
        void Visit(Type t)
        {
            if (t.IsArray) { Visit(t.GetElementType()!); return; }
            if (t.IsGenericType) { foreach (var a in t.GetGenericArguments()) Visit(a); return; }
            if (t.IsEnum || IsDef(t) || !InDefAssembly(t) || RuntimeState(t) != null || !reach.Add(t)) return;
            queue.Enqueue(t);
            foreach (var s in MirroredSubclasses(t)) Visit(s);
        }
        foreach (var root in roots)
        {
            queue.Enqueue(root);
            foreach (var s in MirroredSubclasses(root)) queue.Enqueue(s);
        }
        while (queue.Count > 0)
            foreach (var f in DataFields(queue.Dequeue()))
                Visit(f.FieldType);
        return reach;
    }

    // A non-public field holding one instance of a class family (abstract, or with
    // mirrored subclasses) that no public field reaches: a lazily built worker, graphic
    // or noise module that the game creates from a Type field, which XML never fills.
    // Data held through a non-public field is a collection or a struct
    // (SimpleCurve.points, ThingFilter.allowedQualities) or of a class XML names publicly.
    private bool LazyInstance(FieldInfo f)
    {
        var t = f.FieldType;
        return !f.IsPublic && !t.IsValueType && !t.IsGenericType && !t.IsArray && !IsDef(t) && InDefAssembly(t)
            && (t.IsAbstract || MirroredSubclasses(t).Any()) && !publicReach.Contains(t);
    }

    private bool Instantiable(Type t) =>
        t.IsValueType || (!t.IsAbstract && t.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Any(c => c.GetParameters().Length == 0));

    private bool IsMirrored(Type t)
    {
        return Loadable(t) && !InRuntimeClasses(t);
    }

    private static bool InRuntimeClasses(Type t)
    {
        for (var b = t; b != null; b = b.BaseType)
            if (b.FullName != null && RuntimeClasses.ContainsKey(b.FullName)) return true;
        return false;
    }

    private IEnumerable<Type> MirroredSubclasses(Type t) =>
        subclasses.TryGetValue(t, out var subs) ? subs.Where(s => IsDef(s) || IsMirrored(s)) : Enumerable.Empty<Type>();

    private static string? ScalarOf(Type t) => t.FullName switch
    {
        "System.Boolean" => "bool",
        "System.SByte" or "System.Int16" or "System.Int32" => "int32",
        "System.Byte" or "System.UInt16" or "System.UInt32" => "uint32",
        "System.Char" => "uint32", // the UTF-16 code unit
        "System.Int64" => "int64",
        "System.UInt64" => "uint64",
        "System.Single" => "float",
        "System.Double" => "double",
        "System.String" => "string",
        "System.Type" => "string", // full name, resolved by the native fill
        _ => null,
    };

    private static bool InDefAssembly(Type t) => DefAssemblies.Contains(t.Assembly.GetName().Name);

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
        if (IsSlateRef(t)) return new Ref("string", null, false, false);
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
        var polymorphic = MirroredSubclasses(t).Any();
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
    // already begun is not built again, so a field that reaches an enclosing
    // class (a tree node holding its children) is a recursive message.
    private void Need(Type t)
    {
        if (messages.ContainsKey(t)) return;
        messages[t] = new Message { Clr = t };
        Build(t);
        if (t.IsClass && MirroredSubclasses(t).ToList() is { Count: > 0 } subs)
        {
            anys.Add(t);
            foreach (var s in subs) Need(s);
        }
    }

    private static bool HasUnsaved(FieldInfo f) =>
        f.CustomAttributes.Any(a => a.AttributeType.Name == "UnsavedAttribute");

    private static List<FieldInfo> DataFields(Type t, bool withNonPublic = false)
    {
        var chain = new List<Type>();
        for (var c = t; c != null && c.FullName is not ("System.Object" or "System.ValueType"); c = c.BaseType)
            chain.Insert(0, c);
        var fields = new List<FieldInfo>();
        foreach (var c in chain)
            foreach (var f in c.GetFields(InstanceFields).OrderBy(f => f.MetadataToken))
            {
                if (f.IsStatic || f.IsLiteral || HasUnsaved(f) || (!f.IsPublic && !withNonPublic)) continue;
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
        // An abstract class with no concrete subclass is never instantiated: its non-public fields are not data.
        foreach (var f in DataFields(t, !t.IsAbstract || MirroredSubclasses(t).Any()))
        {
            var where = $"{f.DeclaringType!.FullName}.{f.Name}";
            if (RuntimeState(f.FieldType) is { } state)
            {
                skipped.Add($"{where}: {TypeText(f.FieldType)} (runtime state {TypeText(state)})");
                continue;
            }
            if (LazyInstance(f))
            {
                skipped.Add($"{where}: {TypeText(f.FieldType)} (runtime state: a lazily built instance of a class family)");
                continue;
            }
            var key = char.ToUpperInvariant(f.Name[0]) + f.Name.Substring(1).Replace("_", "");
            if (keys.TryGetValue(key, out var other))
            {
                errors.Add($"{where}: name collides with {other} after protobuf name normalisation");
                continue;
            }
            var enumsBefore = new HashSet<Type>(enums);
            var synthsBefore = new HashSet<Synth>(synths);
            try
            {
                var field = new Field { Name = f.Name, Number = number + 1, Info = f };
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
                    var args = ft.IsArray ? new[] { ft.GetElementType()! } : ft.GetGenericArguments();
                    if (args.Length == 1 && IsDef(args[0])) field.DefRef = args[0].FullName;
                }
                else
                {
                    field.Element = Resolve(ft);
                    if (IsDef(ft)) field.DefRef = ft.FullName;
                }
                message.Fields.Add(field);
                keys[key] = f.Name;
                number++;
            }
            catch (Unsupported u)
            {
                if (f.IsPublic)
                {
                    errors.Add($"{where}: {u.Message}");
                    continue;
                }
                // A non-public field of a type the mirror cannot hold is not def
                // data either; nothing of it stays in the schema.
                skipped.Add($"{where}: {TypeText(f.FieldType)} (runtime state: a non-public field of a type no message can hold, {u.Message})");
                enums.IntersectWith(enumsBefore);
                synths.IntersectWith(synthsBefore);
            }
        }
        // Every Def row names the mod that defined it: Def.modContentPack is
        // [Unsaved], so the field is derived, not reflected.
        if (IsDef(t))
        {
            if (keys.ContainsKey(DerivedModKey)) errors.Add($"{t.FullName}: modPackageId collides with a field");
            message.Fields.Add(new Field { Name = "modPackageId", Number = number + 1, Optional = true, Element = new Ref("string", null, false, false), Path = "modContentPack.PackageId" });
        }
    }

    private const string DerivedModKey = "ModPackageId";
    // The repeated element of a collection: the item, or a key/value entry.
    private Ref CollectionElement(Type t)
    {
        if (t.IsArray)
        {
            if (t.GetArrayRank() != 1) throw new Unsupported($"{t} is a multidimensional array");
            return Slot(ResolveElement(t.GetElementType()!));
        }
        var args = t.GetGenericArguments();
        if (args.Length == 2) return Synthetic(new Synth(true, ResolveElement(args[0]), ResolveElement(args[1])));
        return Slot(ResolveElement(args[0]));
    }

    // A repeated field cannot hold an absent element, and the game uses null list
    // entries positionally, so an element that is a reference type mirrored as a
    // message (a class, a "<Class>Any" wrapper, a nested collection) is wrapped in
    // an Opt_ message whose message-typed value is unset for null. Structs, enums
    // and the scalar mappings (string, defName, Type) are not wrapped.
    private Ref Slot(Ref element)
    {
        var message = element.Synthetic is { Entry: false, Optional: false } || (element.Clr is { IsValueType: false } && !element.IsEnum);
        return message ? Synthetic(new Synth(false, null, element, true)) : element;
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

    // ---- game constants ----

    // UI and dev classes carry no gameplay constants; compiler-generated closures and
    // caches and the *DefOf classes (filled at load, not constants) are no data either.
    private static bool ExcludedClass(Type t)
    {
        for (var c = t; c != null; c = c.DeclaringType)
            if (c.Name.Contains('<') || c.Name.StartsWith("Dialog_", StringComparison.Ordinal) || c.Name is "Widgets" or "DevGUI"
                || c.Name.EndsWith("DefOf", StringComparison.Ordinal) || c.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute"))
                return true;
        return false;
    }

    private static bool InConstantNamespace(Type t)
    {
        var outer = t;
        while (outer.DeclaringType != null) outer = outer.DeclaringType;
        return outer.Namespace != null && ConstantNamespaces.Contains(outer.Namespace);
    }

    // Every const and static readonly of a primitive, string, enum or struct type the def
    // mapping can represent, per class; every enum of the namespaces. A member the mapping
    // cannot represent is listed with its reason; a public one of a type the mapping says
    // must be carried (a primitive, string, enum or struct) fails the run, as a def field does.
    private void CollectConstants(Assembly[] assemblies)
    {
        var types = assemblies.SelectMany(a => a.GetTypes())
            .Where(t => !t.IsGenericTypeDefinition && InConstantNamespace(t) && !ExcludedClass(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
        foreach (var t in types)
        {
            if (t.IsEnum) { enums.Add(t); continue; }
            var message = new Message { Clr = t };
            var keys = new Dictionary<string, string>();
            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).OrderBy(f => f.MetadataToken))
            {
                if (!(f.IsLiteral || f.IsInitOnly) || f.Name.Contains('<')) continue;
                var where = $"{t.FullName}.{f.Name}";
                var element = StaticElement(f, where, out var reason);
                if (element == null)
                {
                    unrepresented.Add($"{where}: {TypeText(f.FieldType)} ({reason})");
                    continue;
                }
                var key = char.ToUpperInvariant(f.Name[0]) + f.Name.Substring(1).Replace("_", "");
                if (keys.TryGetValue(key, out var other))
                {
                    errors.Add($"{where}: name collides with {other} after protobuf name normalisation");
                    continue;
                }
                keys[key] = f.Name;
                message.Fields.Add(new Field { Name = f.Name, Number = message.Fields.Count + 1, Info = f, Element = element });
            }
            if (message.Fields.Count > 0) holders[t] = message;
        }
    }

    private Ref? StaticElement(FieldInfo f, string where, out string reason)
    {
        var t = f.FieldType;
        reason = "";
        if (RuntimeState(t) is { } state) { reason = $"runtime state {TypeText(state)}"; return null; }
        var carried = t.IsPrimitive || t.IsEnum || t.FullName == "System.String" || (t.IsValueType && !t.FullName!.StartsWith("System.", StringComparison.Ordinal));
        if (!carried || t.FullName == "System.Type")
        {
            reason = "not a primitive, string, enum or struct: curves, arrays and collections are not carried yet";
            return null;
        }
        try { return Resolve(t); }
        catch (Unsupported u)
        {
            reason = u.Message;
            if (f.IsPublic) errors.Add($"{where}: {u.Message}");
            return null;
        }
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
        holderStems = new Dictionary<Type, string>();
        foreach (var g in holders.Keys.GroupBy(SimpleName))
            foreach (var t in g)
                holderStems[t] = g.Count() == 1 ? g.Key : (t.Namespace ?? "").Replace('.', '_') + "_" + g.Key;
        foreach (var (t, stem) in holderStems)
        {
            var n = stem + "Constants";
            if (used.TryGetValue(n, out var other)) errors.Add($"{t.FullName}: message name {n} collides with {other.FullName}");
            used[n] = t;
        }
        if (used.ContainsKey("GameConstants")) errors.Add("message name GameConstants is taken");
        foreach (var t in anys)
            if (used.ContainsKey(names[t] + "Any")) errors.Add($"{t.FullName}: wrapper name {names[t]}Any is taken");
    }

    private string Part(Ref r) =>
        r.Synthetic != null ? SynthName(r.Synthetic) : r.Scalar ?? names[r.Clr!] + (r.IsAny ? "Any" : "");

    private string SynthName(Synth s) =>
        s.Entry ? "Entry_" + Part(s.Key!) + "_" + Part(s.Value) : (s.Optional ? "Opt_" : "List_") + Part(s.Value);

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
        L("// Instance fields of any visibility, minus [Unsaved] fields: the game's XML loader fills");
        L("// private fields as well as public ones (it looks a field up by name on the class and");
        L("// its bases, whatever its visibility). A non-public field of a type no message can hold is");
        L("// runtime state (a cache, a scratch buffer) and is listed below; a public one fails the run.");
        L("// Reference cycles are mirrored as recursive messages (a tree node holds its");
        L("// children). Every Def message ends with a derived field, modPackageId, read from");
        L("// Def.modContentPack.PackageId (the field is [Unsaved]); its clr_path option names");
        L("// the member path native reads. It is unset for a def with no mod. Def references are the");
        L("// defName, System.Type is its full name, Nullable<T> is optional, a Dictionary is");
        L("// repeated Entry_ messages (key, value), a collection nested in a collection is a");
        L("// List_ message (items). A repeated element that is a class mirrored as a message is an");
        L("// Opt_ message (value, unset for a null element: the game uses null entries positionally);");
        L("// structs, enums and string-mapped elements (defName, Type) are not wrapped. All three");
        L("// carry the clr_synthetic option.");
        L("//");
        L("// Runtime classes no structural rule separates (with their subclasses), the generator's");
        L("// RuntimeClasses table; a field of one is skipped like any other runtime state:");
        foreach (var (name, reason) in RuntimeClasses) L("//   " + name + ": " + reason);
        L("//");
        L("// Classes that implement Verse.ILoadReferenceable and are def data anyway (with their subclasses),");
        L("// the generator's DataClasses table:");
        foreach (var (name, reason) in DataClasses) L("//   " + name + ": " + reason);
        L("//");
        L("// Abstract classes without fields or concrete subclass (empty messages):");
        foreach (var t in emptyAbstract.OrderBy(t => t.FullName, StringComparer.Ordinal)) L("//   " + t.FullName);
        L("//");
        L("// Fields skipped (runtime state, not def data): a type deriving from Verse.Entity or");
        L("// UnityEngine.Object, implementing Verse.ILoadReferenceable (but a DataClasses class), a delegate, an interface or System.Object (untyped),");
        L("// alone or as a collection element or generic argument; a class the loader cannot build, one in the");
        L("// RuntimeClasses table, or a lazily built instance of a class family held by a non-public field.");
        foreach (var s in skipped) L("//   " + s);
        L("//");
        L("// GameConstants: one <Class>Constants message per class or struct of the namespaces " + string.Join(", ", ConstantNamespaces));
        L("// that holds a carried static member, and the root GameConstants with one field per such class.");
        L("// A member is every const and static readonly of a primitive, string or enum type or of a struct the");
        L("// mapping above represents, whatever its visibility; field names are the CLR names. Every enum of the");
        L("// namespaces is emitted, reachable from a def field or not. Excluded: Dialog_* classes, Widgets, DevGUI,");
        L("// *DefOf classes and compiler-generated members. A member the mapping cannot represent is listed here with");
        L("// its reason; a public one of a primitive, string, enum or struct type fails the run instead.");
        L("// Members not carried (class-typed values such as curves, arrays and collections stay out until a later change):");
        foreach (var s in unrepresented) L("//   " + s);
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
        L("  // \"entry\" (a Dictionary entry: key, value), \"list\" (a nested collection: items)\n  // or \"optional\" (a possibly null repeated element: value).");
        L("  string clr_synthetic = 50101;");
        L("}");
        L();
        L("extend google.protobuf.FieldOptions {");
        L("  // A derived field: not a CLR field of the class but the value of this member path\n  // (fields or properties, dot separated) read from the mirrored object; unset when a\n  // step is null.");
        L("  string clr_path = 50102;");
        L("  // A string field (or repeated string field) of defNames: the CLR full name of the Def class");
        L("  // each names. Dictionary keys and values and nested collections of defNames carry none.");
        L("  string clr_def_ref = 50103;");
        L("}");

        foreach (var e in enums.OrderBy(t => names[t], StringComparer.Ordinal))
        {
            var name = names[e];
            var prefix = UpperSnake(name);
            var members = new List<(string Name, long Value)>();
            foreach (var f in e.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                // Proto enums are int32: a uint enum member above int32.MaxValue (a flags "All") is its two's-complement int32.
                try { var raw = Convert.ToInt64(f.GetRawConstantValue()); members.Add((f.Name, e.GetEnumUnderlyingType().FullName == "System.UInt32" && raw > int.MaxValue ? unchecked((int)(uint)raw) : raw)); }
                catch (OverflowException) { errors.Add($"{e.FullName}.{f.Name}: value outside int64"); }
            }
            L();
            L("// " + e.FullName + (e.CustomAttributes.Any(a => a.AttributeType.Name == "FlagsAttribute") ? " (flags: the field holds the bitmask)" : "") + (e.GetEnumUnderlyingType().FullName == "System.UInt32" && members.Any(m => m.Value < 0) ? " (uint: a member above int32 max is its two's-complement int32)" : ""));
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
                L($"  {(f.Repeated ? "repeated " : f.Optional ? "optional " : "")}{TypeName(f.Element!)} {f.Name} = {f.Number}{(f.Path != null ? " [(clr_path) = \"" + f.Path + "\"]" : f.DefRef != null ? " [(clr_def_ref) = \"" + f.DefRef + "\"]" : "")};");
            L("}");
        }

        foreach (var (t, m) in holders.OrderBy(p => holderStems[p.Key], StringComparer.Ordinal))
        {
            L();
            L("// " + t.FullName + ": its const and static readonly members");
            L("message " + holderStems[t] + "Constants {");
            L("  option (clr_type) = \"" + t.FullName + "\";");
            foreach (var f in m.Fields) L($"  {TypeName(f.Element!)} {f.Name} = {f.Number};");
            L("}");
        }

        L();
        L("// The static members of the game's classes, one field per class holding any.");
        L("message GameConstants {");
        var constant = 0;
        foreach (var (t, stem) in holderStems.OrderBy(p => p.Value, StringComparer.Ordinal))
            L($"  {stem}Constants {stem} = {++constant};");
        L("}");

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
            else if (s.Optional)
            {
                L("  option (clr_synthetic) = \"optional\";");
                L($"  {TypeName(s.Value)} value = 1;");
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
            var variants = new[] { t }.Concat(subclasses[t]).Where(v => Instantiable(v) && messages.ContainsKey(v))
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

        // One repeated field per root class but the carried two, in root order.
        L();
        L("// Every def of each root class the catalog has no field of its own for");
        L("// (DefinitionCatalog carries " + string.Join(" and ", CarriedRoots) + "): one repeated field per");
        L("// concrete Verse.Def class, numbered in root order (ordinal by full name).");
        L("message DefSets {");
        var set = 0;
        foreach (var t in roots.Where(t => !CarriedRoots.Contains(t.FullName)))
            L($"  repeated {names[t]} {UpperSnake(names[t]).ToLowerInvariant()}s = {++set};");
        L("}");
        return sb.ToString();
    }
}
