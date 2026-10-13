using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Store;

/// <summary>
/// One serialisable placement of a stored layout tier (Go <c>DefenseBuilding</c>); <see cref="Building"/> would do,
/// but a tier is edited in place by the runtime, so the record carries plain fields.
/// </summary>
public sealed record DefenseBuilding
{
    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    public Rotation Rotation { get; init; }

    public string Stuff { get; init; } = "";
}

/// <summary>
/// One tier of the layout and what became of it. A tier cut from the layout plan's site nodes names them in
/// <see cref="Nodes"/> and stores no buildings: the plan owns the geometry and the runtime fills the buildings in
/// from it after every load.
/// </summary>
public sealed record DefenseTierRecord
{
    public DefenseTierName Name { get; set; }

    /// <summary>The plan's site nodes the tier is cut from.</summary>
    public List<string> Nodes { get; set; } = [];

    public List<DefenseBuilding> Buildings { get; set; } = [];

    /// <summary>The cells of a node-backed tier the game refused to build on.</summary>
    public List<Cell> Refused { get; set; } = [];

    public List<Cell> Reserved { get; set; } = [];

    /// <summary>The count of admitted methods for the tier; settled routine plans retire out of the goal's method list.</summary>
    public int Attempts { get; set; }

    /// <summary>Every building of the tier was observed standing in the latest census.</summary>
    public bool Built { get; set; }

    /// <summary>The times the census cleared <see cref="Built"/> after the tier stood.</summary>
    public int Reopened { get; set; }

    /// <summary>A perimeter tier of buildings the replanned ring no longer wants.</summary>
    public bool Remove { get; set; }

    public string Bridge { get; set; } = "";

    public bool Foundation { get; set; }

    public bool Before { get; set; }

    /// <summary>Whether the tier's buildings come from the plan.</summary>
    public bool NodeBacked => Nodes.Count > 0;
}

/// <summary>
/// The one layout the colony committed to for one world and episode of its standard (Go
/// <c>DefenseLayoutRecord</c>). The killbox it was proposed on is the plan's node, named by <see cref="Killbox"/>
/// with a digest of its footprint.
/// </summary>
public sealed record DefenseLayoutRecord
{
    private const int MaxIdentity = 256;

    public World World { get; set; }

    public StandardId Standard { get; set; }

    public ulong Episode { get; set; }

    public string Killbox { get; set; } = "";

    public string KillboxDigest { get; set; } = "";

    public List<Cell> TrapLane { get; set; } = [];

    public List<Cell> Firing { get; set; } = [];

    /// <summary>The inner line: <c>Retreat[i]</c> is <c>Firing[i]</c>'s fallback cell. May be empty.</summary>
    public List<Cell> Retreat { get; set; } = [];

    /// <summary>The colony doors whose access the audit keeps.</summary>
    public List<Cell> Entrances { get; set; } = [];

    public List<DefenseTierRecord> Tiers { get; set; } = [];

    public bool Complete { get; set; }

    public Tick VerifiedTick { get; set; }

    public string VerifiedCombat { get; set; } = "";

    public Tick TurretsProbedTick { get; set; }

    public string PerimeterKey { get; set; } = "";

    public int PerimeterRevision { get; set; }

    public string PerimeterBridge { get; set; } = "";

    private static bool ValidIdentity(string s) => s.Length is > 0 and <= MaxIdentity;

