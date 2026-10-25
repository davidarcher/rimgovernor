using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>One selected plan's physical presence owner for baby sound, sleep disturbance and social interaction.
/// Transit and future unallocated waking time are named unlocated spans; they never reuse a snapshot position.</summary>
internal sealed class ForecastNurseryExposure
{
    private readonly ForecastCoupledContext context;
    private readonly int nursery;
    internal sealed record Presence(double Start, double End, ServicePositionRead Position, double Share,
        bool Asleep, ForecastSocialService Social, string Activity)
    {
        internal double LowerShare { get; init; } = Share;
    }

    private readonly Dictionary<string, Presence[]> pawns;
    private readonly IReadOnlyList<ForecastBabyCare.Presence> careWith;
    private readonly IReadOnlyList<ForecastBabyCare.Presence> careWithout;
    internal ForecastNurseryGeometry Geometry { get; }
    internal double[] Cuts { get; }

    internal ForecastNurseryExposure(ForecastCoupledContext context, ForecastCoupledOutcome outcome, double horizon, ISet<string> findings)
    {
        this.context = context;
        nursery = context.PlannedRoomRoles.Where(r => r.Value == PlannedRole.Nursery)
            .Select(r => r.Key).DefaultIfEmpty(0).Min();
        Geometry = new(context.Needs.Babies.Geometry ?? throw new RequiredFactException("nursery map geometry"));
        var tasks = context.Activities.Activities.ToDictionary(t => t.Id);
        pawns = new(StringComparer.Ordinal);
        var cuts = new SortedSet<double> { 0, horizon };
        foreach (var pawn in context.Needs.Pawns)
        {
            var result = new List<Presence>();
            var allocations = outcome.Plan.Allocations.Where(a => a.Pawn == pawn.Pawn).OrderBy(a => a.Depart).ToArray();
            var ownCuts = new SortedSet<double> { 0, horizon };
            foreach (var a in allocations)
            {
                ownCuts.Add(Math.Clamp(a.Depart, 0, horizon)); ownCuts.Add(Math.Clamp(a.Arrive, 0, horizon));
                ownCuts.Add(Math.Clamp(a.Start, 0, horizon)); ownCuts.Add(Math.Clamp(a.End, 0, horizon));
                if (Need(a) is { } need) ownCuts.Add(Math.Clamp(a.Start + need.ServiceDelay, 0, horizon));
                foreach (var option in context.Activities.Options.Where(o => o.Activity == a.Activity && o.Pawn == a.Pawn))
                    ownCuts.Add(Math.Clamp(a.Start + option.ServiceDelayTicks, 0, horizon));
            }
            if (pawn.CurrentService is { } service) ownCuts.Add(Math.Clamp(service.RemainingTicks, 0, horizon));
            var observedSleepEnd = 0d;
            if (!pawn.Awake && pawn.LyingIn is { HediffBlocksSleeping: false, Deathrest: LyingInDeathrest.None } lying)
            {
                var rest = pawn.Needs.SingleOrDefault(n => n.Kind == "rest") ?? throw new RequiredFactException("nursery observed rest:" + pawn.Pawn);
                if (!(lying.Place.GainPerTick > 0)) throw new RequiredFactException("nursery observed rest gain:" + pawn.Pawn);
                observedSleepEnd = Math.Min(horizon, Math.Max(0, pawn.WakeThreshold - rest.Level) / lying.Place.GainPerTick);
                ownCuts.Add(observedSleepEnd);
            }
            var times = ownCuts.ToArray();
            for (var i = 0; i + 1 < times.Length; i++)
            {
                var from = times[i]; var end = times[i + 1];
                if (end <= from) continue;
                var allocation = allocations.SingleOrDefault(a => a.Depart <= from && from < a.End);
                if (allocation is not null)
                {
                    if (!tasks.TryGetValue(allocation.Activity, out var task)) throw new RequiredFactException("nursery activity:" + allocation.Activity);
                    var need = Need(allocation);
                    if (from < allocation.Arrive || task.Location != task.EndLocation)
                    { Add(new ServicePositionRead.Unprojected("nursery_transit_exposure:" + pawn.Pawn), 1, false, new ForecastSocialService.Unprojected(), allocation.Activity); continue; }
                    var options = context.Activities.Options.Where(o => o.Activity == allocation.Activity && o.Pawn == pawn.Pawn).ToArray();
                    if (options.Length != 1) throw new RequiredFactException("nursery service option:" + allocation.Activity + "/" + pawn.Pawn);
                    if (from < allocation.Start + options[0].ServiceDelayTicks)
                    { Add(new ServicePositionRead.Unprojected("nursery_service_preparation_exposure:" + pawn.Pawn), 1, false, new ForecastSocialService.Unprojected(), allocation.Activity); continue; }
                    if (need?.Kind == "food")
                    {
                        // Native random chair/standing selection has verified stationary destinations, but its intervening
                        // path and onset are not timed by the pickup allocation. Enclose the whole interval over all map
                        // positions rather than assert the pickup cell, a sampled chair, or a stationary meal duration.
                        Add(new ServicePositionRead.Unprojected("nursery_meal_pickup_to_chew_exposure_bounded:" + pawn.Pawn), 1, false,
                            new ForecastSocialService.Unprojected(), allocation.Activity); continue;
                    }
                    var asleep = need?.Kind == "rest" && from >= allocation.Start + need.ServiceDelay;
                    Add(options[0].ServicePosition, 1, asleep,
                        asleep ? new ForecastSocialService.Verified(ActivitySocialMode.Off) : options[0].SocialMode, allocation.Activity);
                    continue;
                }
                if (from < observedSleepEnd && pawn.LyingIn is { } observed)
                { Add(Located(observed.Place, pawn.Pawn), 1, true, new ForecastSocialService.Verified(ActivitySocialMode.Off), ""); continue; }
                if (pawn.CurrentService is { } current && from < current.RemainingTicks)
                { Add(new ServicePositionRead.Located(current.Location), 1, current.Kind == "rest", new ForecastSocialService.Unprojected(), ""); continue; }
                // Recurrent rest uses the selected terminal sleeping target, not the last sampled standing position.
                var selected = allocations.Where(a => Need(a)?.Kind == "rest").OrderByDescending(a => a.End).FirstOrDefault();
                var surface = pawn.Sleep;
                var restNeed = pawn.Needs.SingleOrDefault(n => n.Kind == "rest");
                var gain = surface?.GainPerTick;
                ServicePositionRead? sleepPosition = surface is null ? null : Located(surface, pawn.Pawn, pawn.State == "carried" && surface.Bed.Length == 0);
                if (selected is not null)
                {
                    var target = pawn.Targets.SingleOrDefault(t => t.Id == Need(selected)!.Target && t.Kind == "rest")
                        ?? throw new RequiredFactException("nursery terminal sleep target:" + pawn.Pawn);
                    gain = target.Recovery is NeedRecovery.Rate rate ? rate.GainPerTick : throw new RequiredFactException("nursery sleep recovery:" + target.Id);
                    sleepPosition = target.ServicePosition;
                }
                var sleepShare = 0d;
                if (restNeed is { FallPerTick: > 0 } && gain is null || surface is not null && restNeed is null)
                    throw new RequiredFactException("nursery recurrent sleeping surface/rate:" + pawn.Pawn);
                if (gain is { } g && restNeed is { } r)
                {
                    if (!double.IsFinite(g) || g <= 0 || !double.IsFinite(r.FallPerTick) || r.FallPerTick < 0)
                        throw new RequiredFactException("nursery recurrent rest rates:" + pawn.Pawn);
                    sleepShare = r.FallPerTick / (g + r.FallPerTick);
                    if (sleepShare > 0) Add(sleepPosition!, sleepShare, true, new ForecastSocialService.Verified(ActivitySocialMode.Off), "");
                }
                if (sleepShare < 1) Add(new ServicePositionRead.Unprojected("nursery_unallocated_awake_exposure:" + pawn.Pawn),
                    1 - sleepShare, false, new ForecastSocialService.Unprojected(), "");

                void Add(ServicePositionRead position, double share, bool asleep, ForecastSocialService social, string activity)
                {
                    if (position is ServicePositionRead.Unprojected unknown) findings.Add(unknown.Reason);
                    result.Add(new(from, end, position, share, asleep, social, activity));
                }
            }
            pawns.Add(pawn.Pawn, result.ToArray());
            cuts.UnionWith(ownCuts);
        }
        careWith = ForecastBabyCare.Of(context, true, horizon, findings);
        careWithout = ForecastBabyCare.Of(context, false, horizon, findings);
        foreach (var baby in context.Needs.Babies.Babies)
            foreach (var side in new[] { true, false })
                if (BabySource(baby, side) is ServicePositionRead.Unprojected unprojected) findings.Add(unprojected.Reason);
        foreach (var care in careWith.Concat(careWithout)) { cuts.Add(care.Start); cuts.Add(care.End); }
        if (careWith.Concat(careWithout).Any(c => c.LowerShare != c.UpperShare))
            findings.Add("nursery_care_time_budget_not_uniform_occupancy");
        Cuts = cuts.ToArray();

        ForecastNeedTask? Need(ForecastActivityAllocation a) => context.NeedTasks.SingleOrDefault(n => n.Task == a.Activity && n.Pawn == a.Pawn);
    }

