using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Common;
using DomainCell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.Brain.GameAccess;

// Read models of the dispatch verticals. The reads and frame decoders are wire plumbing.

/// <summary>A loose weapon's native stock, item and optional matching gear row.</summary>
public sealed record EquipCandidate
{
    public required RimGovernor.Brain.GameFacts.Observations.ResourceStock Stock { get; init; }

    public required RimGovernor.Brain.GameFacts.Observations.StockItem Item { get; init; }

    public RimGovernor.Brain.GameFacts.Observations.GearItem? Gear { get; init; }

    public string Definition => Stock.DefName;
}

/// <summary>The equip candidates over a region.</summary>
public sealed record EquipRead
{
    public ObservationContext? Context { get; init; }

    public IReadOnlyList<EquipCandidate> Targets { get; init; } = [];
}

/// <summary>One supply thing an allow order names.</summary>
public sealed record SupplyTarget
{
    public required Domain.SupplyAllow Supply { get; init; }
}

/// <summary>The supply things over a cell or the food reserve.</summary>
public sealed record SupplyRead
{
    public ObservationContext? Context { get; init; }

    public IReadOnlyList<SupplyTarget> Targets { get; init; } = [];
}

/// <summary>The access audit's verdicts over the native <see cref="RimGovernor.Brain.GameFacts.Observations.SpatialAccessSnapshot"/>.</summary>
public static class SpatialAccessRules
{
    /// <summary>The first pawn the audit skipped, when there is one.</summary>
    public static (string Id, bool Skipped) Skipped(this RimGovernor.Brain.GameFacts.Observations.SpatialAccessSnapshot access)
    {
        foreach (var p in access.Pawns)
        {
            if (p is RimGovernor.Brain.GameFacts.Observations.PawnAccess.Skipped)
            {
                return (p.Pawn.Value, true);
            }
        }
        return ("", false);
    }

    /// <summary>Why the layout is refused; empty when it is accepted.</summary>
    public static string Refusal(this RimGovernor.Brain.GameFacts.Observations.SpatialAccessSnapshot access)
    {
        if (access.Pawns.Count == 0)
        {
            return "no colonists audited";
        }
        var reached = new HashSet<DomainCell>();
        foreach (var p in access.Pawns)
        {
            switch (p)
            {
                case RimGovernor.Brain.GameFacts.Observations.PawnAccess.Skipped:
                    continue;
                case RimGovernor.Brain.GameFacts.Observations.PawnAccess.Stranded:
                    return $"pawn {p.Pawn.Value} at {Show(p.Position)} has no safe exit";
                case RimGovernor.Brain.GameFacts.Observations.PawnAccess.Audited audited:
                    foreach (var t in audited.Targets)
                    {
                        if (t.NativeReachable && !t.ProjectedReachable)
                        {
                            return $"pawn {p.Pawn.Value} at {Show(p.Position)} loses target {Show(t.Cell)}";
                        }
                        if (t.NativeReachable && t.ProjectedReachable)
                        {
                            reached.Add(t.Cell);
                        }
                    }
                    break;
            }
        }
        foreach (var t in access.Pawns[0] is RimGovernor.Brain.GameFacts.Observations.PawnAccess.Audited first ? first.Targets : [])
        {
            if (!reached.Contains(t.Cell))
            {
                return $"target {Show(t.Cell)} unreachable";
            }
        }
        return "";
    }

    // A cell prints as {X Z}.
    private static string Show(DomainCell c) => $"{{{c.X} {c.Z}}}";
}

/// <summary>
/// The native placement preview of one building action, the stock it implies, whether
/// native work (a blueprint or frame) already stands on the cells and the native reason when it was refused.
/// </summary>
public sealed record BuildingPreview
{
    public required Preview Preview { get; init; }

    public required StockObservation Stock { get; init; }

    public bool NativeWorkPending { get; init; }

    public string Reason { get; init; } = "";
}

/// <summary>One workbench's CAS token and its gear bench facts.</summary>
public sealed record GearBenchRead
{
    public string Token => Bench.Observed.Snapshot.Token ?? throw new RequiredFactException("gear bench CAS token:" + Bench.Id);

    public required GearBench Bench { get; init; }
}
