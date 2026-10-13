namespace RimGovernor.Brain.Policy;

// Hauls of mined, chopped and hunted goods in the station graph (#2998, child of #2949). A miner, a woodcutter
// or a hunter works outdoors and carries what the work gives to a store, so each of their hauls is a trip
// between the store and the outside node: clustering counts it as the store's outside traffic (outdoor) and the
// tiler walks it to the plan's entrances like any outside trip. The model, from the game (ilspycmd,
// JobDriver_Mine, Mineable.TrySpawnYield, JobDriver_PlantWork, JobDriver_Hunt and the ThingDef rows of the
// catalog):
//
//   - Mining: a pick hit every 100 / MiningSpeed ticks does 80 damage to natural rock, so a tile of H hit
//     points takes ceil(H / 80) hits, and it yields the def's mineableYield times mineableDropChance of its
//     mineableThing (times the MiningYield stat when the yield is wasteable). Per mineable def; the defs of one
//     yielded item average.
//   - Chopping: a tree takes harvestWork / PlantWorkSpeed ticks (a grown plant is worked at the stat) and
//     yields harvestYield times PlantHarvestYield of its harvestedThingDef. The trees (treeCategory set, no
//     stump) of one yielded item average, whatever the biome grows.
//   - Hunting: the hunter carries the kill to a storage cell (JobDriver_Hunt,
//     StoreUtility.TryFindBestBetterStoreCellFor), so a kill is one load from the outside to the store that
//     holds a corpse (the cold one while one stands), where the butcher's ingredient haul takes it on.
//
// An owner works the type CookTicksPerDay of the day (the plan's eight hours a worker, the same labor
// convention PlanResourceSupply budgets). How many owners the colony has comes from the work assignment's
// demand (baselineDemand): the woodcutters are the PlantCutting owners, the hunters the Hunting owners, so a
// colony of four or more hunts with two. Mining has no baseline owner (PlanWork gives Mining one only where a
// requirement names it), so the colony's miners are the haulGame table's MinerOwners, one like the baseline of
// the core roles. An owner's output is what the work gives: a type's ticks go to the goods the colony holds a
// stock demand for (Stock) in equal shares, and a hunt happens only where the orders butcher, since a corpse
// nobody butchers has no use. The loads are the items over the item's stack (a hauler carries a stack: a trip
// across a door is one passage, not one per item); a corpse is a load of its own. The weights are loads a day,
// one way, merged into the store's one outside edge.
//
// Not modelled: the output is the owners' capacity, so it is an upper bound where the stock demand is met
// earlier; the skill, passion and traits of the owners (the stats are read at their default); the walk to the
// rock, the tree or the prey; the stone chunks, wood and ore a builder or a deep drill (observation: DeepDrill)
// brings in; trade and loot hauls. An unread yield, work or stack adds no flow: it is unknown, never zero.
public static partial class PolicyConstants
{
    // Reasons of the haul flows.
    public const string ReasonFlowHaulMined = "flow_haul_mined";
    public const string ReasonFlowHaulWood = "flow_haul_wood";
    public const string ReasonFlowHaulHunt = "flow_haul_hunt";

    private static readonly HaulTable HaulGameValue = new()
    {
        PickTicks = 100,
        NaturalHit = 80,
        MiningSpeed = "MiningSpeed",
        MiningYield = "MiningYield",
        PlantWorkSpeed = "PlantWorkSpeed",
        PlantHarvestYield = "PlantHarvestYield",
        MinerOwners = 1,
    };

    internal static HaulTable HaulJudgment => HaulGameValue;
}

/// <summary>
/// The haul model's game numbers the defs do not carry, registered in DefTables so a renamed stat fails the
/// dangling-reference gate. PickTicks and NaturalHit are JobDriver_Mine's literals (ticks between pick hits at
/// a mining speed of 1, damage per hit to natural rock); the stats are the ones the work and the yield are
/// scaled by, read at their default base value (a pawn of no skill). MinerOwners is the one judgment: the
/// colony's miners, since the work assignment gives Mining no baseline owner (reason: mining_no_baseline_owner).
/// </summary>
public sealed record HaulTable
{
    public double PickTicks { get; init; }