    internal IReadOnlyList<Presence> At(string pawn, double at, bool withNursery = true, bool includeCare = true)
    {
        if (!pawns.TryGetValue(pawn, out var rows)) throw new RequiredFactException("nursery projected pawn:" + pawn);
        var care = includeCare ? Care(withNursery).Where(c => c.Carer == pawn && c.Start <= at && at < c.End && c.UpperShare > 0).ToArray() : [];
        return Occupancy(rows.Where(s => s.Start <= at && at < s.End), care,
            c => c.CarerPosition);
    }

    internal IReadOnlyList<Presence> BabyAt(BabyRead baby, double at, bool withNursery, bool includeCare = true)
    {
        var care = includeCare ? Care(withNursery).Where(c => c.Baby == baby.Pawn && c.Start <= at && at < c.End && c.UpperShare > 0).ToArray() : [];
        return Occupancy([new(at, Cuts[^1], BabySource(baby, withNursery), 1, false,
            new ForecastSocialService.Unprojected(), "")], care, c => c.BabyPosition);
    }

    private IReadOnlyList<ForecastBabyCare.Presence> Care(bool withNursery) => withNursery ? careWith : careWithout;

    internal double CareBudgetTicks(string baby, string hearer, bool withNursery) => Math.Min(Cuts[^1],
        Care(withNursery).Where(c => c.Baby == baby || c.Carer == hearer).GroupBy(c => c.Demand)
            .Sum(g => g.Max(c => (c.End - c.Start) * c.UpperShare)));

