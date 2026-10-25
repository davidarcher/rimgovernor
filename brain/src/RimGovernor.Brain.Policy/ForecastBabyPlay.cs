using System.Globalization;
using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Baby play's caretaker time. Exposure is bounded across native walking and toy-drop locations;
/// the room receives the guaranteed saving, with both ends disclosed. No stationary walking multiplier is assumed.</summary>
internal static class ForecastBabyPlay
{
    internal readonly record struct Credit(string Carer, double Lower, double Upper);
    private readonly record struct CareRange(double WithLower, double WithUpper, double WithoutLower, double WithoutUpper);

    /// <summary>WorkGiver_PlayWithBaby chooses the first viable def in random order: uniform per job,
    /// not uniform per tick. Refill from .15 to .99 takes D/(g-f) ticks, then D/f ticks before the next job.
    /// The common deficit cancels. Averaging gain rates would underprice slow jobs.</summary>
    internal static double CareShare(double fall, IReadOnlyList<double> gains)
    {
        if (!double.IsFinite(fall) || fall < 0 || gains.Count == 0 || gains.Any(g => !double.IsFinite(g) || g <= fall))
            throw new RequiredFactException("baby play refill rates");
        var relativeTime = fall * gains.Average(g => 1 / (g - fall));
        return relativeTime / (1 + relativeTime);
    }

    internal static Credit? Of(BabyRead baby, int index, int room, ForecastCoupledContext context, ISet<string> findings)
    {
        if (baby.Play is not { } play) { findings.Add("nursery_baby_no_play_need"); return null; }
        var mechanics = context.Needs.Babies.Play ?? throw new RequiredFactException("baby play mechanics");
        if (play.Carers.Count == 0) { findings.Add("nursery_no_play_carer"); return null; }
        var carers = play.OngoingCarer.Length == 0 ? play.Carers.ToArray() : play.Carers.Where(c => c.Pawn == play.OngoingCarer).ToArray();
        if (carers.Length == 0) throw new RequiredFactException("baby active play carer:" + play.OngoingCarer);
        var credits = carers.Select(c => ForCarer(baby, index, room, context, mechanics, c)).ToArray();
        // Without an observed allocation the two scenarios may choose different carers. Bound their
        // time separately, rather than pairing an invented same-carer assignment across scenarios.
        var credit = new Credit(play.OngoingCarer, credits.Min(c => c.WithoutLower) - credits.Max(c => c.WithUpper),
            credits.Max(c => c.WithoutUpper) - credits.Min(c => c.WithLower));
        findings.Add(play.OngoingCarer.Length == 0 ? "nursery_play_carer_unassigned:" + baby.Pawn : "nursery_play_carer:" + baby.Pawn + ":" + play.OngoingCarer);
        findings.Add("nursery_play_savings:" + baby.Pawn + ":" + credit.Lower.ToString("R", CultureInfo.InvariantCulture) + ".." + credit.Upper.ToString("R", CultureInfo.InvariantCulture));
        if (credit.Lower != credit.Upper) findings.Add("nursery_play_exposure_bounded");
        return credit;
    }

