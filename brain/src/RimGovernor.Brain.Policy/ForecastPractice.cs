using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// A pawn a practice room trains (#3813, #3846): its learning mechanics for the room's skill (SkillLearning, its practice drill
/// capable), the skill's practice ceiling (SkillPractice.Drill.Ceiling) and the DPS it adds to the defense capacity at each stored
/// level (index = stored level): MeleeDPS x health for Melee, ranged DPS at its hit factor for Shooting.
/// </summary>
public sealed record PracticeTrainee(string Pawn, SkillLearning Learning, int Ceiling, EquatableArray<double> Power);

/// <summary>
/// A practice room's trainees (#3813, #3846, PracticeMethodCandidates.TraineesOf): the pawns it trains and the standing practice
/// targets of its skill (sparring markers, range stands).
/// </summary>
public sealed record PracticeTrainees(EquatableArray<PracticeTrainee> Trainees, int Targets);

/// <summary>
/// The raid harm a practice room's trained skill saves (#3813, #3846, epic #3650 P1), shared by the sparring ring
/// (<see cref="ForecastSparring"/>) and the training range (<see cref="ForecastTrainingRange"/>). From the service day to tier
/// 2's horizon, each day the room's practice (its <c>train</c> rule) raises its trainees' skill; without the room the skill only
/// decays. Both run the native skill arithmetic (<see cref="ForecastSkillCursor"/>: decay, the midnight reset, saturation, the
/// direct-learn budget, the XP-to-level table). A trainee's stored level reads its power curve, which moves the defense capacity
/// by <see cref="DefenseCapacityPolicy.DefensePointsPerDps"/> per DPS. The harm ledger (<see cref="HarmLedger"/>) prices the
/// raids at each capacity: a day's credit is its raid loss without the room less with it, shared among the trainees by their
/// added power. Unread mechanics or raid harm raise <see cref="RequiredFactException"/>.
/// </summary>
internal static class ForecastPractice
{
    private static readonly ForecastNeedsResult NoNeeds = new([], default, default, new ForecastCapacityResult(default!, []), []);

    private sealed class Cache
    {
        public readonly ConcurrentDictionary<(string Skill, double ServedAt, ForecastSupportConsequences Support, double Horizon), IReadOnlyList<RoomGain>> Credits = new();
        public readonly ConcurrentDictionary<(ForecastSupportConsequences Support, double Horizon, double Shift), double> Losses = new();
        public readonly ConcurrentDictionary<(string, string, EquatableArray<GearOption>, ForecastSupportConsequences, HitRange), HarmDistribution> Harm = new();
        public readonly ConcurrentDictionary<(string, ForecastSupportConsequences?), HarmDistribution> Illness = new();
    }

    private static readonly ConditionalWeakTable<ForecastCoupledContext, Cache> Caches = new();

    /// <summary>
    /// The credit rows (<see cref="RoomCredit"/>) of a practice room of <paramref name="skill"/> serving at
    /// <paramref name="servedAt"/>: one per trainee whose skill saves harm, under its pawn id. Each day <paramref name="train"/>
    /// practises the cursors of the trainees still below their ceiling (their indices); the tracker's midnight reset, which the
    /// day's tracker learning saturates against, is always read.
    /// </summary>
    internal static IReadOnlyList<RoomGain> Credit(PracticeTrainees practice, ForecastCoupledContext context, double servedAt,
        ForecastSupportConsequences support, double horizon, string skill, Action<ForecastSkillCursor[], int[]> train)
    {
        var credit = RingClosure.CreditDays(context.InvestmentHorizonDays, servedAt);
        if (credit <= 0) return [];
        return Caches.GetValue(context, _ => new()).Credits.GetOrAdd((skill, servedAt, support, horizon),
            key => Gains(practice, context, key.ServedAt, key.Support, key.Horizon, credit, skill, train));
    }

    private static IReadOnlyList<RoomGain> Gains(PracticeTrainees practice, ForecastCoupledContext context, double servedAt, ForecastSupportConsequences support,
        double horizon, double credit, string skill, Action<ForecastSkillCursor[], int[]> train)
    {
        var trainees = practice.Trainees.ToArray();
        foreach (var trainee in trainees)
        {
            if (ForecastSkillPolicy.MechanicsGap(trainee.Learning) is { } gap) throw new RequiredFactException(skill.ToLowerInvariant() + " learning " + trainee.Pawn + ": " + gap);
            if (trainee.Power.Count != trainee.Learning.Levels.Count) throw new RequiredFactException(skill.ToLowerInvariant() + " power curve " + trainee.Pawn);
        }
        var without = trainees.Select(t => new ForecastSkillCursor(t.Learning)).ToArray();
        var with = trainees.Select(t => new ForecastSkillCursor(t.Learning)).ToArray();
        var observed = without.Select((c, i) => trainees[i].Power[c.StoredLevel]).ToArray();
        var saved = new double[trainees.Length];
        var end = context.InvestmentHorizonDays * GameTime.TicksPerDay;
        for (var at = Math.Ceiling(servedAt); at < end; at += GameTime.TicksPerDay)
        {
            var span = Math.Min(GameTime.TicksPerDay, end - at) / GameTime.TicksPerDay;
            var added = new double[trainees.Length];
            double shiftWithout = 0, shiftWith = 0;
            for (var i = 0; i < trainees.Length; i++)
            {
                without[i].Advance((int)at); with[i].Advance((int)at);
                var power = trainees[i].Power;
                shiftWithout += DefenseCapacityPolicy.DefensePointsPerDps * (power[without[i].StoredLevel] - observed[i]);
                shiftWith += DefenseCapacityPolicy.DefensePointsPerDps * (power[with[i].StoredLevel] - observed[i]);
                added[i] = Math.Max(0, DefenseCapacityPolicy.DefensePointsPerDps * (power[with[i].StoredLevel] - power[without[i].StoredLevel]));
            }
            var delta = span * (LossPerDay(context, support, horizon, shiftWithout) - LossPerDay(context, support, horizon, shiftWith));
            var total = added.Sum();
            if (delta > 0 && total > 0)
                for (var i = 0; i < trainees.Length; i++) saved[i] += delta * added[i] / total;
            train(with, [.. Enumerable.Range(0, trainees.Length).Where(i => with[i].Effective < trainees[i].Ceiling)]);
        }
        return [.. Enumerable.Range(0, trainees.Length).Where(i => saved[i] > 0).Select(i => new RoomGain(trainees[i].Pawn, trainees[i].Pawn, saved[i] / credit))];
    }

    /// <summary>The raids' loss a day at the observed defense capacity plus <paramref name="shift"/> raid points.</summary>
    private static double LossPerDay(ForecastCoupledContext context, ForecastSupportConsequences support, double horizon, double shift)
    {
        var cache = Caches.GetValue(context, _ => new());
        return cache.Losses.GetOrAdd((support, horizon, shift), key =>
        {
            var rows = context.Needs.Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var stub = new ForecastCoupledOutcome(new([], 0, 0), default, rows.Keys.ToDictionary(p => p, _ => NoNeeds, StringComparer.Ordinal), []) { Support = key.Support };
            var term = HarmLedger.Of(stub, context, key.Horizon, cache.Harm, rows, null, cache.Illness, key.Shift);
            return term.Detail is { } detail ? detail.RaidLoss / (key.Horizon / GameTime.TicksPerDay)
                : throw new RequiredFactException("raid harm: " + string.Join(", ", term.Findings));
        });
    }
}
