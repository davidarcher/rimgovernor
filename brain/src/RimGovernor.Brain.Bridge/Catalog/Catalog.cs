using RimWorld;
using Verse;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Bridge;

// The definition catalog (Go internal/bridge/catalog*.go, #3056): the static planning facts of every buildable
// or sowable definition and every research project. They hold for the whole game process. The catalog is built
// over the game's own Def objects (a bare host constructs them, see the conventions doc), so it needs no wire
// rows, class-chain tables or fingerprints: a class test is typeof(X).IsAssignableFrom, a comp is
// GetCompProperties<T>, the game's constants are the game's own members. Stat values that depend on world state
// are read through IStatSource. One partial file per Go file.

/// <summary>A catalog invariant failed: a def the catalog needs is absent or states something it must not (Go's <c>contract()</c> error).</summary>
public sealed class CatalogException(string message) : DomainException(message);

/// <summary>The three facts the game names or computes in code (Go <c>CatalogDerived</c>): the currency def, the wort def and the full rot rate.</summary>
public sealed record CatalogDerived(string CurrencyDef, string WortDef, float FullRotRateC)
{
    /// <summary>Refuses an empty def name and a non-finite or non-positive rot rate (Go <c>validateDerived</c>).</summary>
    public void Validate()
    {
        if (CurrencyDef.Length == 0)
        {
            throw new CatalogException("catalog derived currency_def is empty");
        }

        if (WortDef.Length == 0)
        {
            throw new CatalogException("catalog derived wort_def is empty");
        }

        if (!float.IsFinite(FullRotRateC) || FullRotRateC <= 0)
        {
            throw new CatalogException($"catalog derived full_rot_rate_c is {FullRotRateC}");
        }
    }
}

/// <summary>
/// What the game's own race code says about one race that its def fields do not (Go <c>RaceFacts</c>): the
/// foods <c>RaceProperties.CanEverEat</c> accepts, the taming facts and the adult feed figure. They come from the
/// game thread (#3080) with the stat service.
/// </summary>
public sealed record RaceGameFacts(IReadOnlyList<string> EdibleDefs, bool TamenessCanDecay, int TamenessDecayPeriodTicks, float TameChanceFactor, double? AdultFeedPerDay);

/// <summary>One definition catalog over a game's Def objects.</summary>
public sealed partial class DefinitionCatalog
{
    private readonly Dictionary<string, ThingDef> _thingDefs = [];
    private readonly Dictionary<string, TerrainDef> _terrainDefs = [];
    private readonly Dictionary<Type, Dictionary<string, Def>> _rows = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object> _rowViews = new();
    private readonly IReadOnlyDictionary<string, RaceGameFacts> _raceFacts;
    private readonly Lazy<P.ItemFacts> _items;
    private readonly Lazy<IReadOnlyDictionary<string, P.PowerSourceProfile>> _powerSources;
    private readonly Lazy<IReadOnlyDictionary<string, P.FloorTerrain>> _floorTerrains;
    private readonly Lazy<P.PieceShapes> _shapes;
    private readonly Lazy<P.StationCatalog> _stations;
    private readonly Lazy<P.AnimalRaceCatalog> _races;
    private readonly Lazy<IReadOnlyList<P.Food>> _foods;
    private readonly Lazy<P.IdeologyDefs?> _ideology;
    private readonly Lazy<P.CreepJoinerDisarm> _disarm;
    private readonly Lazy<IReadOnlySet<string>> _spawnForbidden;
    private readonly Lazy<IReadOnlyDictionary<string, P.ResearchProjectFacts>> _research;

    /// <summary>Builds the catalog over <paramref name="defs"/> (every Def of the game); duplicate def names of one class refuse.</summary>
    public DefinitionCatalog(IEnumerable<Def> defs, IStatSource stats, CatalogDerived derived, IReadOnlyDictionary<string, RaceGameFacts>? raceFacts = null)
    {
        Stats = stats;
        Derived = derived;
        derived.Validate();
        _raceFacts = raceFacts ?? new Dictionary<string, RaceGameFacts>();
        foreach (var def in defs)
        {
            Add(def);
        }

        if (_thingDefs.Count == 0)
        {
            throw new CatalogException("catalog carries no thing defs");
        }

        if (_terrainDefs.Count == 0)
        {
            throw new CatalogException("catalog carries no terrain defs");
        }

        if (!_thingDefs.ContainsKey(derived.CurrencyDef))
        {
            throw new CatalogException($"catalog has no def row for the currency {derived.CurrencyDef}");
        }

        _items = new(BuildItemFacts);
        _powerSources = new(BuildPowerSources);
        _floorTerrains = new(BuildFloorTerrains);
        _shapes = new(BuildPieceShapes);
        _stations = new(BuildStationCatalog);
        _races = new(BuildAnimalRaces);
        _foods = new(BuildFoods);
        _ideology = new(BuildIdeologyDefs);
        _disarm = new(BuildCreepJoinerDisarm);
        _spawnForbidden = new(BuildSpawnForbiddenProducts);
        _research = new(BuildResearchFacts);
        _biotech = new(BuildBiotech);
    }

