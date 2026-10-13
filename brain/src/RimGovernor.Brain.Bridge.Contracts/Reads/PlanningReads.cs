using RimGovernor.Brain.Policy;
using RimGovernor.Protocol.Common;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using TradeTarget = RimGovernor.Protocol.Common.TradeTarget;

namespace RimGovernor.Brain.Bridge.Contracts;

// Read models of the dispatch verticals (Go equip_things.go, supply_allow.go, trade_session.go,
// wall_upgrade_sites.go, spatial_access.go, world.go, building_preview.go, gear_bench_census.go,
// combat_orders.go). The reads and frame decoders are wire plumbing and not ported.

/// <summary>Go's <c>EquipCandidate</c>: one weapon thing a pawn could equip, where it lies and its CAS token.</summary>
public sealed record EquipCandidate
{
    public string Thing { get; init; } = "";

    public string Definition { get; init; } = "";

    public Domain.PawnId BiocodedTo { get; init; }

    public bool Biocoded { get; init; }

    public DomainCell Cell { get; init; }

    public string Token { get; init; } = "";
}

/// <summary>Go's <c>EquipRead</c>: the equip candidates over a region.</summary>
public sealed record EquipRead
{
    public ObservationContext? Context { get; init; }

    public IReadOnlyList<EquipCandidate> Targets { get; init; } = [];
}

/// <summary>Go's <c>SupplyTarget</c>: one supply thing an allow order names.</summary>
public sealed record SupplyTarget
{
    public required Domain.SupplyAllow Supply { get; init; }
}

/// <summary>Go's <c>SupplyRead</c>: the supply things over a cell or the food reserve.</summary>
public sealed record SupplyRead
{
    public ObservationContext? Context { get; init; }

    public IReadOnlyList<SupplyTarget> Targets { get; init; } = [];
}

/// <summary>Go's <c>TradeSessionRead</c>: the open trade session, its parties and its CAS target.</summary>
public sealed record TradeSessionRead
{
    public TradeTarget? Target { get; init; }

    public ObservationContext? Context { get; init; }

    public string Trader { get; init; } = "";

    public string Negotiator { get; init; } = "";

    public bool Open { get; init; }
}

/// <summary>Go's <c>WallUpgradeSite</c>: one wall segment a stone upgrade would replace.</summary>
public sealed record WallUpgradeSite
{
    public string TargetId { get; init; } = "";

    public bool TargetPresent { get; init; }

    public string Blocker { get; init; } = "";

    public int X { get; init; }

    public int Z { get; init; }

    public int NX { get; init; }

    public int NZ { get; init; }

    public IReadOnlyList<DomainCell> BackupCells { get; init; } = [];

    public bool LeftSupport { get; init; }

    public bool RightSupport { get; init; }

    /// <summary>Whether the site names a present target and nothing blocks it.</summary>
    public bool Eligible() => TargetId.Length != 0 && TargetPresent && Blocker.Length == 0;
}

/// <summary>Go's <c>WallUpgradeSites</c>: the wall upgrade sites over one wall.</summary>
public sealed record WallUpgradeSites
{
    public ObservationContext? Context { get; init; }

    public IReadOnlyList<WallUpgradeSite> Sites { get; init; } = [];
}

/// <summary>Go's <c>AccessTarget</c>: whether one target cell is reachable natively and in the projected layout.</summary>
public sealed record AccessTarget
{
    public DomainCell Cell { get; init; }

    public bool NativeReachable { get; init; }

    public bool ProjectedReachable { get; init; }

    public uint ProjectedSteps { get; init; }
}

/// <summary>Go's <c>PawnAccess</c>: one audited pawn's access before and after a projected layout.</summary>
public sealed record PawnAccess
{
    public bool Skipped { get; init; }

    public string Id { get; init; } = "";

    public DomainCell Position { get; init; }

    public uint Current { get; init; }

    public uint After { get; init; }

    public bool LosesAccess { get; init; }

    public bool OriginKnown { get; init; }

    public DomainCell ProjectedOrigin { get; init; }

    public uint EgressSteps { get; init; }

    public IReadOnlyList<AccessTarget> Targets { get; init; } = [];
}