    private static CareRange ForCarer(BabyRead baby, int index, int room, ForecastCoupledContext context, BabyPlayMechanicsRead mechanics, BabyPlayCarerRead carer)
    {
        var play = baby.Play!;
        if (!context.Needs.Pawns.Any(p => p.Pawn == carer.Pawn)) throw new RequiredFactException("baby play carer:" + carer.Pawn);
        var sites = context.NurserySites.Where(s => s.Room == room).OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        BabyPlayBuildingRead? Stats(PlannedBuildSite site) => mechanics.Buildings.FirstOrDefault(b => b.Def == site.Definition && b.Stuff == site.Stuff);
        var toys = sites.Where(s => mechanics.Buildings.Any(b => b.Def == s.Definition && !b.Decoration)).ToArray();
        var decorations = sites.Where(s => mechanics.Buildings.Any(b => b.Def == s.Definition && b.Decoration))
            .SelectMany(s => s.Positions.Select(p => (Position: p, Stats: Stats(s) ?? throw new RequiredFactException("nursery decoration stats:" + s.Definition + "/" + s.Stuff)))).ToArray();
        var factors = decorations.Select(d => d.Stats.GainFactor).Append(1).ToArray();
        var toyStats = toys.Select(t => Stats(t) ?? throw new RequiredFactException("nursery toy stats:" + t.Definition + "/" + t.Stuff)).ToArray();
        var source = baby.InNursery ? carer.Toy : null;
        // A projected room has no native thing-lister ordering yet. Bound every planned toy's source stat.
        var toyMin = toyStats.Length != 0 ? toyStats.Min(t => t.GainFactor) * Math.Min(factors.Min(), carer.WalkingMinFactor)
            : source is not null ? source.GainFactor * source.MinRoomFactor : (double?)null;
        var toyMax = toyStats.Length != 0 ? toyStats.Max(t => t.GainFactor) * Math.Max(factors.Max(), carer.WalkingMaxFactor)
            : source is not null ? source.GainFactor * source.MaxRoomFactor : (double?)null;
        var beds = context.PlannedBeds.GetValueOrDefault(room) ?? [];
        var bedDefs = beds.OfType<SleepingBedFacts>().Select(b => b.Def).ToHashSet(StringComparer.Ordinal);
        var positions = sites.Where(s => bedDefs.Contains(s.Definition)).SelectMany(s => s.Positions).ToArray();
        var staticMin = baby.InNursery ? play.RoomFactor : 1d;
        var staticMax = staticMin;
        if (decorations.Length != 0)
        {
            var at = index < positions.Length ? positions[index]
                : baby.InNursery ? context.Needs.Pawns.First(p => p.Pawn == baby.Pawn).Location
                : throw new RequiredFactException("nursery play position:" + baby.Pawn);
            // The first eligible decoration wins, never a product. Different stuff factors retain the
            // range because projected construction does not establish native room-lister ordering.
            var eligible = decorations.Where(d => Math.Pow(d.Position.X - at.X, 2) + Math.Pow(d.Position.Z - at.Z, 2) < 9.9 * 9.9).Select(d => d.Stats.GainFactor).ToArray();
            staticMin = eligible.Length == 0 ? 1 : eligible.Min();
            staticMax = eligible.Length == 0 ? 1 : eligible.Max();
            if (baby.InNursery)
            {
                staticMin = Math.Min(staticMin, play.RoomFactor);
                staticMax = Math.Max(staticMax, play.RoomFactor);
            }
        }

        var lowerWith = new List<double>();
        var upperWith = new List<double>();
        var lowerWithout = new List<double>();
        var upperWithout = new List<double>();
        void Add(List<double> lower, List<double> upper, double min, double max)
        {
            if (!double.IsFinite(min) || !double.IsFinite(max) || min <= 0 || max < min) throw new RequiredFactException("baby play exposure:" + baby.Pawn);
            lower.Add(mechanics.GainPerTick * min); upper.Add(mechanics.GainPerTick * max);
        }
        foreach (var activity in mechanics.Activities)
        {
            if (!Enum.IsDefined(activity.Kind)) throw new RequiredFactException("baby play activity:" + activity.Def);
            switch (activity.Kind)
            {
                case BabyPlayKind.Static:
                    Add(lowerWith, upperWith, staticMin, staticMax);
                    var oldStatic = baby.InNursery ? 1 : play.RoomFactor;
                    Add(lowerWithout, upperWithout, oldStatic, oldStatic);
                    break;
                case BabyPlayKind.Walking:
                    Add(lowerWith, upperWith, Math.Min(carer.WalkingMinFactor, factors.Min()), Math.Max(carer.WalkingMaxFactor, factors.Max()));
                    Add(lowerWithout, upperWithout, baby.InNursery ? carer.WalkingWithoutNurseryMinFactor : carer.WalkingMinFactor,
                        baby.InNursery ? carer.WalkingWithoutNurseryMaxFactor : carer.WalkingMaxFactor);
                    break;
                case BabyPlayKind.Toys:
                    if (toyMin is not null && toyMax is not null) Add(lowerWith, upperWith, toyMin.Value, toyMax.Value);
                    var oldSource = baby.InNursery ? carer.ToyWithoutNursery : carer.Toy;
                    if (oldSource is not null) Add(lowerWithout, upperWithout, oldSource.GainFactor * oldSource.MinRoomFactor, oldSource.GainFactor * oldSource.MaxRoomFactor);
                    break;
            }
        }
        if (lowerWith.Count == 0 || lowerWithout.Count == 0) throw new RequiredFactException("baby play activities:" + baby.Pawn);
        return new(CareShare(mechanics.FallPerTick, upperWith), CareShare(mechanics.FallPerTick, lowerWith),
            CareShare(mechanics.FallPerTick, upperWithout), CareShare(mechanics.FallPerTick, lowerWithout));
    }
}
