using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    /// <summary>
    /// F, the part of the personal pool colonists may direct at themselves. It must stay below 1: personal
    /// spend adds to the colony's wealth and so to the pool, and with f &lt; 1 the total converges at
    /// f/(1-f) x the starting pool (0.2 gives a quarter), where f &gt;= 1 would let spending chase its own
    /// growth without bound.
    /// </summary>
    public const double PersonalShareFraction = 0.2;

    // Share weight modifiers over the equal base of 1. A role adds to the claim its owner presses; an
    // Ascetic claims half the base and nothing more.
    internal const double ShareWeightSoldier = 0.25; // gear
    internal const double ShareWeightDoctor = 0.25; // bionics
    internal const double ShareWeightGreedy = 0.5; // bedroom
    internal const double ShareWeightJealous = 0.25; // bedroom
    internal const double ShareWeightAscetic = 0.5;
}

/// <summary>
/// One colonist's input to PersonalShares. Soldier and Doctor come from the caller's roster (SoldierSquad,
/// the surgeon role); Spent is the derived attribution of what the colonist holds, unknown until read. Free is
/// a free colonist; slaves and prisoners are not, and get necessities only.
/// </summary>
public sealed record ShareMember
{
    public PawnProfile Profile { get; init; } = new();

    public bool Free { get; init; }

    public bool Soldier { get; init; }

    public bool Doctor { get; init; }

    public Fact<double> Spent { get; init; }
}

/// <summary>
/// What one colonist may direct at themselves: Share is f x pool x weight over the free colonists' total
/// weight, Spent the derived attribution, and Remaining Share - Spent floored at 0 (an overspent colonist has
/// nothing left, never a negative headroom). Share or Spent unknown leaves Remaining unknown, which Allows
/// refuses. The default value is ungated, so callers that carry no share keep working.
/// </summary>
public sealed record PersonalShare
{
    public Fact<double> Share { get; init; }

    public Fact<double> Spent { get; init; }

    public Fact<double> Remaining { get; init; }

    /// <summary>Exported so a recorded share keeps its gate.</summary>
    public bool Gated { get; init; }

    /// <summary>
    /// Whether the colonist's remaining share covers an upgrade of the given market-value delta. A default
    /// PersonalShare allows everything, and so does a delta of zero or less (necessities are never
    /// charged); otherwise an unknown remaining refuses.
    /// </summary>
    public bool Allows(double delta)
    {
        if (!Gated || delta <= 0)
        {
            return true;
        }
        return Remaining.TryGetValue(out var remaining) && delta <= remaining;
    }

    /// <summary>
    /// The share of a colonist the colony has no share for (an unread pool, spent or roster, a slave or a
    /// prisoner): gated, with every part unknown, so Allows refuses any charged upgrade and only necessities
    /// pass. Unlike the default value it is never ungated.
    /// </summary>
    public static PersonalShare UnknownPersonalShare() =>
        new() { Share = Unknown<double>(), Spent = Unknown<double>(), Remaining = Unknown<double>(), Gated = true };
}

public static class RoundsFactsPersonalExtensions
{
    /// <summary>The elective surgery gate over f's held shares: a colonist with no entry gets UnknownPersonalShare (necessities only); null shares are ungated.</summary>
    public static ElectiveShare ElectiveShareOf(this RoundsFacts f)
    {
        if (f.PersonalShares is null)
        {
            return new ElectiveShare();
        }
        return new ElectiveShare
        {
            Items = f.Items,
            Of = pawn => f.PersonalShares.TryGetValue(pawn, out var s) ? s : PersonalShare.UnknownPersonalShare(),
        };
    }

    /// <summary>The surgery ranking context over f: the hospital bed, the elective gate and the work profiles.</summary>
    public static SurgeryContext SurgeryContextOf(this RoundsFacts f)
    {
        f.WorkProfiles.TryGetValue(out var profiles);
        return new SurgeryContext
        {
            Profiles = profiles,
            HospitalBed = Positive(HospitalBedReady(f.Sleeping)),
            Elective = f.ElectiveShareOf(),
        };
    }

