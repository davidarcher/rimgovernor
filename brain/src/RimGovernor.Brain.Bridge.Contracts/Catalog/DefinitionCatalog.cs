using Google.Protobuf;
using RimGovernor.Brain.Policy;
using RimGovernor.Protocol.Defs;
using RimGovernor.Protocol.Observations;
using DefsTerrainDef = RimGovernor.Protocol.Defs.TerrainDef;
using DefsThingDef = RimGovernor.Protocol.Defs.ThingDef;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>DefinitionCatalog</c>: one load's decoded catalog, the static planning facts of every buildable or sowable
/// definition and every research project. They hold for the whole game process. This type is the data shape only;
/// the derivations Go hung on the type (foods, power sources, stations, recipes, stat evaluation, ...) belong to
/// the catalog port (#3056) and extend it from outside, with their own per-catalog caches.
/// The stat evaluator Go kept inside (<c>stateval</c>) is replaced by <see cref="IStatSource"/>, not ported.
/// </summary>
public sealed class DefinitionCatalog
{
    public required string LoadToken { get; init; }

    /// <summary>
    /// Names the static part (everything but <see cref="LoadToken"/> and the stat environment); the next load under the
    /// same fingerprint reuses it. Empty in a hand-built catalog.
    /// </summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>The wire Biotech facts, kept to rebuild <see cref="Biotech"/> for a load; null without Biotech.</summary>
    public Protocol.Observations.BiotechCatalog? BiotechWire { get; init; }

    /// <summary>Every research project's static facts by name.</summary>
    public IReadOnlyDictionary<string, ResearchProjectFacts> Research { get; init; } = new Dictionary<string, ResearchProjectFacts>(StringComparer.Ordinal);

    /// <summary>The Biotech defs; null without Biotech.</summary>
    public BiotechCatalog? Biotech { get; init; }

    /// <summary>Every ThingDef with all its fields by defName: the generated messages of defs.proto, unfiltered.</summary>
    public IReadOnlyDictionary<string, DefsThingDef> ThingDefs { get; init; } = new Dictionary<string, DefsThingDef>(StringComparer.Ordinal);

    /// <summary>Every TerrainDef with all its fields by defName.</summary>
    public IReadOnlyDictionary<string, DefsTerrainDef> TerrainDefs { get; init; } = new Dictionary<string, DefsTerrainDef>(StringComparer.Ordinal);

    /// <summary>
    /// The defs of every other concrete Def class: the generated rows of defs.proto's DefSets by message full name,
    /// then defName. Read them with <see cref="DefRow{T}"/>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IMessage>> Defs { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, IMessage>>(StringComparer.Ordinal);

    /// <summary>Each class the rows name by CLR full name with its base classes, nearest first.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ClassBases { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>The three facts the game names or computes in code (currency def, wort def, full rot rate); the numeric constants are <see cref="GameConstants"/>.</summary>
    public CatalogDerived? Derived { get; init; }

    /// <summary>The const and static readonly members of the game classes, typed per class; null only in a hand-built catalog.</summary>
    public GameConstants? GameConstants { get; init; }

    /// <summary>The game-computed flags of every ThingDef by name.</summary>
    public IReadOnlyDictionary<string, ThingDefFacts> ThingFacts { get; init; } = new Dictionary<string, ThingDefFacts>(StringComparer.Ordinal);

    /// <summary>The rows of one other concrete Def class by defName (Go's <c>Rows</c>); empty when the catalog has none.</summary>
    public IReadOnlyDictionary<string, IMessage> Rows(string classFullName) =>
        Defs.TryGetValue(classFullName, out var rows) ? rows : EmptyRows;

    /// <summary>The generated ThingDef row of <paramref name="name"/>, null when the catalog has none.</summary>
    public DefsThingDef? ThingDef(string name) => ThingDefs.GetValueOrDefault(name);

    /// <summary>The generated TerrainDef row of <paramref name="name"/>, null when the catalog has none.</summary>
    public DefsTerrainDef? TerrainDef(string name) => TerrainDefs.GetValueOrDefault(name);