    private static IReadOnlyList<Presence> Occupancy(IEnumerable<Presence> baseline,
        ForecastBabyCare.Presence[] care, Func<ForecastBabyCare.Presence, ServicePositionRead> position)
    {
        if (care.Length == 0) return baseline.ToArray();
        var guaranteed = care.Max(c => c.LowerShare);
        // An observed active job owns this instant. Future opportunities cannot also displace its known positions.
        if (guaranteed == 1) care = care.Where(c => c.LowerShare == 1).ToArray();
        // Future service has a finite horizon budget, but may occupy any individual tick. A fraction of the
        // budget is not a probability of occupancy at this tick; alternatives are deliberately not averaged.
        var result = baseline.Select(p => p with { Share = p.Share * (1 - guaranteed), LowerShare = 0 }).ToList();
        result.AddRange(care.Select(c => new Presence(c.Start, c.End, position(c), 1, false, c.Social, c.Demand)
        { LowerShare = c.LowerShare }));
        return result;
    }

    internal static ServicePositionRead Located(NeedSleep surface, string pawn, bool randomDrop = false) => surface.Position is ServicePositionRead.Located
        ? surface.Position : randomDrop ? surface.Position
            : throw new RequiredFactException("nursery sleeping slot:" + pawn + "/" + surface.Bed);

    internal readonly record struct Range(double Lower, double Upper)
    {
        internal static Range Unknown => new(0, 1);
        internal static Range Zero => new(0, 0);
    }

    internal Range Hearing(ServicePositionRead source, ServicePositionRead listener, double hearing, int openedNursery = -1)
        => hearing == 0 ? Range.Zero : Between(source, listener, (a, b) => Geometry.Hears(a, b, hearing, openedNursery));

    internal ServicePositionRead BabySource(BabyRead baby, bool withNursery) => RestSource(context, baby, withNursery);