    /// <summary>Rejects a record that could not have come from a policy layout (Go <c>Validate</c>).</summary>
    public void Validate()
    {
        DomainException.Require(ValidIdentity(World.Colony.Value) && ValidIdentity(World.Load.Value) && World.Map.Value >= 0 && ValidIdentity(Standard.Value), "defense layout world or standard identity invalid");
        DomainException.Require(
            Firing.Count != 0 && Tiers.Count != 0 && Firing.Count <= 64 && (Retreat.Count == 0 || Retreat.Count == Firing.Count) && TrapLane.Count <= 512 && Entrances.Count <= 64,
            "defense layout geometry out of bounds"
        );
        DomainException.Require(
            VerifiedTick.Value >= 0 && TurretsProbedTick.Value >= 0 && VerifiedCombat.Length <= 512 && PerimeterKey.Length <= 128 && PerimeterRevision >= 0 && PerimeterBridge.Length <= 64 && Killbox.Length <= 128 && KillboxDigest.Length <= 64,
            "defense layout verification invalid"
        );
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tier in Tiers)
        {
            var ok =
                tier.Name.Value.Length > 0
                && seen.Add(tier.Name.Value)
                && (tier.NodeBacked || tier.Buildings.Count <= 512)
                && tier.Reserved.Count <= 512
                && tier.Nodes.Count <= 256
                && tier.Refused.Count <= 512
                && tier.Attempts is >= 0 and <= 64
                && tier.Reopened is >= 0 and <= 4096;
            DomainException.Require(ok, "defense layout tier invalid");
            foreach (var id in tier.Nodes)
            {
                DomainException.Require(ValidIdentity(id), "defense layout tier node invalid");
            }
            foreach (var b in tier.Buildings)
            {
                _ = new Building(b.Definition, b.Cell, b.Rotation, b.Stuff);
            }
        }
    }

    /// <summary>What the record holds of the plan's ring nodes: every node a tier names, and whether a perimeter removal is under way.</summary>
    public PerimeterHold PerimeterHold()
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        var removing = false;
        foreach (var t in Tiers)
        {
            held.UnionWith(t.Nodes);
            removing = removing || (t.Remove && PolicyFunctions.IsPerimeterTier(t.Name));
        }
        return new PerimeterHold { Held = held, Removing = removing };
    }

    /// <summary>The record as it is persisted: a node-backed tier carries no buildings, the plan holds them.</summary>
    internal DefenseLayoutRecord Stored() => this with { Tiers = [.. Tiers.Select(t => t.NodeBacked ? t with { Buildings = [] } : t)] };

    /// <summary>The named tier's buildings as domain placements; false when the record has no such tier or a building is invalid.</summary>
    public (DefenseTierRecord Tier, List<Building> Buildings, bool Found) Tier(DefenseTierName name)
    {
        foreach (var tier in Tiers)
        {
            if (tier.Name != name)
            {
                continue;
            }
            var output = new List<Building>(tier.Buildings.Count);
            try
            {
                output.AddRange(tier.Buildings.Select(b => new Building(b.Definition, b.Cell, b.Rotation, b.Stuff)));
            }
            catch (DomainException)
            {
                return (new DefenseTierRecord(), [], false);
            }
            return (tier, output, true);
        }
        return (new DefenseTierRecord(), [], false);
    }

    private static DefenseTierRecord FromPolicy(DefenseTier tier) =>
        new()
        {
            Name = tier.Name,
            Reserved = [.. tier.Reserved],
            Buildings = [.. tier.Buildings.Select(b => new DefenseBuilding { Definition = b.Definition, Cell = b.Cell, Rotation = b.Rotation, Stuff = b.Stuff })],
        };

    /// <summary>Replaces the named tier (turrets, mortars) with a fresh pending record of the policy tier, appending it when the record lacks one.</summary>
    public void SetPolicyTier(DefenseTier tier)
    {
        var fresh = FromPolicy(tier);
        var i = Tiers.FindIndex(t => t.Name == tier.Name);
        if (i >= 0)
        {
            Tiers[i] = fresh;
        }
        else
        {
            Tiers.Add(fresh);
        }
    }

    /// <summary>
    /// Whether the layout was verified complete and every tier placed since still stands in the last census: the
    /// state in which the goal has no deficit for development arbitration.
    /// </summary>
    public bool Standing() => Complete && Tiers.All(t => !((t.Buildings.Count > 0 || t.NodeBacked) && !t.Built));

    /// <summary>Replaces the named tier's record in place; unknown names are ignored.</summary>
    public void SetTier(DefenseTierRecord tier)
    {
        for (var i = 0; i < Tiers.Count; i++)
        {
            if (Tiers[i].Name == tier.Name)
            {
                Tiers[i] = tier;
            }
        }
    }

    /// <summary>Captures a policy layout for one standard episode, proposed on the plan's killbox node (its id and footprint digest).</summary>
    public static DefenseLayoutRecord From(World w, StandardId standard, ulong episode, DefenseLayout layout, IEnumerable<Cell> entrances, string killbox, string digest)
    {
        var record = new DefenseLayoutRecord
        {
            World = w,
            Standard = standard,
            Episode = episode,
            Killbox = killbox,
            KillboxDigest = digest,
            TrapLane = [.. layout.TrapLane],
            Entrances = [.. entrances],
            Firing = [.. layout.Firing.Select(f => f.Cell)],
            Retreat = [.. layout.Firing.Select(f => f.Retreat)],
            Tiers = [.. layout.Tiers.Select(FromPolicy)],
        };
        record.Validate();
        return record;
    }
}

