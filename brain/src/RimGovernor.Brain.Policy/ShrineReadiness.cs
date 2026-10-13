using DomainPawnId = RimGovernor.Brain.Domain.PawnId;

namespace RimGovernor.Brain.Policy;

// Breaching a sealed shrine releases its guards at once, so the gate is judged before the wall goes, never
// during the fight. Every hold is a journal reason; Ready names the wall and the squad the breach goal drafts.
// Peaceful softens the gate: that storyteller never spawns hostile mechanoids (1.3+), so the squad and trap
// floors drop.

/// <summary>
/// One colonist the breach could draft: the squad eligibility SelectSquadDefense uses plus the longest range
/// the pawn's ranged weapon reaches (unknown or zero for a melee pawn). The squad-defender facts it extends
/// are the squad slice's record, which must not be sealed.
/// </summary>
public sealed record ShrineDefenderFacts : SquadDefenderFacts
{
    public Fact<double> WeaponRange { get; init; }
}

public sealed record ShrineReadinessRequest
{
    public AncientShrine Shrine { get; init; } = new();

    /// <summary>The storyteller's current reading; unknown holds.</summary>
    public Fact<double> RaidPoints { get; init; }

    /// <summary>True under the Peaceful storyteller; unknown counts as not.</summary>
    public Fact<bool> Peaceful { get; init; }

    public EquatableArray<ShrineDefenderFacts> Squad { get; init; }

    /// <summary>The built spike traps' cells.</summary>
    public EquatableArray<Cell> Traps { get; init; }

    public Cell Center { get; init; }

    /// <summary>Any active emergency fact; a breach never starts under one.</summary>
    public bool Emergency { get; init; }
}

public sealed record ShrineReadiness
{
    public bool Ready { get; init; }

    public string Reason { get; init; } = "";

    /// <summary>The chosen breach wall: the candidate nearest the colony centre.</summary>
    public ShrineBreachWall Wall { get; init; } = new();

    /// <summary>Every eligible defender, ranged first.</summary>
    public EquatableArray<DomainPawnId> Squad { get; init; }

    /// <summary>Counts the built traps within ShrineTrapRadius of the wall's outside cell.</summary>
    public int Traps { get; init; }
}

public static partial class PolicyFunctions
{
    public const string ShrineHoldNotSealed = "not_sealed";
    public const string ShrineHoldNoBreachWall = "no_breach_wall";
    public const string ShrineHoldEmergency = "emergency_active";
    public const string ShrineHoldSquadTooSmall = "squad_too_small";
    public const string ShrineHoldNoRanged = "no_ranged";
    public const string ShrineHoldNoTraps = "no_traps";
    public const string ShrineHoldThreatUnknown = "threat_unknown";
    public const string ShrineHoldThreatTooHigh = "threat_too_high";

    internal const int ShrineSquadMinimum = 2;
    internal const int ShrineSquadMinimumPeaceful = 1;
    internal const double ShrineRangedMinimumRange = 20.0;
    internal const int ShrineTrapMinimum = 3;
    internal const double ShrineTrapRadius = 12.0;

    /// <summary>The most raid points a squad of the given size breaches under; larger squads read the last row.</summary>
    private static readonly (int Squad, double Points)[] ShrineThreatCeiling = [(2, 300), (3, 500), (4, 800)];

    internal static bool ShrineDefenderEligible(ShrineDefenderFacts d)
    {
        if (!SquadDefenderEligible(d))
        {
            return false;
        }
        return d.Armed.TryGetValue(out var armed) && armed;
    }

    internal static bool ShrineRanged(ShrineDefenderFacts d)
    {
        var rk = d.RangedEquipped.TryGetValue(out var ranged);
        var ok = d.WeaponRange.TryGetValue(out var reach);
        return rk && ranged && ok && !double.IsNaN(reach) && reach >= ShrineRangedMinimumRange;
    }

    /// <summary>Judges one shrine. It is a decision, never an order; the breach goal re-reads the facts before drafting anyone.</summary>
    public static ShrineReadiness ShrineBreachReadiness(ShrineReadinessRequest r)
    {
        if (!r.Shrine.Sealed)
        {
            return new ShrineReadiness { Reason = ShrineHoldNotSealed };
        }
        if (r.Shrine.BreachWalls.Count == 0)
        {
            return new ShrineReadiness { Reason = ShrineHoldNoBreachWall };
        }
        var wall = r.Shrine.BreachWalls
            .OrderBy(w => SquaredDistance(w.Outside, r.Center))
            .ThenBy(w => w.EntityId, StringComparer.Ordinal)
            .First();
        var traps = 0;
        foreach (var trap in r.Traps)
        {
            if (Math.Sqrt((double)SquaredDistance(trap, wall.Outside)) <= ShrineTrapRadius)
            {
                traps++;
            }
        }
        r.Peaceful.TryGetValue(out var peaceful);
        var ranged = 0;
        var shooters = new List<DomainPawnId>();
        var others = new List<DomainPawnId>();
        foreach (var d in r.Squad)
        {
            if (!ShrineDefenderEligible(d))
            {
                continue;
            }
            if (ShrineRanged(d))
            {
                ranged++;
                shooters.Add(d.Id);
            }
            else
            {
                others.Add(d.Id);
            }
        }
        shooters.Sort();
        others.Sort();
        var squad = shooters.Concat(others).ToList();
        var output = new ShrineReadiness { Wall = wall, Traps = traps, Squad = EquatableArray.From(squad) };
        if (r.Emergency)
        {
            return output with { Reason = ShrineHoldEmergency };
        }
        var minimum = peaceful ? ShrineSquadMinimumPeaceful : ShrineSquadMinimum;
        var minTraps = peaceful ? 0 : ShrineTrapMinimum;
        if (squad.Count < minimum)
        {
            return output with { Reason = ShrineHoldSquadTooSmall };
        }
        if (ranged == 0)
        {
            return output with { Reason = ShrineHoldNoRanged };
        }
        if (traps < minTraps)
        {
            return output with { Reason = ShrineHoldNoTraps };
        }
        if (!r.RaidPoints.TryGetValue(out var points) || double.IsNaN(points) || double.IsInfinity(points))
        {
            if (peaceful)
            {
                return output with { Ready = true };
            }
            return output with { Reason = ShrineHoldThreatUnknown };
        }
        if (points > ShrineSquadCeiling(squad.Count) && !peaceful)
        {
            return output with { Reason = ShrineHoldThreatTooHigh };
        }
        return output with { Ready = true };
    }

    /// <summary>The most raid points a squad of size breaches (or opens caskets) under.</summary>
    internal static double ShrineSquadCeiling(int size)
    {
        foreach (var row in ShrineThreatCeiling)
        {
            if (size <= row.Squad)
            {
                return row.Points;
            }
        }
        return ShrineThreatCeiling[^1].Points;
    }
}