    internal static ServicePositionRead RestSource(ForecastCoupledContext context, BabyRead baby, bool withNursery)
    {
        var pawn = context.Needs.Pawns.SingleOrDefault(p => p.Pawn == baby.Pawn)
            ?? throw new RequiredFactException("nursery baby pawn:" + baby.Pawn);
        if (!withNursery) return Located(baby.SleepWithoutNursery, baby.Pawn, pawn.State == "carried" && baby.SleepWithoutNursery.Bed.Length == 0);
        if (baby.InNursery) return Located(pawn.Sleep ?? throw new RequiredFactException("nursery baby surface:" + baby.Pawn), baby.Pawn);
        var nursery = context.PlannedRoomRoles.Where(r => r.Value == PlannedRole.Nursery).Select(r => r.Key).DefaultIfEmpty(0).Min();
        var index = context.Needs.Babies.Babies.ToList().FindIndex(b => b.Pawn == baby.Pawn);
        var beds = context.PlannedBeds.GetValueOrDefault(nursery) ?? throw new RequiredFactException("nursery planned beds");
        var definitions = beds.OfType<SleepingBedFacts>().Select(b => b.Def).ToHashSet(StringComparer.Ordinal);
        var positions = context.NurserySites.Where(s => s.Room == nursery && definitions.Contains(s.Definition)).OrderBy(s => s.Id, StringComparer.Ordinal)
            .SelectMany(s => s.Positions.Select(at => (Site: s, At: at))).ToArray();
        if (index < 0 || index >= positions.Length || index >= beds.Count || beds[index] is not { } bed)
            throw new RequiredFactException("nursery planned sleeping slot:" + baby.Pawn);
        var slots = context.Needs.Babies.Geometry?.BedSlots.SingleOrDefault(s => s.Def == bed.Def)
            ?? throw new RequiredFactException("nursery bed native slot offsets:" + bed.Def);
        if (slots.Offsets.Count != 4) throw new RequiredFactException("nursery bed native slot rotations:" + bed.Def);
        var offset = slots.Offsets[(int)positions[index].Site.Rotation];
        return new ServicePositionRead.Located(new(positions[index].At.X + offset.X, positions[index].At.Z + offset.Z));
    }

    internal Range HeardAt(BabyRead baby, string hearer, double hearing, double at, bool withNursery, bool asleepOnly = false, bool includeCare = true)
    {
        var lower = 0d; var upper = 0d;
        foreach (var source in BabyAt(baby, at, withNursery, includeCare))
        foreach (var presence in At(hearer, at, withNursery, includeCare).Where(p => !asleepOnly || p.Asleep))
        {
            var range = Sound(baby, source.Position, presence.Position, hearing, withNursery);
            lower += Math.Max(0, source.LowerShare + presence.LowerShare - 1) * range.Lower;
            upper += Math.Min(source.Share, presence.Share) * range.Upper;
        }
        return new(Math.Min(1, lower), Math.Min(1, upper));
    }

    internal Range HeardDuringFit(BabyRead baby, string hearer, double hearing, double at, double duration, bool withNursery)
    {
        var interval = context.Needs.Babies.ClamorIntervalTicks;
        if (!(duration > 0) || !(interval > 0)) throw new RequiredFactException("nursery fit sound timing");
        var end = Math.Min(Cuts[^1], at + duration);
        var cuts = Cuts.Where(t => t > at && t < end).Prepend(at).Append(end).ToArray();
        // A new fit's lastScreamTick starts at -1, so the first mental-state tick emits immediately.
        var lower = HeardAt(baby, hearer, hearing, at, withNursery).Lower; var upper = 0d;
        for (var i = 0; i + 1 < cuts.Length; i++)
        {
            var span = HeardAt(baby, hearer, hearing, cuts[i], withNursery);
            // The native clamor phase is not sampled from RNG. A complete check interval at a verified location
            // guarantees an opportunity; a shorter visit may miss every clamor. Max/union bounds retain unknown
            // correlation of recurrent sleep/care shares without treating every clamor as another fit memory.
            var ticks = cuts[i + 1] - cuts[i];
            if (ticks >= interval) lower = Math.Max(lower, span.Lower);
            upper = Math.Min(1, upper + span.Upper * Math.Ceiling(ticks / interval + 1));
        }
        return new(lower, upper);
    }