/// <summary>Go's <c>SpatialAccess</c>: the access audit of a projected layout.</summary>
public sealed record SpatialAccess
{
    public ObservationContext? Context { get; init; }

    public uint MapCells { get; init; }

    public uint Walkable { get; init; }

    public IReadOnlyList<PawnAccess> Pawns { get; init; } = [];

    /// <summary>Whether the layout loses no audited pawn's access to any target.</summary>
    public bool Accepted() => Refusal().Length == 0;

    /// <summary>The first pawn the audit skipped, when there is one.</summary>
    public (string Id, bool Skipped) Skipped()
    {
        foreach (var p in Pawns)
        {
            if (p.Skipped)
            {
                return (p.Id, true);
            }
        }
        return ("", false);
    }

    /// <summary>Why the layout is refused; empty when it is accepted.</summary>
    public string Refusal()
    {
        if (Pawns.Count == 0)
        {
            return "no colonists audited";
        }
        var reached = new HashSet<DomainCell>();
        foreach (var p in Pawns)
        {
            if (p.Skipped)
            {
                continue;
            }
            if (!p.OriginKnown)
            {
                return $"pawn {p.Id} at {Show(p.Position)} has no safe exit";
            }
            foreach (var t in p.Targets)
            {
                if (t.NativeReachable && !t.ProjectedReachable)
                {
                    return $"pawn {p.Id} at {Show(p.Position)} loses target {Show(t.Cell)}";
                }
                if (t.NativeReachable && t.ProjectedReachable)
                {
                    reached.Add(t.Cell);
                }
            }
        }
        foreach (var t in Pawns[0].Targets)
        {
            if (!reached.Contains(t.Cell))
            {
                return $"target {Show(t.Cell)} unreachable";
            }
        }
        return "";
    }

    // Go prints a cell with %v: {X Z}.
    private static string Show(DomainCell c) => $"{{{c.X} {c.Z}}}";
}

/// <summary>Go's <c>SettlementFact</c>: one world settlement and the faction that holds it.</summary>
public sealed record SettlementFact
{
    public string Id { get; init; } = "";

    public string Label { get; init; } = "";

    public Fact<bool> CanTrade { get; init; }

    public string TraderKind { get; init; } = "";

    public int Tile { get; init; }

    public string FactionId { get; init; } = "";

    public string FactionDefName { get; init; } = "";

    public string Relation { get; init; } = "";

    public int Goodwill { get; init; }

    public bool GoodwillKnown { get; init; }

    public bool Player { get; init; }

    public string SnapshotToken { get; init; } = "";

    public string FactionSnapshotToken { get; init; } = "";
}

/// <summary>Go's <c>WorldRead</c>: the world's settlements around a tile and the home tile's longitude.</summary>
public sealed record WorldRead
{
    public ObservationContext? Context { get; init; }

    public IReadOnlyList<SettlementFact> Settlements { get; init; } = [];

    public Fact<double> Longitude { get; init; }
}

/// <summary>
/// Go's <c>BuildingPreview</c>: the native placement preview of one building action, the stock it implies, whether
/// native work (a blueprint or frame) already stands on the cells and the native reason when it was refused.
/// </summary>
public sealed record BuildingPreview
{
    public required Preview Preview { get; init; }

    public required StockObservation Stock { get; init; }

    public bool NativeWorkPending { get; init; }

    public string Reason { get; init; } = "";
}

/// <summary>Go's <c>GearBenchRead</c>: one workbench's CAS token and its gear bench facts.</summary>
public sealed record GearBenchRead
{
    public string Token { get; init; } = "";

    public required GearBench Bench { get; init; }
}

/// <summary>Go's <c>CombatOrderResult</c>: the native answer to one combat order.</summary>
public sealed record CombatOrderResult
{
    public int Index { get; init; }

    /// <summary>Empty for a door order.</summary>
    public string PawnId { get; init; } = "";

    public bool Applied { get; init; }

    /// <summary>One of the <c>CombatRefusal*</c> reasons when the order was not applied.</summary>
    public string Refusal { get; init; } = "";

    /// <summary>The ordered job, when the order took one.</summary>
    public string JobDef { get; init; } = "";
}