/// <summary>
/// The workshop planner's last observed rung for a resource or equipment deficit: which bench and recipe it settled
/// on and the research projects still gating them (Go <c>ProductionLadderRecord</c>).
/// </summary>
public sealed record ProductionLadderRecord
{
    public World World { get; init; }

    public Tick Tick { get; init; }

    public Resource Resource { get; init; }

    public string Bench { get; init; } = "";

    public string Recipe { get; init; } = "";

    /// <summary>Sorted and unique.</summary>
    public IReadOnlyList<string> Research { get; init; } = [];

    public ConcernId Concern { get; init; }

    public void Validate()
    {
        World.Validate();
        DomainException.Require(Concern.Value.Length == 0 || Concern == PolicyFunctions.MaintainEquipment || Concern == PolicyFunctions.MaintainResource, "invalid production ladder standard");
        DomainException.Require(
            Tick.Value >= 0 && Families.ValidIdentity(Resource.Value) && (Bench.Length == 0 || Families.ValidIdentity(Bench)) && (Recipe.Length == 0 || Families.ValidIdentity(Recipe)) && Research.Count <= 256,
            "invalid production ladder record"
        );
        for (var i = 0; i < Research.Count; i++)
        {
            DomainException.Require(Families.ValidIdentity(Research[i]) && (i == 0 || string.CompareOrdinal(Research[i - 1], Research[i]) < 0), "invalid production ladder research");
        }
    }
}

/// <summary>The persistent soldier squad: bot intent, kept in the save as the <c>family/soldier_squad</c> blob. Members are sorted.</summary>
public sealed record SoldierSquadRecord
{
    public World World { get; init; }

    public IReadOnlyList<PawnId> Members { get; init; } = [];

    public void Validate()
    {
        World.Validate();
        DomainException.Require(Members.Count <= 256, "invalid soldier squad");
        for (var i = 0; i < Members.Count; i++)
        {
            DomainException.Require(Families.ValidIdentity(Members[i].Value) && (i == 0 || Members[i - 1].CompareTo(Members[i]) < 0), "invalid soldier squad member");
        }
    }
}

internal static class Families
{
    internal static bool ValidIdentity(string s) => s.Length is > 0 and <= 256;
}

public sealed partial class Store
{
    public const string GovernorStandardKeyPrefix = "standard/";
    public const string GovernorLayoutPlanKey = "family/layout_plan";
    public const string GovernorDefenseLayoutKey = "family/defense_layout";
    public const string GovernorProductionLadderKey = "family/production_ladder";
    public const string GovernorSoldierSquadKey = "family/soldier_squad";

    private const int MaxDefenseLayoutBytes = 1024 * 1024;
    private const int MaxProductionLadderBytes = 64 * 1024;
    private const int MaxSoldierSquadBytes = 64 * 1024;