    private Range Sound(BabyRead baby, ServicePositionRead source, ServicePositionRead listener, double hearing, bool withNursery)
    {
        if (hearing == 0) return Range.Zero;
        if (!withNursery || baby.InNursery) return Hearing(source, listener, hearing, withNursery ? -1 : baby.NurseryRoom);
        // A future closed nursery has no observed region graph. Native predicts its connected interior, so its
        // closed doors exclude outside listeners. Within it the region cap can only reduce the radius enclosure.
        if (source is ServicePositionRead.Unprojected or ServicePositionRead.PassableMap || listener is ServicePositionRead.Unprojected or ServicePositionRead.PassableMap) return Range.Unknown;
        var closures = context.Closures.Where(c => c.Room == nursery).ToArray();
        if (closures.Length == 0) throw new RequiredFactException("nursery predicted acoustic interior:" + nursery);
        var sources = Cells(source); var listeners = Cells(listener);
        if (sources.Count > 1 && listeners.Count > 1) return Range.Unknown;
        var all = true; var any = false;
        foreach (var a in sources) foreach (var b in listeners)
        {
            var radius = (float)(context.Needs.Babies.Geometry?.ScreamRadius ?? throw new RequiredFactException("nursery sound radius")) * (float)hearing;
            var inRange = (float)(a.X - b.X) * (a.X - b.X) + (float)(a.Z - b.Z) * (a.Z - b.Z) <= radius * radius;
            var sourceInside = closures.Any(c => c.Interior.Contains(a));
            var listenerInside = closures.Any(c => c.Interior.Contains(b));
            // Exterior care does not magically become silent. Outside the new room the observed region partition
            // may also split at its shell, so retain the radius upper bound rather than reusing that partition.
            var possible = inRange && (!sourceInside || listenerInside);
            any |= possible;
            // The source region itself is always processed; no assertion about newly split future regions is made.
            all &= possible && a == b;
        }
        return new(all ? 1 : 0, any ? 1 : 0);
    }

    internal Range Social(ServicePositionRead actor, ServicePositionRead recipient, bool withNursery = false)
    {
        if (!withNursery || context.Needs.Babies.Babies.All(b => b.InNursery))
        {
            var opened = withNursery ? -1 : context.Needs.Babies.Babies.Select(b => b.NurseryRoom).FirstOrDefault(r => r >= 0, -1);
            return Between(actor, recipient, (a, b) => Geometry.Social(a, b, opened));
        }
        if (actor is ServicePositionRead.Unprojected or ServicePositionRead.PassableMap || recipient is ServicePositionRead.Unprojected or ServicePositionRead.PassableMap) return Range.Unknown;
        // A future shell has no observed visibility grid. Retain only the verified native distance exclusion;
        // treating today's unobstructed ray as tomorrow's would freeze future wall/door geometry.
        return BetweenBounds(actor, recipient, (a, b) =>
        {
            var radius = context.Needs.Babies.Geometry!.SocialRadius;
            if ((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Z - b.Z) * (a.Z - b.Z) > radius * radius) return Range.Zero;
            return Range.Unknown;
        });
    }

    private static Range Between(ServicePositionRead first, ServicePositionRead second, Func<Cell, Cell, bool> predicate)
    {
        if (first is ServicePositionRead.Unprojected or ServicePositionRead.PassableMap || second is ServicePositionRead.Unprojected or ServicePositionRead.PassableMap) return Range.Unknown;
        return BetweenBounds(first, second, (a, b) => predicate(a, b) ? new(1, 1) : Range.Zero);
    }

    private static Range BetweenBounds(ServicePositionRead first, ServicePositionRead second, Func<Cell, Cell, Range> predicate)
    {
        if (first is ServicePositionRead.Unprojected or ServicePositionRead.PassableMap || second is ServicePositionRead.Unprojected or ServicePositionRead.PassableMap) return Range.Unknown;
        var a = Cells(first); var b = Cells(second);
        // Two independently enclosed moving populations have no joint location law. Keep its full enclosure;
        // do not construct a map-squared source/recipient matrix to invent one.
        if (a.Count > 1 && b.Count > 1) return Range.Unknown;
        var lower = 1d; var upper = 0d;
        foreach (var from in a) foreach (var to in b)
        {
            var match = predicate(from, to); lower = Math.Min(lower, match.Lower); upper = Math.Max(upper, match.Upper);
            if (lower == 0 && upper == 1) return Range.Unknown;
        }
        return new(lower, upper);
    }

    internal static IReadOnlyList<Cell> Cells(ServicePositionRead position) => position switch
    {
        ServicePositionRead.Located located => [located.Cell],
        ServicePositionRead.Possible possible when possible.Cells.Count > 0 => possible.Cells,
        ServicePositionRead.Possible => throw new RequiredFactException("empty nursery service position alternatives"),
        ServicePositionRead.Unprojected or ServicePositionRead.PassableMap => throw new RequiredFactException("unlocated nursery service position alternatives"),
        var invalid => throw new ArgumentOutOfRangeException(nameof(position), invalid, "Invalid nursery service position."),
    };
}