    public double NaturalHit { get; init; }

    public string MiningSpeed { get; init; } = string.Empty;

    public string MiningYield { get; init; } = string.Empty;

    public string PlantWorkSpeed { get; init; } = string.Empty;

    public string PlantHarvestYield { get; init; } = string.Empty;

    public int MinerOwners { get; init; }
}

/// <summary>One good the colony works out of the map: what a node of the work (a mined tile, a felled tree) yields and the work ticks it takes at a skill-less pawn's speed.</summary>
public sealed record HaulSource
{
    public Resource Good { get; init; }

    public double Units { get; init; }

    public double Work { get; init; }

    /// <summary>Reports a source whose numbers were read.</summary>
    internal bool Valid() =>
        !string.IsNullOrEmpty(Good.Value) && Units > 0 && Work > 0 && !double.IsInfinity(Units) && !double.IsInfinity(Work);
}

/// <summary>The catalog's mined and chopped goods, sorted by good. The zero value is unknown and leaves them unhauled.</summary>
public sealed record HaulModel
{
    public IReadOnlyList<HaulSource> Mined { get; init; } = [];

    public IReadOnlyList<HaulSource> Chopped { get; init; } = [];
}

internal sealed partial class GraphBuilder
{
    /// <summary>Adds the haul edges of the outdoor work: the owners' output a day, in loads, between the stores that hold it and the outside node.</summary>
    public void Hauls()
    {
        var colonists = In.Population.Colonists;
        if (colonists <= 0)
        {
            return;
        }
        var model = In.Catalog.Haul;
        HaulSources(model.Mined, PolicyConstants.HaulJudgment.MinerOwners, PolicyConstants.ReasonFlowHaulMined);
        HaulSources(model.Chopped, PolicyFunctions.BaselineDemand(WorkType.PlantCutting, colonists, new WorkDemand()), PolicyConstants.ReasonFlowHaulWood);
        HaulHunt(PolicyFunctions.BaselineDemand(WorkType.Hunting, colonists, new WorkDemand()));
    }

    /// <summary>
    /// Hauls the goods of sources the colony holds a stock demand for: the owners' ticks, in equal shares, over
    /// each source's work, times its yield, in stacks, from the outside to the store that holds the good.
    /// </summary>
    private void HaulSources(IReadOnlyList<HaulSource> sources, int owners, string reason)
    {
        var wanted = sources.Where(s => s.Valid() && In.Stock.GetValueOrDefault(s.Good) > 0).ToList();
        if (owners <= 0 || wanted.Count == 0)
        {
            return;
        }
        var ticks = owners * (double)PolicyFunctions.CookTicksPerDay / wanted.Count;
        foreach (var s in wanted)
        {
            var limit = In.StackLimit.GetValueOrDefault(s.Good);
            var (store, held) = StoreFor(s.Good);
            if (limit <= 0 || !held)
            {
                continue;
            }
            AddEdge(store, PolicyConstants.OutsideNode, ticks / s.Work * s.Units / limit, false, reason);
        }
    }

    /// <summary>Hauls the hunters' kills (HuntKillsPerHunterDay each) to the store that holds a corpse, where the orders butcher: a work node whose recipes make filth is a butcher's.</summary>
    private void HaulHunt(int hunters)
    {
        var butchers = false;
        foreach (var n in Nodes.Values)
        {
            butchers = butchers || (n.Purpose == StationPurpose.Work && n.Filth);
        }
        if (!butchers || hunters <= 0)
        {
            return;
        }
        var (store, ok) = StoreOf(true);
        if (ok)
        {
            AddEdge(store, PolicyConstants.OutsideNode, hunters * (double)PolicyFunctions.HuntKillsPerHunterDay, false, PolicyConstants.ReasonFlowHaulHunt);
        }
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The table the bridge reads the haul sources with.</summary>
    public static HaulTable HaulGame() => PolicyConstants.HaulJudgment;
}
