using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

// The nursery credit (#3854, epic #3650 P1). Confirmed with ilspycmd on Assembly-CSharp.dll (1.7) and the Biotech defs:
// - MentalFitGenerator.TickInterval: an awake pawn out of any mental state, CheckInterval (150) ticks apart, tries each MentalFitDef
//   in def order and starts the first whose Rand.MTBEventOccurs(CalculateMTBDays, 60000, 150) fires. CalculateMTBDays is the def's
//   mtbDaysMoodCurve at Need_Mood.CurLevelPercentage (Crying first, then Giggling; Baby stage only, so no newborn fits). After any
//   mental state the generator waits MinTicksSinceRecoveryToHaveFit (600) awake ticks.
// - A baby fit (BaseBabyMentalFit) lasts exactly maxTicksBeforeRecovery (1200, min = max, recoveryMtbDays Infinity). Every 150
//   ticks MentalState_BabyFit calls GenClamor.DoClamor(baby, BabyScreamRadius 9.9): a region walk through open doors only, at most
//   15 regions, reaching every pawn within 9.9 × its Hearing. Each hearer gains the fit's memory once (alreadyHeard): a cry gives
//   CryingBaby (−8, MyCryingBaby −4 for a parent), a giggle GigglingBaby (+4, MyGigglingBaby +8), each a quarter day, stackLimit 3,
//   stackedEffectMultiplier 0.8.
// - MemoryThoughtHandler.TryGainMemory renews the oldest of a baby's group once it holds stackLimit, and drops a def's oldest once
//   the def holds more than stackLimit: a hearer holds the latest min(n, stackLimit) memories of a def from the n heard within one
//   duration, whichever babies they came from. ThoughtHandler.MoodOffsetOfGroup counts a baby's k of them as offset × Σ_{i<k} 0.8^i.

/// <summary>
/// The Nursery room credit (#3854): a crib's rest compared with the baby's other sleeping surface, and the crying and giggling
/// it changes for the colonists. A baby fits at the rates its mood
/// gives (<see cref="FitRates"/>); each colonist who hears it gains the fit's memory, stacked as the game stacks them
/// (<see cref="HeardMood"/>). Who hears a baby is the native sound-reach read now (<see cref="BabyRead.Hearers"/>). Before a
/// nursery stands, its baby is heard as now without the room and by nobody with it (behind its closed door, with no colonist inside);
/// a baby in a standing nursery is heard as now with the room and as with that nursery's doors open without it. Each colonist is
/// credited <see cref="PlannedRoomCredits.MoodGain"/> of its mood with the room less its mood without it: a crying baby's room gains,
/// a giggling baby's loses the giggles. The lowest-id planned nursery takes every baby (the layout sizes its cribs for them all).
/// </summary>
public static class ForecastNursery
{
    /// <summary>The named state of a colony with no baby a fit def admits: no fit is heard.</summary>
    public const string NoBaby = "nursery_no_baby";

    /// <summary>The named state of babies none of whose fits can start (no mood, or a state its worker refuses, as a baby unable to talk).</summary>
    public const string NoFit = "nursery_no_fit";

    /// <summary>The named state of babies whose heard cry and giggle mood is unchanged with the nursery.</summary>
    public const string Unheard = "nursery_unheard";

    /// <summary>The named state of a nursery whose babies a lower-id planned nursery takes.</summary>
    public const string Housed = "nursery_babies_housed";

    /// <summary>The prefix of a baby fit whose mental state the read does not know: its hearers' memories are not priced.</summary>
    public const string UnknownFit = "nursery_fit_hearers_not_modelled:";

    /// <summary>The mechanics the credit leaves out, named with each served nursery.</summary>
    public static readonly EquatableArray<string> NotModelled =
    [
        "nursery_play", // #3940: the toy box's BabyPlayGainFactor and a BabyDecoration's: the caretaker ticks they save
        "nursery_baby_mood", // #3941: outcome-dependent room/rest mood trajectories beyond the crib comfort difference
        "nursery_disturbed_sleep", // #3942: a cry's SleepDisturbed memory on a sleeping colonist in reach
        "nursery_baby_opinion", // #3943: the BabyCriedSocial and BabyGiggledSocial opinion memories
        "nursery_hearers_move", // #3944: colonists held where they stand now; a colonist who enters the nursery hears its babies
        "nursery_other_hearers", // #3945: prisoners, guests and slaves in reach
    ];

