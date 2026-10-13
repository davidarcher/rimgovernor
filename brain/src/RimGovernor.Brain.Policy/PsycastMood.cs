using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

// Mood psycasts: a colonist whose mood pressure is active and whose measured need relief is spent (or absent)
// is cast on by a colonist who knows a mood psycast. The mood relief planner owns the call; it is the generic
// Ability action, where native owns the cooldown, psyfocus, neural heat and range guards. Policy holds the
// cast on every unread fact and keeps a psyfocus reserve back for the casters' other work.
//
// Only casts with a clear, safe payoff for an unattended colony are listed here;
// docs/developers/contracts/controller-contracts.md names the vanilla psycasts left undone. Matching is
// case-insensitive on the AbilityDef name.

public static partial class PolicyConstants
{
    internal static readonly IReadOnlyList<string> MoodCasts = ["WordOfJoy"];

    /// <summary>
    /// The Psyfocus (0-1) a caster keeps after a mood cast, so a colonist never spends itself dry on a mood
    /// and keeps what its other psycasts need.
    /// </summary>
    public const double PsycastPsyfocusReserve = 0.25;
}

/// <summary>One caster casting one psycast on one pawn.</summary>
public sealed record PsycastCall(PawnId Caster, string Ability, PawnId Target);

/// <summary>Reports a caster-ability pair whose bounded attempts for the target are spent.</summary>
public delegate bool MoodCastExhausted(PawnId caster, string ability);

public static partial class PolicyFunctions
{
    internal static bool IsMoodCast(string def) =>
        PolicyConstants.MoodCasts.Any(c => string.Equals(c, def, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The mood cast for target, or null. A caster must be a different, available colonist with a read
    /// psyfocus at least the cast's cost plus PsycastPsyfocusReserve, know a pawn-targeted mood psycast whose
    /// cost, neural heat and cooldown the read carries, and not be exhausted for the target. The caster with
    /// the most psyfocus wins, then the lowest id. The pawn's current neural heat and the cast's remaining
    /// cooldown are not read: native refuses a cast that would overflow either, and the bounded attempts count
    /// that refusal.
    /// </summary>
    public static PsycastCall? SelectMoodCast(PawnId target, Fact<RoyaltyFacts> royalty, Fact<IReadOnlyList<WorkPawn>> pawns, MoodCastExhausted? exhausted)
    {
        var rk = royalty.TryGetValue(out var facts);
        var pk = pawns.TryGetValue(out var rows);
        if (!rk || !pk)
        {
            return null;
        }
        PsycastCall? best = null;
        var bestFocus = -1.0;
        foreach (var pawn in rows!)
        {
            if (pawn.Id == target)
            {
                continue;
            }
            var ak = pawn.Available.TryGetValue(out var available);
            var fk = pawn.Psyfocus.TryGetValue(out var focus);
            if (!ak || !available || !fk)
            {
                continue;
            }
            if (!facts!.Psycasts.TryGetValue(pawn.Id, out var casts))
            {
                continue;
            }
            foreach (var cast in casts)
            {
                var ck = cast.PsyfocusCost.TryGetValue(out var cost);
                var ek = cast.Entropy.TryGetValue(out _);
                var dk = cast.CooldownTicks.TryGetValue(out _);
                if (!IsMoodCast(cast.Def) || cast.Target != PsycastTarget.Pawn || !ck || !ek || !dk
                    || focus < cost + PolicyConstants.PsycastPsyfocusReserve || (exhausted is not null && exhausted(pawn.Id, cast.Def)))
                {
                    continue;
                }
                if (focus > bestFocus || (focus == bestFocus && pawn.Id.CompareTo(best!.Caster) < 0))
                {
                    best = new PsycastCall(pawn.Id, cast.Def, target);
                    bestFocus = focus;
                }
                break;
            }
        }
        return bestFocus >= 0 ? best : null;
    }
}
