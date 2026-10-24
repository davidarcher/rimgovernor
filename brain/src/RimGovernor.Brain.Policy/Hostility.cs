namespace RimGovernor.Brain.Policy;

// Hostility response: the bot owns every colonist's Assign-tab hostility response. Attack is the default for anyone
// capable of violence (Flee drops work at the first rat). Flee is for pawns who cannot fight or should not:
// violence-incapable, children, and pawns with serious injuries or blood loss. Ignore is task-scoped: while a
// pawn's work-giver job targets a cell beside a known-passive hostile (a sleeping hive's jelly) and no engaging
// hostile is near that cell. The mapping is stateless, so the restore needs no memory: when the job moves on, the
// hostiles wake or an engaging hostile comes near, the same mapping returns the pawn's default and the planner
// writes it back.

/// <summary>One colonist's hostility inputs.</summary>
public sealed record HostilityPawn
{
    public required PawnId ID { get; init; }

    /// <summary>The pawn's hostility response; unknown for a pawn without care settings (no configurable response), which is never written.</summary>
    public required Fact<HostilityResponse> Current { get; init; }

    public required bool ViolenceCapable { get; init; }
    public required double Age { get; init; }
    public required double BloodLoss { get; init; }

    /// <summary>The BloodLoss severity from which a pawn flees, from the catalog.</summary>
    public required double SeriousBloodLoss { get; init; }

    public required double Health { get; init; }
    public required PawnJob Job { get; init; }
}

public static class HostilityRules
{
    /// <summary>The biological age below which a pawn is a child (vanilla's DevelopmentalStage.Child ends at 13).</summary>
    public const double ChildAge = 13.0;

    /// <summary>
    /// The summary health below which a pawn is badly hurt. The serious BloodLoss severity is the minSeverity of
    /// the BloodLoss hediff's "moderate" stage, read from the catalog (HostilityPawn.SeriousBloodLoss).
    /// </summary>
    public const double SeriousInjuryHealth = 0.6;

    /// <summary>The Chebyshev reach around a job's target inside which a passive hostile makes the job stealth work, and an engaging hostile makes it unsafe.</summary>
    public const int StealthCells = 15;

    /// <summary>
    /// The pawn's standing response: Flee for a pawn that cannot or should not fight, Attack otherwise.
    /// </summary>
    public static HostilityResponse DefaultHostility(HostilityPawn p) =>
        !p.ViolenceCapable || p.Age < ChildAge || p.BloodLoss >= p.SeriousBloodLoss || p.Health < SeriousInjuryHealth
            ? HostilityResponse.Flee
            : HostilityResponse.Attack;

    /// <summary>
    /// Reports a work-giver job whose target cell stands within StealthCells of a known-passive hostile with no
    /// engaging hostile within that reach. A job no work giver issued (a forced order, rest, a meal), or one without
    /// a target cell, is never stealth work.
    /// </summary>
    public static bool StealthJob(PawnJob j, IReadOnlyList<EmergencyThreat> threats)
    {
        if (string.IsNullOrEmpty(j.Def) || j.Work == WorkType.None)
        {
            return false;
        }
        if (!j.Target.CellOf(out var cell))
        {
            return false;
        }
        var passive = false;
        foreach (var t in threats)
        {
            if (t.Dead)
            {
                continue;
            }
            if (!ThreatNear(t, cell))
            {
                continue;
            }
            if (t.Engaging)
            {
                return false;
            }
            passive = true;
        }
        return passive;
    }

    /// <summary>Reports whether a threat (a pawn's cell, any of a building's cells) stands within StealthCells of cell.</summary>
    private static bool ThreatNear(EmergencyThreat t, Cell cell)
    {
        IReadOnlyList<Cell> cells = t.Building ? t.Cells : [t.Position];
        return cells.Any(c => LightingRules.Chebyshev(c, cell) <= StealthCells);
    }

    /// <summary>The response the pawn should hold now: Ignore on stealth work for a pawn that would otherwise Attack, else its default. A Flee pawn keeps fleeing beside sleeping hostiles.</summary>
    public static HostilityResponse WantHostility(HostilityPawn p, IReadOnlyList<EmergencyThreat> threats)
    {
        var mode = DefaultHostility(p);
        return mode == HostilityResponse.Attack && StealthJob(p.Job, threats) ? HostilityResponse.Ignore : mode;
    }

    /// <summary>The settings to write: every pawn whose known response differs from the one it should hold.</summary>
    public static IReadOnlyList<PawnSettings> HostilityChanges(IReadOnlyList<HostilityPawn> pawns, IReadOnlyList<EmergencyThreat> threats)
    {
        var @out = new List<PawnSettings>();
        foreach (var p in pawns)
        {
            var want = WantHostility(p, threats);
            if (!p.Current.TryGetValue(out var current) || current == want)
            {
                continue;
            }
            try
            {
                @out.Add(PawnSettings.NewHostilitySetting((Domain.PawnId)p.ID, want));
            }
            catch (DomainException)
            {
                // An unwritable setting is skipped.
            }
        }
        return @out;
    }

    /// <summary>The review's HostilityOwed fact: a change is owed. Unknown pawns owe nothing.</summary>
    public static Fact<bool> HostilityOwed(Fact<EquatableArray<HostilityPawn>> pawns, IReadOnlyList<EmergencyThreat> threats)
    {
        if (!pawns.TryGetValue(out var rows))
        {
            return Unknown<bool>();
        }
        return Known(HostilityChanges(rows, threats).Count > 0);
    }
}