    /// <summary>The credit (<see cref="RoomCredit"/>): one row per baby whose sleep changes or colonist whose heard-fit mood changes.</summary>
    public static IReadOnlyList<RoomGain> NurseryCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        if (Taker(context) != room.Room) return [];
        var read = Cache.GetValue(context, Of);
        return [.. read.Moods.Keys.Concat(read.Rest.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(pawn => new RoomGain(pawn, pawn, (read.Moods.TryGetValue(pawn, out var mood)
                ? PlannedRoomCredits.MoodGain(mood.With, pawn, outcome, context, horizon)
                    - PlannedRoomCredits.MoodGain(mood.Without, pawn, outcome, context, horizon) : 0) + read.Rest.GetValueOrDefault(pawn)))];
    }

    /// <summary>The findings (<see cref="RoomFindings"/>): what the credit leaves out and the room's named states.</summary>
    public static IEnumerable<string> Findings(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        Taker(context) != room.Room ? [.. NotModelled, Housed] : [.. NotModelled, .. Cache.GetValue(context, Of).States];

    /// <summary>Per colonist, the distinct nonzero shifts (mood / 100) its timeline integrates breaks at for the nursery credit.</summary>
    internal static IReadOnlyDictionary<string, double[]> Shifts(ForecastCoupledContext context) => Cache.GetValue(context, Of).Shifts;

    /// <summary>
    /// Per fit def a baby can have (<paramref name="fits"/>, def order kept from <paramref name="read"/>), its fits a tick at mood
    /// <paramref name="mood"/> awake <paramref name="awake"/> of the time. A check fires fit k with the chance the earlier defs did
    /// not fire times its own, interval / (mtb × a day) at most 1. Between fits the baby waits interval / (the chance any fires) awake
    /// ticks, then fits for the fired def's duration, then the generator's recovery awake ticks: a fit k a tick is its share of the
    /// fires over that mean cycle.
    /// </summary>
    public static IReadOnlyDictionary<string, double> FitRates(BabyFitsRead read, IReadOnlyCollection<string> fits, double mood, double awake)
    {
        DomainException.Require(read.CheckIntervalTicks > 0 && read.RecoveryAwakeTicks >= 0 && awake is >= 0 and <= 1 && mood is >= 0 and <= 1, "baby fit rates");
        var defs = read.Fits.Where(f => fits.Contains(f.Def)).ToArray();
        var fire = new double[defs.Length];
        var none = 1d;
        for (var k = 0; k < defs.Length; k++)
        {
            var mtb = Curve(defs[k].MtbDaysByMood, mood, defs[k].Def);
            var chance = mtb <= 0 ? 1 : double.IsPositiveInfinity(mtb) ? 0 : Math.Min(1, read.CheckIntervalTicks / (mtb * GameTime.TicksPerDay));
            fire[k] = none * chance;
            none *= 1 - chance;
        }

        var any = 1 - none;
        var rates = defs.ToDictionary(d => d.Def, _ => 0d, StringComparer.Ordinal);
        if (any <= 0 || awake <= 0) return rates;
        var cycle = (read.CheckIntervalTicks / any + read.RecoveryAwakeTicks) / awake + defs.Select((d, k) => fire[k] / any * d.DurationTicks).Sum();
        for (var k = 0; k < defs.Length; k++) rates[defs[k].Def] = fire[k] / any / cycle;
        return rates;
    }

    /// <summary>
    /// The mean mood the heard fits give one colonist (#3854): per memory def, the n of it heard within one duration are Poisson
    /// at the summed rate of its babies' fits times the duration; the latest min(n, stackLimit) count, each a baby's with the chance
    /// of its share of the rate, and a baby's k of them count offset × Σ_{i&lt;k} multiplier^i (no stackLimit: each baby's own
    /// Poisson count). Each entry is a fit's memory and that fit's rate a tick from one baby.
    /// </summary>
    public static double HeardMood(IReadOnlyList<(HeardMemoryRead Memory, double Rate)> heard)
    {
        var mood = 0d;
        foreach (var pool in heard.Where(h => h.Rate > 0).GroupBy(h => h.Memory.Thought, StringComparer.Ordinal))
        {
            var entries = pool.ToArray();
            var memory = entries[0].Memory;
            DomainException.Require(entries.All(e => e.Memory.DurationTicks == memory.DurationTicks && e.Memory.StackLimit == memory.StackLimit
                && e.Memory.StackedMultiplier == memory.StackedMultiplier) && memory.DurationTicks > 0, "heard memory " + memory.Thought);
            var (limit, multiplier) = (memory.StackLimit, memory.StackedMultiplier);
            double Stack(int k) => Enumerable.Range(0, k).Sum(i => Math.Pow(multiplier, i));
            if (limit < 0)
            {
                foreach (var (m, rate) in entries)
                {
                    var lambda = rate * memory.DurationTicks;
                    mood += m.Offset * (multiplier == 1 ? lambda : (1 - Math.Exp(-lambda * (1 - multiplier))) / (1 - multiplier));
                }

                continue;
            }

            var total = entries.Sum(e => e.Rate);
            var mean = total * memory.DurationTicks;
            var counts = new double[limit + 1];
            var pmf = Math.Exp(-mean);
            for (var n = 0; n < limit; n++) { counts[n] = pmf; pmf *= mean / (n + 1); }
            counts[limit] = Math.Max(0, 1 - counts.Take(limit).Sum());
            foreach (var (m, rate) in entries)
            {
                var share = rate / total;
                var expected = 0d;
                for (var n = 1; n <= limit; n++)
                    for (var k = 1; k <= n; k++) expected += counts[n] * Binomial(n, k) * Math.Pow(share, k) * Math.Pow(1 - share, n - k) * Stack(k);
                mood += m.Offset * expected;
            }
        }

        return mood;
    }

    private static double Binomial(int n, int k) => Enumerable.Range(1, k).Aggregate(1d, (c, i) => c * (n - k + i) / i);

    private static double Curve(EquatableArray<CurvePointRead> points, double x, string fit) => points.Count != 0
        ? LayoutFilthRules.EvaluateCurve([.. points.Select(p => new CurvePoint(p.X, p.Y))], x) : throw new RequiredFactException("baby fit mood curve:" + fit);

    /// <summary>The planned priced nursery that takes the babies: the lowest id; 0 for none.</summary>
    private static int Taker(ForecastCoupledContext context) =>
        context.RoomCredits.ContainsKey(PlannedRole.Nursery) ? context.PlannedRoomRoles.Where(r => r.Value == PlannedRole.Nursery).Select(r => r.Key).DefaultIfEmpty(0).Min() : 0;

    /// <summary>Per colonist its heard mood with the nursery and without it, its break shifts, and the taker's named states.</summary>
    private sealed record Heard(IReadOnlyDictionary<string, (double With, double Without)> Moods, IReadOnlyDictionary<string, double[]> Shifts, string[] States)
    {
        public IReadOnlyDictionary<string, double> Rest { get; init; } = new Dictionary<string, double>();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, Heard> Cache = new();

    private static Heard Of(ForecastCoupledContext context)
    {
        var read = context.Needs.Babies;
        var states = new SortedSet<string>(read.Fits.Where(f => !f.Heard).Select(f => UnknownFit + f.Def), StringComparer.Ordinal);
        if (Taker(context) == 0) return new(new Dictionary<string, (double, double)>(), new Dictionary<string, double[]>(), []);
        if (read.Babies.Count == 0) return new(new Dictionary<string, (double, double)>(), new Dictionary<string, double[]>(), [NoBaby, .. states]);
        var heard = read.Fits.Where(f => f.Heard).Select(f => f.Def).ToHashSet(StringComparer.Ordinal);
        var with = new Dictionary<string, List<(HeardMemoryRead, double)>>(StringComparer.Ordinal);
        var without = new Dictionary<string, List<(HeardMemoryRead, double)>>(StringComparer.Ordinal);
        var ownMood = new Dictionary<string, (double With, double Without)>(StringComparer.Ordinal);
        var restDays = new Dictionary<string, double>(StringComparer.Ordinal);
        var beds = context.PlannedBeds.GetValueOrDefault(Taker(context)) ?? [];
        var babyIndex = 0;
        static void Add(Dictionary<string, List<(HeardMemoryRead, double)>> into, string pawn, HeardMemoryRead memory, double rate)
        {
            if (!into.TryGetValue(pawn, out var list)) into[pawn] = list = [];
            list.Add((memory, rate));
        }

        foreach (var baby in read.Babies)
        {
            var row = context.Needs.Pawns.FirstOrDefault(p => p.Pawn == baby.Pawn) ?? throw new RequiredFactException("baby needs:" + baby.Pawn);
            var mood = row.Mood;
            if (mood is null && baby.Fits.Count != 0) throw new RequiredFactException("baby mood:" + baby.Pawn);
            var rest = row.Needs.FirstOrDefault(n => n.Kind == "rest") ?? throw new RequiredFactException("baby rest:" + baby.Pawn);
            var now = baby.SleepWithoutNursery ?? throw new RequiredFactException("baby alternate sleeping surface:" + baby.Pawn);
            var planned = babyIndex < beds.Count ? beds[babyIndex] ?? throw new RequiredFactException("nursery crib stats:" + baby.Pawn) : null;
            babyIndex++;
            var standing = planned is null && baby.InNursery ? row.Sleep ?? throw new RequiredFactException("nursery sleeping surface:" + baby.Pawn) : null;
            var (gain, comfortLevel) = planned is not null ? (rest.GainPerTickPerEffectiveness * planned.RestEffectiveness, planned.Comfort)
                : standing is not null ? (standing.GainPerTick, standing.Comfort)
                : throw new RequiredFactException("nursery planned crib:" + baby.Pawn);
            if (!double.IsFinite(rest.FallPerTick) || rest.FallPerTick < 0 || !double.IsFinite(now.GainPerTick) || now.GainPerTick <= 0
                || !double.IsFinite(gain) || gain <= 0)
                throw new RequiredFactException("baby rest rates:" + baby.Pawn);
            var freed = ForecastHousing.RestDaysFreed(rest, now.GainPerTick, gain);
            if (freed != 0) restDays[baby.Pawn] = freed;
            // Comfort is separate from rest effectiveness. Vanilla babies have no comfort need (NeedDef's default
            // stage filter is Child|Adult); an absent comfort thought therefore changes neither mood nor fit rate.
            var comfort = ForecastHousing.ComfortMood(row, comfortLevel, context.ComfortCurve)
                - ForecastHousing.ComfortMood(row, now.Comfort, context.ComfortCurve);
            if (comfort != 0) ownMood[baby.Pawn] = baby.InNursery ? (0, -comfort) : (comfort, 0);
            else if (!row.ThoughtStages.Any(t => t.Role == ForecastHousing.NeedComfortRole)) states.Add("nursery_baby_no_comfort_need");
            if (baby.Fits.Count == 0) continue;
            var rates = FitRates(read, [.. baby.Fits], Math.Clamp(mood!.Target - (baby.InNursery ? comfort / 100 : 0), 0, 1), now.GainPerTick / (now.GainPerTick + rest.FallPerTick));
            var cribRates = FitRates(read, [.. baby.Fits], Math.Clamp(mood.Target + (baby.InNursery ? 0 : comfort / 100), 0, 1), gain / (gain + rest.FallPerTick));
            foreach (var hearer in baby.Hearers)
                foreach (var memory in hearer.Memories.Where(m => heard.Contains(m.Fit) && rates.ContainsKey(m.Fit)))
                {
                    if (baby.InNursery && hearer.Now) Add(with, hearer.Pawn, memory, cribRates[memory.Fit]);
                    if (baby.InNursery ? hearer.NurseryOpen : hearer.Now) Add(without, hearer.Pawn, memory, rates[memory.Fit]);
                }
        }

        if (read.Babies.All(b => b.Fits.Count == 0)) states.Add(NoFit);
        var moods = new Dictionary<string, (double With, double Without)>(StringComparer.Ordinal);
        foreach (var pawn in with.Keys.Concat(without.Keys).Distinct(StringComparer.Ordinal))
        {
            var change = (With: HeardMood(with.GetValueOrDefault(pawn) ?? []), Without: HeardMood(without.GetValueOrDefault(pawn) ?? []));
            if (change.With != change.Without) moods[pawn] = change;
        }

        if (moods.Count == 0 && !states.Contains(NoFit)) states.Add(Unheard);
        foreach (var (pawn, delta) in ownMood)
        {
            var heardMood = moods.GetValueOrDefault(pawn);
            moods[pawn] = (heardMood.With + delta.With, heardMood.Without + delta.Without);
        }
        var shifts = moods.ToDictionary(m => m.Key, m => new[] { m.Value.With / 100, m.Value.Without / 100 }.Where(s => s != 0).Distinct().Order().ToArray(), StringComparer.Ordinal);
        return new(moods, shifts, [.. states]) { Rest = restDays };
    }
}