    /// <summary>Replaces the one row of a singleton table (a session cache of the save) with the record's JSON; <see cref="CapacityException"/> past the limit.</summary>
    internal static void PutSingleton<T>(StoreTransaction tx, string table, T record, int limit)
    {
        var data = StoreJson.Serialize(record);
        if (System.Text.Encoding.UTF8.GetByteCount(data) > limit)
        {
            throw new CapacityException();
        }
        _ = tx.Execute("INSERT INTO " + table + "(singleton,payload) VALUES(1,$payload) ON CONFLICT(singleton) DO UPDATE SET payload=excluded.payload", ("$payload", data));
    }

    /// <summary>The singleton table's record, or null when it has none; a record that does not decode is <see cref="StoreCorruptException"/>.</summary>
    internal static T? LoadSingleton<T>(StoreTransaction tx, string table, int limit)
        where T : class
    {
        var (data, found) = tx.QueryFirst("SELECT payload FROM " + table + " WHERE singleton=1", [], r => r.GetString(0));
        if (!found)
        {
            return null;
        }
        if (System.Text.Encoding.UTF8.GetByteCount(data!) > limit)
        {
            throw new CapacityException();
        }
        return StoreJson.Deserialize<T>(data!);
    }

    internal static DefenseLayoutRecord? LoadDefenseLayoutIn(StoreTransaction tx)
    {
        var r = LoadSingleton<DefenseLayoutRecord>(tx, "defense_layout", MaxDefenseLayoutBytes);
        return r is null ? null : Validated(r, r.Validate, "defense layout");
    }

    internal static ProductionLadderRecord? LoadProductionLadderIn(StoreTransaction tx)
    {
        var r = LoadSingleton<ProductionLadderRecord>(tx, "production_ladder", MaxProductionLadderBytes);
        return r is null ? null : Validated(r, r.Validate, "production ladder");
    }

    internal static SoldierSquadRecord? LoadSoldierSquadIn(StoreTransaction tx)
    {
        var r = LoadSingleton<SoldierSquadRecord>(tx, "soldier_squad", MaxSoldierSquadBytes);
        return r is null ? null : Validated(r, r.Validate, "soldier squad");
    }

    private static T Validated<T>(T record, Action validate, string what)
    {
        try
        {
            validate();
        }
        catch (DomainException)
        {
            throw new StoreCorruptException("invalid stored " + what);
        }
        return record;
    }

    /// <summary>
    /// The stored layout for the world's colony and map; null when none or when the stored record belongs to another
    /// colony or map. Its load may differ: the geometry outlives a reload of the same colony. A node-backed tier
    /// comes back without buildings; the caller resolves them from the plan.
    /// </summary>
    public DefenseLayoutRecord? LoadDefenseLayout(World w, CancellationToken cancellationToken) =>
        Run(tx => LoadDefenseLayoutIn(tx) is { } r && r.World.Colony == w.Colony && r.World.Map == w.Map ? r : null, cancellationToken);

    public Task<DefenseLayoutRecord?> LoadDefenseLayoutAsync(World w, CancellationToken cancellationToken) => Task.Run(() => LoadDefenseLayout(w, cancellationToken), cancellationToken);

    /// <summary>Replaces the stored layout. The singleton holds one colony's layout: a new colony overwrites. A node-backed tier's buildings are not stored.</summary>
    public void SaveDefenseLayout(DefenseLayoutRecord r, CancellationToken cancellationToken)
    {
        r.Validate();
        Run(tx => PutSingleton(tx, "defense_layout", r.Stored(), MaxDefenseLayoutBytes), cancellationToken);
    }

    public Task SaveDefenseLayoutAsync(DefenseLayoutRecord r, CancellationToken cancellationToken) => Task.Run(() => SaveDefenseLayout(r, cancellationToken), cancellationToken);

