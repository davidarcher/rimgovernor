namespace RimGovernor.Brain.Policy;

// Hostility response: the bot owns every colonist's Assign-tab hostility response. Attack is the default for anyone
// capable of violence (Flee drops work at the first rat). Flee is for pawns who cannot fight or should not:
// violence-incapable, children, and pawns with serious injuries or blood loss. Ignore is task-scoped: while a
// pawn's work-giver job targets a cell beside a known-passive hostile (a sleeping hive's jelly) and no engaging
// hostile is near that cell. The mapping is stateless, so the restore needs no memory: when the job moves on, the
// hostiles wake or an engaging hostile comes near, the same mapping returns the pawn's default and the planner
// writes it back.

/// <summary>One colonist's hostility inputs. (Go <c>HostilityPawn</c>.)</summary>
public sealed record HostilityPawn
{
    public PawnId ID { get; init; }

    /// <summary>The pawn's hostility response; unknown where the read did not carry it (no configurable response), which is never written.</summary>
    public Fact<HostilityResponse> Current { get; init; }

    public Fact<bool> ViolenceCapable { get; init; }
    public Fact<double> Age { get; init; }
    public Fact<double> BloodLoss { get; init; }

    /// <summary>The BloodLoss severity from which a pawn flees; unknown without a catalog, which leaves blood loss undecided.</summary>
    public Fact<double> SeriousBloodLoss { get; init; }

    public Fact<double> Health { get; init; }
    public Fact<PawnJob> Job { get; init; }
}

public static partial class PolicyFunctions
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
    /// The pawn's standing response: Flee for a pawn that cannot or should not fight, Attack otherwise. Not ok
    /// when a deciding fact is unknown.
    /// </summary>
    public static (HostilityResponse Response, bool Ok) DefaultHostility(HostilityPawn p)
    {
        var vk = p.ViolenceCapable.TryGetValue(out var violent);
        if (vk && !violent)
        {
            return (HostilityResponse.Flee, true);
        }
        var ak = p.Age.TryGetValue(out var age);
        if (ak && age < ChildAge)
        {
            return (HostilityResponse.Flee, true);
        }
        var lk = p.BloodLoss.TryGetValue(out var loss);
        if (p.SeriousBloodLoss.TryGetValue(out var serious) && lk && loss >= serious)
        {
            return (HostilityResponse.Flee, true);
        }
        var hk = p.Health.TryGetValue(out var health);
        if (hk && health < SeriousInjuryHealth)
        {
            return (HostilityResponse.Flee, true);
        }
        if (!vk || !ak || !hk)
        {
            return (default, false);
        }
        return (HostilityResponse.Attack, true);
    }

    /// <summary>
    /// Reports a work-giver job whose target cell stands within StealthCells of a known-passive hostile with no
    /// engaging hostile within that reach. A job no work giver issued (a forced order, rest, a meal), an unknown
    /// target or an unplaced hostile is never stealth work.
    /// </summary>
    public static bool StealthJob(Fact<PawnJob> job, IReadOnlyList<EmergencyThreat> threats)
    {
        if (!job.TryGetValue(out var j) || string.IsNullOrEmpty(j.Def) || j.Work == WorkType.None)
        {
            return false;
        }
        if (!j.Target.TryGetValue(out var target))
        {
            return false;
        }
        if (!target.Cell.TryGetValue(out var cell))
        {
            return false;
        }
        var passive = false;
        foreach (var t in threats)
        {
            if (t.Dead.TryGetValue(out var dead) && dead)
            {
                continue;
            }
            var (near, placed) = ThreatNear(t, cell);
            if (!placed)
            {
                if (t.Engaging && !t.DistantThreat)
                {
                    return false;
                }
                continue;
            }
            if (!near)
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

    /// <summary>Reports whether a threat stands within StealthCells of cell, and whether its place is known at all.</summary>
    private static (bool Near, bool Placed) ThreatNear(EmergencyThreat t, Cell cell)
    {
        var cells = new List<Cell>();
        if (t.Position.TryGetValue(out var p))
        {
            cells.Add(p);
        }
        cells.AddRange(t.Cells);
        foreach (var c in cells)
        {
            if (Chebyshev(c, cell) <= StealthCells)
            {
                return (true, true);
            }
        }
        return (false, cells.Count > 0);
    }

    /// <summary>The response the pawn should hold now: Ignore on stealth work for a pawn that would otherwise Attack, else its default. A Flee pawn keeps fleeing beside sleeping hostiles.</summary>
    public static (HostilityResponse Response, bool Ok) WantHostility(HostilityPawn p, IReadOnlyList<EmergencyThreat> threats)
    {
        var (mode, ok) = DefaultHostility(p);
        if (!ok)
        {
            return (default, false);
        }
        if (mode == HostilityResponse.Attack && StealthJob(p.Job, threats))
        {
            return (HostilityResponse.Ignore, true);
        }
        return (mode, true);
    }

    /// <summary>The settings to write: every pawn whose known response differs from the one it should hold.</summary>
    public static IReadOnlyList<PawnSettings> HostilityChanges(IReadOnlyList<HostilityPawn> pawns, IReadOnlyList<EmergencyThreat> threats)
    {
        var @out = new List<PawnSettings>();
        foreach (var p in pawns)
        {
            var ck = p.Current.TryGetValue(out var current);
            var (want, ok) = WantHostility(p, threats);
            if (!ck || !ok || current == want)
            {
                continue;
            }
            try
            {
                @out.Add(PawnSettings.NewHostilitySetting((Domain.PawnId)p.ID, want));
            }
            catch (DomainException)
            {
                // an unwritable setting is skipped, as the Go error branch does
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
