using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// A pawn a sparring ring trains (#3813): its Melee learning mechanics (SkillLearning, its PracticeCapable true), the sparring
/// ceiling (SkillLearning.PracticeCeiling), its MeleeDPS at each stored Melee level (the biography's MeleeDPS curve, index =
/// stored level) and its health (PawnHealth.SummaryFraction, which scales its melee power in the defense capacity).
/// </summary>
public sealed record SparringTrainee(string Pawn, SkillLearning Learning, int Ceiling, EquatableArray<double> MeleeDps, double Health);

/// <summary>
/// The sparring ring's trainees (#3813, PracticeMethodCandidates.TraineesOf): the pawns it trains, the capable pawns whose
/// skills, health or ceiling were not read, and the standing Melee practice targets (a colony with two already spars).
/// </summary>
public sealed record SparringTrainees(EquatableArray<SparringTrainee> Trainees, EquatableArray<string> Unread, int Markers);

/// <summary>
/// The SparringRing credit (#3813, epic #3650 P1): the raid harm the Melee its trainees gain saves. From its service day to
/// tier 2's horizon, each day every trainee below the sparring ceiling spars to the game's daily cap, today's XP reaching
/// SkillRecord.MaxFullRateXpPerDay (SkillLearning.SaturationThreshold) at the pawn's learn rate, while two are still below it
/// (a bout needs a partner); without the ring its Melee only decays. Both are the native skill arithmetic
/// (<see cref="ForecastSkillCursor"/>: decay, the midnight reset, saturation, the XP-to-level table). A trainee's stored
/// level reads its MeleeDPS curve, and MeleePower = MeleeDPS x health moves the defense capacity by
/// <see cref="DefenseCapacityPolicy.DefensePointsPerDps"/> per DPS. The harm ledger (<see cref="HarmLedger"/>) prices the
/// raids at each capacity: a day's credit is its raid loss without the ring less with it, shared among the trainees by their
/// added melee power. Only the lowest-id planned ring takes the trainees. Fewer than two trainees (nobody to train with), two
/// standing Melee targets (the colony already spars) or no raid to expect earn nothing; unread trainees, mechanics or raid
/// harm raise <see cref="RequiredFactException"/>.
/// </summary>
internal static class ForecastSparring
{
    /// <summary>
    /// The mechanics the credit leaves out, named with every served ring: sparring injuries, the bout's recreation, the work time
    /// practice takes (the sessions' own scheduling prices that), and an Ideology role's skill requirement (named per role by
    /// PracticeMethodCandidates).
    /// </summary>
    internal static readonly string[] Unmodelled =
    [
        "sparring_injury_unmodelled", "sparring_recreation_unmodelled", "sparring_work_time_unmodelled",
    ];

    private static readonly ForecastNeedsResult NoNeeds =new([], default, default, new ForecastCapacityResult(default!, []), []);

    private sealed class Cache
    {
        public readonly ConcurrentDictionary<(double ServedAt, ForecastSupportConsequences Support, double Horizon), IReadOnlyList<RoomGain>> Credits = new();
        public readonly ConcurrentDictionary<(ForecastSupportConsequences Support, double Horizon, double Shift), double> Losses = new();
        public readonly ConcurrentDictionary<(string, string, EquatableArray<GearOption>, ForecastSupportConsequences, HitRange), HarmDistribution> Harm = new();
        public readonly ConcurrentDictionary<(string, ForecastSupportConsequences?), HarmDistribution> Illness = new();
    }

    private static readonly ConditionalWeakTable<ForecastCoupledContext, Cache> Caches = new();

    /// <summary>The ring's credit (<see cref="RoomCredit"/>): one row per trainee whose Melee saves harm, under its pawn id.</summary>
    internal static IReadOnlyList<RoomGain> RingCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        if (context.PlannedRoomRoles.Where(p => p.Value == PlannedRole.SparringRing).Min(p => p.Key) != room.Room) return [];
        var sparring = context.Sparring ?? throw new RequiredFactException("work pawns");
        if (sparring.Unread.Count > 0) throw new RequiredFactException("sparring trainees: " + string.Join(", ", sparring.Unread));
        if (sparring.Trainees.Count < 2 || sparring.Markers >= 2 || horizon <= 0) return [];
        var credit = RingClosure.CreditDays(context.InvestmentHorizonDays, room.ServedAt);
        if (credit <= 0) return [];
        return Caches.GetValue(context, _ => new()).Credits.GetOrAdd((room.ServedAt, outcome.Support, horizon), key => Gains(sparring, context, key.ServedAt, key.Support, key.Horizon, credit));
    }

    private static IReadOnlyList<RoomGain> Gains(SparringTrainees sparring, ForecastCoupledContext context, double servedAt, ForecastSupportConsequences support, double horizon, double credit)
    {
        var trainees = sparring.Trainees.ToArray();
        foreach (var trainee in trainees)
        {
            if (ForecastSkillPolicy.MechanicsGap(trainee.Learning) is { } gap) throw new RequiredFactException("melee learning " + trainee.Pawn + ": " + gap);
            if (trainee.MeleeDps.Count != trainee.Learning.Levels.Count) throw new RequiredFactException("MeleeDPS curve " + trainee.Pawn);
        }
        var without = trainees.Select(t => new ForecastSkillCursor(t.Learning)).ToArray();
        var with = trainees.Select(t => new ForecastSkillCursor(t.Learning)).ToArray();
        if (with.Any(c => !c.ResetsDaily)) throw new RequiredFactException("melee midnight reset");
        var observed = without.Select((c, i) => trainees[i].MeleeDps[c.StoredLevel]).ToArray();
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
                var points = DefenseCapacityPolicy.DefensePointsPerDps * trainees[i].Health;
                shiftWithout += points * (trainees[i].MeleeDps[without[i].StoredLevel] - observed[i]);
                shiftWith += points * (trainees[i].MeleeDps[with[i].StoredLevel] - observed[i]);
                added[i] = Math.Max(0, points * (trainees[i].MeleeDps[with[i].StoredLevel] - trainees[i].MeleeDps[without[i].StoredLevel]));
            }
            var delta = span * (LossPerDay(context, support, horizon, shiftWithout) - LossPerDay(context, support, horizon, shiftWith));
            var total = added.Sum();
            if (delta > 0 && total > 0)
                for (var i = 0; i < trainees.Length; i++) saved[i] += delta * added[i] / total;
            // The day's bouts: every trainee still below the ceiling spars to the daily cap while it has a partner.
            var below = Enumerable.Range(0, trainees.Length).Where(i => with[i].Effective < trainees[i].Ceiling).ToArray();
            if (below.Length >= 2)
                foreach (var i in below) with[i].LearnToSaturation();
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