    /// <summary>The building defs that pack into a minified item: those with a minifiedDef, which vanilla uninstalls instead of destroying.</summary>
    public IReadOnlySet<string> Packable()
    {
        var packable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, def) in ThingDefs)
        {
            if (def.Category == ThingCategory.Building && def.MinifiedDef.Length != 0)
            {
                packable.Add(name);
            }
        }
        return packable;
    }

    /// <summary>
    /// The sowable plants that harvest a thing, sorted: the crops a resource deficit can be planted for, whatever they
    /// are eaten as.
    /// </summary>
    public IReadOnlyList<string> SowableCrops() =>
        [.. ThingDefs.Where(pair => pair.Value.Plant is { } plant && plant.SowTags.Count > 0 && plant.HarvestedThingDef.Length != 0)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)];

    private static readonly IReadOnlyDictionary<string, IMessage> EmptyRows = new Dictionary<string, IMessage>(StringComparer.Ordinal);

    /// <summary>The generated row of def class <typeparamref name="T"/> (a message of defs.proto other than ThingDef and TerrainDef), null when the catalog has none.</summary>
    public T? DefRow<T>(string name)
        where T : class, IMessage<T>, new() =>
        Rows(new T().Descriptor.FullName).GetValueOrDefault(name) as T;
}

/// <summary>Go's package-level <c>AllThingDefs</c>, <c>DefRow</c>, <c>Buildable</c> and <c>TerrainBuildable</c>.</summary>
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>DefRow[T](catalog, name)</c>, tolerating a null catalog.</summary>
    public static T? DefRow<T>(DefinitionCatalog? catalog, string name)
        where T : class, IMessage<T>, new() => catalog?.DefRow<T>(name);

    /// <summary>Go's <c>Buildable</c>: ThingDef.BuildableByPlayer, the def has a designation category.</summary>
    public static bool Buildable(ThingDef row) => row.DesignationCategory.Length != 0;

    /// <summary>Go's <c>TerrainBuildable</c>: BuildableDef.BuildableByPlayer of a TerrainDef.</summary>
    public static bool TerrainBuildable(TerrainDef row) => row.DesignationCategory.Length != 0;
}

/// <summary>
/// Go's <c>BiotechCatalog</c>: one load's Biotech facts, a facade over the def mirror. The gene, race and mech kind
/// defs are the catalog's own rows, and only the game's role picks and tuning constants come from the native Biotech
/// section. Null on <see cref="DefinitionCatalog.Biotech"/> without Biotech. Built by the catalog port (#3056).
/// </summary>
public sealed class BiotechCatalog
{
    /// <summary>The GeneDef rows by name.</summary>
    public IReadOnlyDictionary<string, GeneDef> Genes { get; init; } = new Dictionary<string, GeneDef>(StringComparer.Ordinal);

    /// <summary>The humanlike races' RaceProperties by ThingDef name.</summary>
    public IReadOnlyDictionary<string, RaceProperties> Races { get; init; } = new Dictionary<string, RaceProperties>(StringComparer.Ordinal);

    /// <summary>The controllable mech kinds by PawnKindDef name.</summary>
    public IReadOnlyDictionary<string, MechKindRow> MechKinds { get; init; } = new Dictionary<string, MechKindRow>(StringComparer.Ordinal);

    /// <summary>The MechWorkModeDef name the game picks for the work role.</summary>
    public string Work { get; init; } = "";

    /// <summary>The MechWorkModeDef name the game picks for the escort role.</summary>
    public string Escort { get; init; } = "";

    /// <summary>The MechWorkModeDef name the game picks for the recharge role.</summary>
    public string Recharge { get; init; } = "";

    /// <summary>The singleton of GeneTuning constants; null when the native did not send it.</summary>
    public GeneTuningFacts? GeneTuning { get; init; }
}

/// <summary>Go's <c>mechKindRow</c>: a controllable mech kind as the planner reads it.</summary>
public sealed record MechKindRow(string Race, bool WorkMech, double BandwidthCost, double CombatPower, EquatableArray<string> WorkTypes);
