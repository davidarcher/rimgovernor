using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Names which of Population-*'s two custody sub-steps a candidate calls for: capture (a downed hostile not
/// yet colony property) or rescue (a downed guest not yet admitted). The decision is derived purely from
/// observed facts -- guest status vs. hostility -- matching the deficit-detection style every other MaintainX
/// vertical uses.
/// </summary>
public enum CustodyDecision
{
    [WireName("")]
    None = 0,
    Capture,
    Rescue,
}

/// <summary>
/// Describes one humanlike population-census row as a candidate custody target. It is sourced from the same
/// per-cycle population census PrisonerFacts already reads, broadened to include every observed humanlike
/// rather than only prisoners.
/// </summary>
public sealed record CustodyFacts
{
    public bool QuestRefugee { get; init; }

    public bool QuestProtected { get; init; }

    public Fact<bool> WillJoinIfRescued { get; init; }

    public PawnId Pawn { get; init; }

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    public Fact<bool> Guest { get; init; }

    public Fact<bool> Admitted { get; init; }

    public Fact<bool> Prisoner { get; init; }

    public Fact<bool> Hostile { get; init; }

    /// <summary>
    /// The game's guest.Recruitable, rolled at pawn generation (PawnGenerator.GeneratePawn ->
    /// SetupRecruitable), so a downed raider already carries it before capture. It orders captures only.
    /// </summary>
    public Fact<bool> Recruitable { get; init; }

    /// <summary>The pawn's biography: skills rank a standing hostile as a lance target.</summary>
    public Fact<PrisonerProspect> Prospect { get; init; }

    /// <summary>
    /// A LuciferiumAddiction hediff: the raider is not CaptureWorthy, so the fight strips and finishes it
    /// instead.
    /// </summary>
    public Fact<bool> Luciferium { get; init; }

    /// <summary>Gates a capture on the fight's strip: every downed raider is stripped before it is taken.</summary>
    public Fact<bool> WearingApparel { get; init; }
}

/// <summary>Names why the population custody planner did or did not propose a capture/rescue write.</summary>
public enum CustodyPlanReason
{
    [WireName("")]
    None = 0,
    [WireName("no_custody_candidate")]
    NoDeficit,
    [WireName("population_census_unknown")]
    Unknown,
}

/// <summary>The one candidate chosen for a capture/rescue dispatch, mirroring PrisonerChoice's single-write-per-cycle rule.</summary>
public sealed record CustodyChoice
{
    public CustodyPlanReason Reason { get; init; }

    public PawnId Pawn { get; init; }

    public CustodyDecision Decision { get; init; }
}

public static partial class PolicyFunctions
{
    internal static (CustodyDecision Decision, bool Ok) CustodyEligible(CustodyFacts row)
    {
        var dk = row.Dead.TryGetValue(out var dead);
        var wk = row.Downed.TryGetValue(out var downed);
        if (!dk || !wk || dead || !downed)
        {
            return (CustodyDecision.None, false);
        }
        var gk = row.Guest.TryGetValue(out var guest);
        var ak = row.Admitted.TryGetValue(out var admitted);
        if (!gk || !ak)
        {
            return (CustodyDecision.None, false);
        }
        if (guest && !admitted)
        {
            return (CustodyDecision.Rescue, true);
        }
        var pk = row.Prisoner.TryGetValue(out var prisoner);
        var hk = row.Hostile.TryGetValue(out var hostile);
        if (!pk || !hk)
        {
            return (CustodyDecision.None, false);
        }
        if (row.QuestRefugee && !admitted && !prisoner)
        {
            return (hostile ? CustodyDecision.Capture : CustodyDecision.Rescue, true);
        }
        if (!row.QuestProtected && !guest && !admitted && !prisoner && hostile)
        {
            row.Luciferium.TryGetValue(out var addicted);
            var known = row.WearingApparel.TryGetValue(out var worn);
            if (!CaptureWorthy(new CombatPawnState { Luciferium = addicted }) || !known || worn)
            {
                return (CustodyDecision.None, false);
            }
            return (CustodyDecision.Capture, true);
        }
        return (CustodyDecision.None, false);
    }

    /// <summary>
    /// Reports whether any observed candidate currently qualifies for capture or rescue. An unknown census
    /// leaves the whole need unknown; an individual row with incomplete facts is simply not a candidate, the
    /// same lenient style SelectRescue's eligibility checks use, since the candidate pool here is the whole
    /// map population rather than a small trusted list.
    /// </summary>
    public static Fact<bool> CustodyDeficit(Fact<EquatableArray<CustodyFacts>> rows)
    {
        if (!rows.TryGetValue(out var all))
        {
            return Unknown<bool>();
        }
        return Known(all.Any(row => CustodyEligible(row).Ok));
    }

    /// <summary>
    /// Picks the next eligible candidate to dispatch. A capture of a raider not known to be recruitable ranks
    /// after every other candidate; within a rank the lowest pawn ID wins, mirroring
    /// SelectPrisonerInteractionMethod's determinism. Rescues are never demoted, and an unrecruitable raider
    /// is still captured once nothing outranks it.
    /// </summary>
    public static CustodyChoice SelectCustodyMethod(Fact<EquatableArray<CustodyFacts>> rows)
    {
        if (!rows.TryGetValue(out var all))
        {
            return new CustodyChoice { Reason = CustodyPlanReason.Unknown };
        }
        CustodyChoice? best = null;
        var bestRank = 0;
        foreach (var row in all)
        {
            var (decision, ok) = CustodyEligible(row);
            if (!ok)
            {
                continue;
            }
            var rank = 0;
            var known = row.Recruitable.TryGetValue(out var recruitable);
            if (decision == CustodyDecision.Capture && (!known || !recruitable))
            {
                rank = 1;
            }
            if (best is null || rank < bestRank || (rank == bestRank && row.Pawn.CompareTo(best.Pawn) < 0))
            {
                best = new CustodyChoice { Pawn = row.Pawn, Decision = decision };
                bestRank = rank;
            }
        }
        return best ?? new CustodyChoice { Reason = CustodyPlanReason.NoDeficit };
    }
}