    /// <summary>The stored rung for the world; null when none or when it belongs to another world: a reload observes the ladder afresh.</summary>
    public ProductionLadderRecord? LoadProductionLadder(World w, CancellationToken cancellationToken) =>
        Run(tx => LoadProductionLadderIn(tx) is { } r && r.World == w ? r : null, cancellationToken);

    public Task<ProductionLadderRecord?> LoadProductionLadderAsync(World w, CancellationToken cancellationToken) => Task.Run(() => LoadProductionLadder(w, cancellationToken), cancellationToken);

    /// <summary>Replaces the stored rung; research is canonicalised.</summary>
    public void SaveProductionLadder(ProductionLadderRecord r, CancellationToken cancellationToken)
    {
        var canonical = r with { Research = [.. r.Research.Order(StringComparer.Ordinal)] };
        canonical.Validate();
        Run(tx => PutSingleton(tx, "production_ladder", canonical, MaxProductionLadderBytes), cancellationToken);
    }

    public Task SaveProductionLadderAsync(ProductionLadderRecord r, CancellationToken cancellationToken) => Task.Run(() => SaveProductionLadder(r, cancellationToken), cancellationToken);

    /// <summary>The squad for the world's colony and map, empty when none or when it belongs to another colony or map. A reload of the same colony keeps the squad.</summary>
    public SoldierSquad LoadSoldierSquad(World w, CancellationToken cancellationToken) =>
        Run(tx => LoadSoldierSquadIn(tx) is { } r && r.World.Colony == w.Colony && r.World.Map == w.Map ? new SoldierSquad { Members = [.. r.Members.Select(p => new Policy.PawnId(p.Value))] } : new SoldierSquad(), cancellationToken);

    public Task<SoldierSquad> LoadSoldierSquadAsync(World w, CancellationToken cancellationToken) => Task.Run(() => LoadSoldierSquad(w, cancellationToken), cancellationToken);

    /// <summary>Replaces the stored squad; members are canonicalised.</summary>
    public void SaveSoldierSquad(SoldierSquadRecord r, CancellationToken cancellationToken)
    {
        var canonical = r with { Members = [.. r.Members.Order()] };
        canonical.Validate();
        Run(tx => PutSingleton(tx, "soldier_squad", canonical, MaxSoldierSquadBytes), cancellationToken);
    }

    public Task SaveSoldierSquadAsync(SoldierSquadRecord r, CancellationToken cancellationToken) => Task.Run(() => SaveSoldierSquad(r, cancellationToken), cancellationToken);

    /// <summary>Empties the rounds session cache on a world change; the next review recomputes it from revision zero.</summary>
    public void ResetRounds(CancellationToken cancellationToken) => Run(tx => _ = tx.Execute("DELETE FROM rounds"), cancellationToken);

    public Task ResetRoundsAsync(CancellationToken cancellationToken) => Task.Run(() => ResetRounds(cancellationToken), cancellationToken);
}

internal static partial class StoreSchema
{
    internal static readonly SchemaPart SingletonPart = new(
        """
        CREATE TABLE rounds(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload TEXT NOT NULL) STRICT;
        CREATE TABLE defense_layout(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload TEXT NOT NULL) STRICT;
        CREATE TABLE production_ladder(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload TEXT NOT NULL) STRICT;
        CREATE TABLE soldier_squad(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload TEXT NOT NULL) STRICT;
        CREATE TABLE combat_restoration(singleton INTEGER PRIMARY KEY CHECK(singleton=1), payload TEXT NOT NULL) STRICT;
        """,
        [
            "SELECT singleton,payload FROM rounds LIMIT 0",
            "SELECT singleton,payload FROM defense_layout LIMIT 0",
            "SELECT singleton,payload FROM production_ladder LIMIT 0",
            "SELECT singleton,payload FROM soldier_squad LIMIT 0",
            "SELECT singleton,payload FROM combat_restoration LIMIT 0",
        ]
    );
}