    /// <summary>The stat source every stat read goes through.</summary>
    public IStatSource Stats { get; }

    /// <summary>The three facts the game computes in code.</summary>
    public CatalogDerived Derived { get; }

    /// <summary>Every ThingDef by defName.</summary>
    public IReadOnlyDictionary<string, ThingDef> ThingDefs => _thingDefs;

    /// <summary>Every TerrainDef by defName.</summary>
    public IReadOnlyDictionary<string, TerrainDef> TerrainDefs => _terrainDefs;

    /// <summary>Every research project's static facts by name (Go <c>Research</c>).</summary>
    public IReadOnlyDictionary<string, P.ResearchProjectFacts> Research => _research.Value;

    private void Add(Def def)
    {
        if (string.IsNullOrWhiteSpace(def.defName))
        {
            throw new CatalogException("invalid catalog def");
        }

        var kind = def.GetType();
        if (!_rows.TryGetValue(kind, out var rows))
        {
            _rows[kind] = rows = [];
        }

        if (!rows.TryAdd(def.defName, def))
        {
            throw new CatalogException($"duplicate catalog {kind.Name} def {def.defName}");
        }

        switch (def)
        {
            case ThingDef thing:
                _thingDefs[thing.defName] = thing;
                break;
            case TerrainDef terrain:
                _terrainDefs[terrain.defName] = terrain;
                break;
        }
    }

    /// <summary>The rows of Def class <typeparamref name="T"/> (and its subclasses) by defName.</summary>
    public IReadOnlyDictionary<string, T> Rows<T>()
        where T : Def
    {
        return (IReadOnlyDictionary<string, T>)_rowViews.GetOrAdd(typeof(T), static (_, catalog) =>
        {
            var found = new Dictionary<string, T>();
            foreach (var (kind, rows) in catalog._rows)
            {
                if (!typeof(T).IsAssignableFrom(kind))
                {
                    continue;
                }

                foreach (var (name, row) in rows)
                {
                    found[name] = (T)row;
                }
            }

            return found;
        }, this);
    }

    /// <summary>name's row of Def class <typeparamref name="T"/>, null when the catalog has none (Go <c>DefRow</c>).</summary>
    public T? DefRow<T>(string name)
        where T : Def
    {
        foreach (var (kind, rows) in _rows)
        {
            if (typeof(T).IsAssignableFrom(kind) && rows.TryGetValue(name, out var row))
            {
                return (T)row;
            }
        }

        return null;
    }

    /// <summary>name's row of class <typeparamref name="T"/>; absence is a <see cref="CatalogException"/>.</summary>
    public T RequireRow<T>(string name)
        where T : Def =>
        DefRow<T>(name) ?? throw new CatalogException($"catalog has no {typeof(T).Name} {name}");

    /// <summary>name's ThingDef, null when the catalog has none (Go <c>ThingDef</c> accessor).</summary>
    public ThingDef? FindThing(string name) => _thingDefs.GetValueOrDefault(name);

    /// <summary>name's TerrainDef, null when the catalog has none.</summary>
    public TerrainDef? FindTerrain(string name) => _terrainDefs.GetValueOrDefault(name);

    /// <summary>name's ThingDef; a def without a row is a contract error, never a default (Go <c>thingRow</c>).</summary>
    public ThingDef ThingRow(string name) =>
        _thingDefs.GetValueOrDefault(name) ?? throw new CatalogException($"catalog has no def row for {name}");

    /// <summary>The building defs that pack into a minified item: those with a minifiedDef, which vanilla uninstalls instead of destroying.</summary>
    public IReadOnlySet<string> Packable() =>
        _thingDefs.Values.Where(d => d.category == ThingCategory.Building && d.minifiedDef is not null).Select(d => d.defName).ToHashSet(StringComparer.Ordinal);

    /// <summary>The sowable plants that harvest a thing, sorted: the crops a resource deficit can be planted for, whatever they are eaten as.</summary>
    public IReadOnlyList<string> SowableCrops() =>
        [.. _thingDefs.Values.Where(d => d.plant is { sowTags.Count: > 0, harvestedThingDef: not null }).Select(d => d.defName).Order(StringComparer.Ordinal)];

    /// <summary>The raw-ingredient products a spawner makes forbidden on purpose (<c>CompProperties_Spawner.spawnForbidden</c>): a hive's insect jelly.</summary>
    public IReadOnlySet<string> SpawnForbiddenProducts() => _spawnForbidden.Value;

    private HashSet<string> BuildSpawnForbiddenProducts() =>
        _thingDefs.Values
            .Select(d => d.GetCompProperties<CompProperties_Spawner>())
            .Where(s => s is { spawnForbidden: true, thingToSpawn: not null })
            .Select(s => s!.thingToSpawn.defName)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The first definition-level float that is finite; a non-finite number is a contract error.</summary>
    internal static float Finite(float value, string what) =>
        float.IsFinite(value) ? value : throw new CatalogException($"nonfinite {what} number");
}
