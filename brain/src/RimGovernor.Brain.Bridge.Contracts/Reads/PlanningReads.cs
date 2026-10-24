using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Common;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Read models of the dispatch verticals. The reads and frame decoders are wire plumbing.

/// <summary>One weapon thing a pawn could equip, where it lies and its CAS token.</summary>
public sealed record EquipCandidate
{
    public string Thing { get; init; } = "";

    public string Definition { get; init; } = "";

    public Domain.PawnId BiocodedTo { get; init; }

    public bool Biocoded { get; init; }

    public DomainCell Cell { get; init; }

    public string Token { get; init; } = "";
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

/// <summary>The access audit's verdicts over the native <see cref="Obs.SpatialAccessSnapshot"/>.</summary>
public static class SpatialAccessRules
{
    /// <summary>The first pawn the audit skipped, when there is one.</summary>
    public static (string Id, bool Skipped) Skipped(this Obs.SpatialAccessSnapshot access)
    {
        foreach (var p in access.Pawns)
        {
            if (p is Obs.PawnAccess.Skipped)
            {
                return (p.Pawn.Value, true);
            }
        }
        return ("", false);
    }

    /// <summary>Why the layout is refused; empty when it is accepted.</summary>
    public static string Refusal(this Obs.SpatialAccessSnapshot access)
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
                case Obs.PawnAccess.Skipped:
                    continue;
                case Obs.PawnAccess.Stranded:
                    return $"pawn {p.Pawn.Value} at {Show(p.Position)} has no safe exit";
                case Obs.PawnAccess.Audited audited:
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
        foreach (var t in access.Pawns[0] is Obs.PawnAccess.Audited first ? first.Targets : [])
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
    public string Token { get; init; } = "";

    public required GearBench Bench { get; init; }
}

/// <summary>The native answer to one combat order.</summary>
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
