using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RimGovernor.Brain.Analyzers;

/// <summary>
/// The planner worker thread never calls game APIs that touch world state (epic #3043, RGARCH007). Brain code may read
/// immutable Def data (Def classes and their fields, DefDatabase&lt;T&gt;, the *DefOf statics, constants, enums and
/// value types) and nothing that reads or holds live world state: the statics (Find, Current, Rand, GenTicks, GenDate, the
/// Gen* helpers, StatExtension...), live objects (Thing and its subclasses, Map, Game, World and WorldObjects, Faction,
/// the Pawn_* trackers, Lord and Job, Hediff, Need, Room...), StatWorker, and UnityEngine's main-thread API. World state
/// reaches the brain only as facts decoded from the wire; computed stats come from IStatSource.
///
/// The allowed side is a default: a game class that is none of the banned kinds is Def-adjacent data (GraphicData,
/// StatModifier, CompProperties...). Key "file|Type.Member|Namespace.Type" names the banned type that was used; the
/// project must reference the game assemblies (Game.props), and an unavailable build has nothing to judge.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WorldStateAnalyzer : GateAnalyzer
{
    private static readonly HashSet<string> GameAssemblies = new(StringComparer.Ordinal) { "Assembly-CSharp", "Waylith" };

    /// <summary>Static classes that touch no world state. Everything else static in a game assembly is banned.</summary>
    private static readonly HashSet<string> PureStatics = new(StringComparer.Ordinal)
    {
        "Verse.DefDatabase`1",
        "UnityEngine.Mathf",
    };

    /// <summary>Members of otherwise banned static classes that read nothing live (the allowlist of Def-data helpers).</summary>
    private static readonly HashSet<string> PureMembers = new(StringComparer.Ordinal)
    {
        // Any(List<T>, Predicate<T>): a list predicate helper. The rest of GenCollection (RandomElement...) draws on Rand.
        "Verse.GenCollection.Any",
    };

    /// <summary>
    /// Live-state class hierarchies. A class equal to or derived from one is banned; a Def (data) never derives from one.
    /// </summary>
    private static readonly HashSet<string> LiveRoots = new(StringComparer.Ordinal)
    {
        "Verse.Thing",
        "Verse.ThingComp",
        "Verse.Map",
        "Verse.MapComponent",
        "Verse.Game",
        "Verse.GameComponent",
        "Verse.TickManager",
        "Verse.Room",
        "Verse.RoomGroup",
        "Verse.Region",
        "Verse.Zone",
        "Verse.Hediff",
        "Verse.HediffSet",
        "Verse.Need",
        "Verse.Gene",
        "Verse.ThingOwner",
        "Verse.ListerThings",
        "RimWorld.Faction",
        "RimWorld.Storyteller",
        "RimWorld.StatWorker",
        "RimWorld.Ideo",
        "RimWorld.Precept",
        "RimWorld.Trait",
        "RimWorld.Planet.WorldComponent",
    };

    /// <summary>Namespaces whose classes are all live state (the AI and the world map); their Defs, enums and structs are still data.</summary>
    private static readonly string[] LiveNamespaces = { "Verse.AI", "RimWorld.Planet" };

    public WorldStateAnalyzer()
        : base(7, "No world-state game API on the worker thread", "world-state game API in brain code")
    {
    }

    protected override bool Applies(string shortName) => true;

    protected override void Register(CompilationStartAnalysisContext context, GateRun run)
    {
        if (!context.Compilation.ReferencedAssemblyNames.Any(r => GameAssemblies.Contains(r.Name)))
        {
            return;
        }

        context.RegisterSyntaxNodeAction(
            c => Check(c, run),
            SyntaxKind.IdentifierName,
            SyntaxKind.GenericName);
    }

    private static void Check(SyntaxNodeAnalysisContext context, GateRun run)
    {
        var node = context.Node;
        if (node is IdentifierNameSyntax { IsVar: true } || node.Parent is UsingDirectiveSyntax or NameEqualsSyntax)
        {
            return;
        }

        // typeof(Building_Bed) and CheapestOfClass<Building_Bed>() name a class token the Def's thingClass is compared
        // with; they call nothing.
        var key = Arch.FileKey(node.SyntaxTree, run.Assembly);
        if (key is null || node.Ancestors().Any(a => a is TypeOfExpressionSyntax or TypeArgumentListSyntax))
        {
            return;
        }

        var symbol = context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol;
        if (symbol is null || symbol is INamespaceSymbol or ILocalSymbol or IParameterSymbol or ITypeParameterSymbol)
        {
            return;
        }

        // In GenTicks.TicksPerRealSecond the member decides, not the class name in front of it.
        if (symbol is INamedTypeSymbol && Arch.IsQualifier(node))
        {
            return;
        }

        if (Banned(symbol) is { } banned)
        {
            run.Add(key + "|" + Arch.MemberName(node) + "|" + banned, node.GetLocation());
        }
    }


    /// <summary>The banned type a symbol of a game assembly reaches, or null when it is Def data or not the game's.</summary>
    internal static string? Banned(ISymbol symbol)
    {
        // A constant, or a static field of a plain value (the game's tuning numbers: GenTicks.TicksPerRealSecond,
        // TemperatureTuning.*), holds no world state. A static property or a field of a class type (Current.Game) does.
        if (symbol is IFieldSymbol { HasConstantValue: true }
            or IFieldSymbol { IsStatic: true, Type: { SpecialType: not SpecialType.None } or { TypeKind: TypeKind.Enum } })
        {
            return null;
        }

        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (type is null)
        {
            return null;
        }

        while (type.ContainingType is not null)
        {
            type = type.ContainingType;
        }

        type = type.OriginalDefinition;
        var assembly = type.ContainingAssembly?.Name ?? "";
        var game = GameAssemblies.Contains(assembly);
        var unity = assembly.StartsWith("Unity", StringComparison.Ordinal);
        if (!game && !unity)
        {
            return null;
        }

        var full = Full(type);
        if (type.TypeKind is TypeKind.Enum or TypeKind.Delegate or TypeKind.Interface or TypeKind.TypeParameter || type.IsValueType || Derives(type, "System.Attribute"))
        {
            return null;
        }

        if (PureStatics.Contains(full))
        {
            return null;
        }

        if (type.IsStatic)
        {
            if (type.Name.EndsWith("DefOf", StringComparison.Ordinal))
            {
                return null;
            }

            // The member names the call: a static class mixes pure helpers with world reads.
            var member = symbol is INamedTypeSymbol ? full : full + "." + symbol.Name;
            return PureMembers.Contains(member) ? null : member;
        }

        if (unity)
        {
            return full;
        }

        if (Derives(type, "Verse.Def"))
        {
            return null;
        }

        for (var t = type; t is not null; t = t.BaseType)
        {
            if (LiveRoots.Contains(Full(t)))
            {
                return full;
            }
        }

        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        if (LiveNamespaces.Any(l => ns == l || ns.StartsWith(l + ".", StringComparison.Ordinal)) || type.Name.StartsWith("Pawn_", StringComparison.Ordinal))
        {
            return full;
        }

        return null;
    }

    private static bool Derives(INamedTypeSymbol type, string fullName)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (Full(t) == fullName)
            {
                return true;
            }
        }

        return false;
    }

    private static string Full(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + type.MetadataName : type.MetadataName;
}
