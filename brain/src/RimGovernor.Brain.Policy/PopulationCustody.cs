using O = RimGovernor.Contracts.Wire.Observations;
namespace RimGovernor.Brain.Policy;

/// <summary>
/// Names which of Population-*'s two custody sub-steps a candidate calls for: capture (a downed hostile not
/// yet colony property) or rescue (a downed guest not yet admitted). The decision is derived purely from
/// observed facts -- guest status vs. hostility -- matching the deficit-detection style every other MaintainX
/// vertical uses.
/// </summary>
public enum CustodyDecision
{
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

    public bool WillJoinIfRescued => Person.WillJoinIfRescued;

    public required O.PopulationPerson Person { get; init; }
    public required O.PawnState Observed { get; init; }
    public PawnId Pawn => Person.Pawn;

    public bool Dead => Observed.Dead;

    public bool Downed => Observed.Downed;

    public bool Guest => Person.Guest;

    public bool Admitted => Person.Admitted;

    public bool Prisoner => Observed.Prisoner;

    public bool Hostile => Observed.Hostile;

    /// <summary>
    /// The game's guest.Recruitable, rolled at pawn generation (PawnGenerator.GeneratePawn ->
    /// SetupRecruitable), so a downed raider already carries it before capture. It orders captures only.
    /// </summary>
    public bool Recruitable => Person.Recruitable;

    /// <summary>The pawn's biography: skills rank a standing hostile as a lance target.</summary>
    public required PrisonerProspect Prospect { get; init; }

    /// <summary>
    /// A LuciferiumAddiction hediff: the raider is not CaptureWorthy, so the fight strips and finishes it
    /// instead.
    /// </summary>
    public bool Luciferium => Observed.Health.Hediffs.Any(h => h.DefName == "LuciferiumAddiction");

    /// <summary>Gates a capture on the fight's strip: every downed raider is stripped before it is taken.</summary>
    public bool WearingApparel => Observed.Equipment.Apparel.Count > 0;
}

/// <summary>The one candidate chosen for a capture/rescue dispatch, chosen deterministically.</summary>
public sealed record CustodyChoice(PawnId Pawn, CustodyDecision Decision);

public static partial class PopulationRules
{
    /// <summary>The custody sub-step <paramref name="row"/> calls for, or null when it is not a candidate.</summary>
    internal static CustodyDecision? CustodyEligible(CustodyFacts row)
    {
        if (row.Dead || !row.Downed)
        {
            return null;
        }
        if (row.Guest && !row.Admitted)
        {
            return CustodyDecision.Rescue;
        }
        if (row.QuestRefugee && !row.Admitted && !row.Prisoner)
        {
            return row.Hostile ? CustodyDecision.Capture : CustodyDecision.Rescue;
        }
        if (!row.QuestProtected && !row.Guest && !row.Admitted && !row.Prisoner && row.Hostile)
        {
            if (!CombatThreatRules.CaptureWorthy(row.Luciferium) || row.WearingApparel)
            {
                return null;
            }
            return CustodyDecision.Capture;
        }
        return null;
    }

    /// <summary>
    /// Picks the next eligible candidate to dispatch, or null when none qualifies. A capture of an unrecruitable
    /// raider ranks after every other candidate; within a rank the lowest pawn ID wins, for a deterministic order.
    /// Rescues are never demoted, and an unrecruitable raider is still captured once nothing outranks it.
    /// </summary>
    public static CustodyChoice? SelectCustodyMethod(IReadOnlyList<CustodyFacts> rows)
    {
        CustodyChoice? best = null;
        var bestRank = 0;
        foreach (var row in rows)
        {
            if (CustodyEligible(row) is not { } decision)
            {
                continue;
            }
            var rank = decision == CustodyDecision.Capture && !row.Recruitable ? 1 : 0;
            if (best is null || rank < bestRank || (rank == bestRank && row.Pawn.CompareTo(best.Pawn) < 0))
            {
                best = new CustodyChoice(row.Pawn, decision);
                bestRank = rank;
            }
        }
        return best;
    }
}