    /// <summary>The pool from f's wealth.</summary>
    public static Fact<double> PersonalPoolOf(this RoundsFacts f) => PersonalPool(f.Wealth);
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The wealth a colony's personal shares divide: items plus buildings, pawns excluded so animals,
    /// prisoners and installed bionics do not inflate it. Unknown unless the wealth fact is known and both
    /// parts are finite and non-negative, like WealthBudget.
    /// </summary>
    public static Fact<double> PersonalPool(Fact<WealthFacts> wealth)
    {
        if (!wealth.TryGetValue(out var w) || !Finite(w.Items) || !Finite(w.Buildings) || w.Items < 0 || w.Buildings < 0)
        {
            return Unknown<double>();
        }
        return Known(w.Items + w.Buildings);
    }

    /// <summary>
    /// The colonist's claim on the pool: an equal base of 1, raised by role (soldier, doctor) and by Greedy
    /// and Jealous, and an Ascetic at half the base.
    /// </summary>
    public static double ShareWeight(ShareMember m)
    {
        var e = m.Profile.Effects;
        if (e.Ascetic)
        {
            return PolicyConstants.ShareWeightAscetic;
        }
        var w = 1.0;
        if (m.Soldier)
        {
            w += PolicyConstants.ShareWeightSoldier;
        }
        if (m.Doctor)
        {
            w += PolicyConstants.ShareWeightDoctor;
        }
        if (e.Greedy)
        {
            w += PolicyConstants.ShareWeightGreedy;
        }
        if (e.Jealous)
        {
            w += PolicyConstants.ShareWeightJealous;
        }
        return w;
    }

    /// <summary>
    /// The colonists whose claim carries the doctor weight: the DoctorsWanted(len(profiles)) most skilled
    /// Medicine doctors among those able to doctor (not backstory-incapable, not a child), best level first
    /// and ties by id.
    /// </summary>
    public static IReadOnlyDictionary<PawnId, bool> ShareDoctors(IReadOnlyList<PawnProfile> profiles)
    {
        var able = profiles
            .Where(p => p.Capable(WorkType.Doctor, 0) && !p.Child)
            .OrderByDescending(p => p.SkillFor(WorkType.Doctor).Level)
            .ThenBy(p => p.Id)
            .ToList();
        var result = new Dictionary<PawnId, bool>();
        var wanted = DoctorsWanted(profiles.Count);
        for (var i = 0; i < able.Count && i < wanted; i++)
        {
            result[able[i].Id] = true;
        }
        return result;
    }

    /// <summary>
    /// Divides the pool among the free members by weight and pairs each member's share with their spent. A
    /// member that is not free gets a zero share. An unknown pool leaves every free member's share and
    /// remaining unknown.
    /// </summary>
    public static IReadOnlyDictionary<PawnId, PersonalShare> PersonalShares(Fact<double> pool, IReadOnlyList<ShareMember> members)
    {
        double total = 0;
        foreach (var m in members)
        {
            if (m.Free)
            {
                total += ShareWeight(m);
            }
        }
        var poolKnown = pool.TryGetValue(out var p);
        poolKnown = poolKnown && Finite(p) && p >= 0;
        var result = new Dictionary<PawnId, PersonalShare>();
        foreach (var m in members)
        {
            var share = Known(0.0);
            if (m.Free)
            {
                share = Unknown<double>();
                if (poolKnown && total > 0)
                {
                    share = Known(PolicyConstants.PersonalShareFraction * p * ShareWeight(m) / total);
                }
            }
            var s = new PersonalShare { Share = share, Spent = m.Spent, Gated = true };
            if (share.TryGetValue(out var sh) && m.Spent.TryGetValue(out var sp) && Finite(sp) && sp >= 0)
            {
                s = s with { Remaining = Known(Math.Max(sh - sp, 0)) };
            }
            result[m.Profile.Id] = s;
        }
        return result;
    }
}
