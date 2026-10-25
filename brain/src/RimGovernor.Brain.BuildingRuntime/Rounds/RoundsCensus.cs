namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>One retained review reading with what it covers, so later planners of the same tick reuse it.</summary>
internal sealed partial class RoundsCensus
{
    public RoundsReading Reading = null!;
    public HashSet<string> Definitions = new(StringComparer.Ordinal);
    public ulong Generation;
    /// <summary>The review frame's colony facts, by reference: planner reads inside the reuse window are served it.</summary>
    public PbObservations.ColonyFactsSnapshot? Colony;
}

/// <summary>
/// The review census the planners of one tick share: the latest reading, the remembered food plan, layout, royalty and
/// worship facts and the bench table version. Guarded by one lock.
/// </summary>
internal sealed class RoundsCensusStore
{
    // Fields are internal: the food, fishing and trade-mission planning of the Rounder reads and writes them under Mu.
    internal readonly object Mu = new();
    internal RoundsCensus? Latest;
    internal ulong Generation;
    internal Identity? FoodIdentity;
    internal FoodPlan? FoodPlanHeld;
    internal ulong FoodGeneration;
    internal double FoodMin;
    internal double FoodTarget;
    internal ulong FoodOffers;

    /// <summary>The last hunt comparison, described for the review telemetry.</summary>
    internal string HuntNote = "";
    internal ResourceSupplyState? Supply;
    internal ResourceSupplyKey SupplyKey;
    internal LayoutPlan? Layout;
    internal Identity? LayoutScope;

    /// <summary>The latest review's royalty read with Royalty active; null with Royalty inactive or before the first review.</summary>
    internal RoyaltyFacts? Royalty;
    internal IReadOnlyList<string> Worship = [];
    internal ulong Benches;
    internal PbObservations.ColonyFactsSnapshot? ReadColony;

    internal void RememberColony(PbObservations.ColonyFactsSnapshot colony)
    {
        lock (Mu)
        {
            ReadColony = colony;
        }
    }

    internal void RememberBenches(ulong version)
    {
        lock (Mu)
        {
            Benches = version;
        }
    }

    internal bool BenchTable(FactStore m, FactScope scope, Identity expected, out KeyedTable<string, GearBenchRead> table)
    {
        RoundsCensus? census;
        ulong generation;
        ulong version;
        lock (Mu)
        {
            census = Latest;
            generation = Generation;
            version = Benches;
        }
        table = default!;
        if (version == 0 || census is null || census.Generation != generation || !SameObservedIdentity(census.Reading.Reading.Identity, expected))
        {
            return false;
        }
        var ok = StoreTables.TryGetTable(m, scope, ClockMirror.BenchSectionName, out table);
        return ok && table.Version == version;
    }

    internal void RememberLayout(Identity identity, LayoutPlan? layout)
    {
        lock (Mu)
        {
            Layout = layout;
            LayoutScope = identity;
        }
    }

    internal void RememberRoyalty(RoyaltyState royalty)
    {
        lock (Mu)
        {
            Royalty = royalty is RoyaltyState.Active { Facts: var facts } ? facts : null;
        }
    }

    internal void RememberWorship(IReadOnlyList<string> names)
    {
        lock (Mu)
        {
            Worship = names;
        }
    }

    /// <summary>The work chairs the remembered layout plan chose (WorkChairRules.ChairDefinitions); none before a plan is remembered.</summary>
    internal IReadOnlyList<string> RememberedWorkChairs()
    {
        lock (Mu)
        {
            return Layout is { } plan ? WorkChairRules.ChairDefinitions(plan) : [];
        }
    }

    internal IReadOnlyList<string> RememberedWorship()
    {
        lock (Mu)
        {
            return Worship;
        }
    }

    internal RoyaltyFacts? Remembered()
    {
        lock (Mu)
        {
            return Royalty;
        }
    }

    /// <summary>Serves the remembered layout plan into a projection that did not read it, within one colony/map/load.</summary>
    internal ColonyReading ServeLayout(ColonyReading projection)
    {
        LayoutPlan? layout;
        Identity? scope;
        lock (Mu)
        {
            layout = Layout;
            scope = LayoutScope;
        }
        var id = projection.Identity;
        bool Same(Identity? s) => s is not null && s.Colony == id.Colony && s.Map == id.Map && s.Load == id.Load;
        if (projection.Facts.LayoutPlan is null && layout is not null && Same(scope))
        {
            projection = projection with { Facts = projection.Facts with { LayoutPlan = layout } };
        }

        return projection;
    }

    internal void Retain(RoundsReading reading)
    {
        var census = new RoundsCensus { Reading = reading };
        foreach (var definition in reading.Reading.Facts.Definitions)
        {
            census.Definitions.Add(definition.Name);
        }
        lock (Mu)
        {
            census.Generation = Generation;
            census.Colony = ReadColony;
            ReadColony = null;
            Latest = census;
            Benches = 0;
        }
    }

    /// <summary>
    /// The retained review's colony facts, the same instance its frame published, while that review serves
    /// <paramref name="expected"/> under the reuse rule (same world and native generation, not behind the review, no
    /// invalidation since); null otherwise, and the read goes to native.
    /// </summary>
    internal PbObservations.ColonyFactsSnapshot? ServedColony(Identity expected)
    {
        RoundsCensus? census;
        ulong generation;
        lock (Mu)
        {
            census = Latest;
            generation = Generation;
        }
        if (census is null || census.Generation != generation || !RoundsCensus.SameObservedIdentity(census.Reading.Reading.Identity, expected))
        {
            return null;
        }
        return census.Colony;
    }

    internal void Invalidate()
    {
        lock (Mu)
        {
            Generation++;
        }
    }

    internal bool Lookup(object? source, object? reviewerSource, Identity expected, IReadOnlyList<string>? definitions, out RoundsReading reading)
    {
        reading = default!;
        if (!RoundsCensus.SameNativeSource(source, reviewerSource))
        {
            return false;
        }
        RoundsCensus? census;
        ulong generation;
        lock (Mu)
        {
            census = Latest;
            generation = Generation;
        }
        if (census is null || census.Generation != generation || !RoundsCensus.SameObservedIdentity(census.Reading.Reading.Identity, expected))
        {
            return false;
        }
        if (definitions is not null)
        {
            foreach (var definition in definitions)
            {
                if (!census.Definitions.Contains(definition))
                {
                    return false;
                }
            }
        }
        reading = census.Reading;
        return true;
    }

    private static bool SameObservedIdentity(Identity census, Identity expected) => RoundsCensus.SameObservedIdentity(census, expected);
}

/// <summary>The bench census read answered from the fact mirror when it holds the current table, else published fresh.</summary>
internal sealed class MirroredBenches : IRoundsWorkBenchSource
{
    private readonly Rounder _reviewer;
    private readonly IRoundsWorkBenchSource _native;
    private readonly FactScope _scope;
    private readonly Identity _expected;
    private readonly bool _fresh;

    public MirroredBenches(Rounder reviewer, IRoundsWorkBenchSource native, FactScope scope, Identity expected, bool fresh)
    {
        _reviewer = reviewer;
        _native = native;
        _scope = scope;
        _expected = expected;
        _fresh = fresh;
    }

    public async Task<IReadOnlyList<GearBenchRead>> ReadGearBenchesAsync(C.Identity id, CancellationToken ct)
    {
        var r = _reviewer;
        if (!_fresh && r.Census.BenchTable(r.Store!, _scope, _expected, out var cached))
        {
            return BenchRows(cached.Rows);
        }
        var tick = _expected.Tick.Value;
        var table = await PublishBenchesAsync(r.Store!, _scope, _native, id, tick, ct).ConfigureAwait(false);
        if (_fresh)
        {
            r.Census.RememberBenches(table.Version);
        }
        return BenchRows(table.Rows);
    }

    private static List<GearBenchRead> BenchRows(IReadOnlyDictionary<string, GearBenchRead> rows) => ClockMirror.BenchRows(rows);

    private static async Task<KeyedTable<string, GearBenchRead>> PublishBenchesAsync(FactStore m, FactScope scope, IRoundsWorkBenchSource native, C.Identity id, long tick, CancellationToken ct) =>
        await ClockMirror.PublishBenchesAsync(m, scope, native, id, tick, ct).ConfigureAwait(false);
}

partial class RoundsCensus
{
    internal static bool SameObservedIdentity(Identity census, Identity expected)
    {
        var lag = expected.Tick.Value - census.Tick.Value;
        return census.SameContext(expected) && lag >= 0 && census.NativeGeneration is C.ObservedGeneration.AtCase a && expected.NativeGeneration is C.ObservedGeneration.AtCase b && a == b;
    }

    /// <summary>Whether two native sources are the same comparable object: reference identity for classes, equality for values.</summary>
    internal static bool SameNativeSource(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return false;
        }
        var ta = a.GetType();
        if (ta != b.GetType())
        {
            return false;
        }
        return ta.IsValueType ? a.Equals(b) : ReferenceEquals(a, b);
    }
}

public sealed partial class Rounder
{
    private EquatableArray<ConstructionClaim> ReadConstructionClaims(Identity expected, CancellationToken ct) =>
        Player.Journal.ConstructionClaims(Player.Session.State().Snapshot, expected.Tick, ct);

    internal Task<RoundsReading> ObserveOwnedAsync(IRoundsSource source, Identity expected, CancellationToken ct) =>
        ObserveOwnedAsync(source, expected, ReadConstructionClaims(expected, ct), [], ct);

    internal Task<RoundsReading> ObserveOwnedAsync(IRoundsSource source, Identity expected, IReadOnlyList<string> definitions, CancellationToken ct) =>
        ObserveOwnedAsync(source, expected, ReadConstructionClaims(expected, ct), definitions, ct);

    internal async Task<RoundsReading> ObserveOwnedAsync(IRoundsSource source, Identity expected, EquatableArray<ConstructionClaim> claims, CancellationToken ct) =>
        await ObserveOwnedAsync(source, expected, claims, [], ct).ConfigureAwait(false);

    internal async Task<RoundsReading> ObserveOwnedAsync(IRoundsSource source, Identity expected, EquatableArray<ConstructionClaim> claims, IReadOnlyList<string> definitions, CancellationToken ct)
    {
        if (ClockScheduler.ImmediateReview())
        {
            return await RoundsReads.ObserveRoundsProtection(source, Clock, expected, MaxAge, claims, ct).ConfigureAwait(false);
        }
        using var window = ClockPlanning.StandaloneWindow(source);
        if (Census.Lookup(source, Native, expected, definitions, out var cached))
        {
            return cached;
        }
        var reading = await ObserveRoundsAsync(source, expected, claims, definitions, ct).ConfigureAwait(false);
        return reading.WithReading(ServeAndReview(reading.Reading));
    }

    /// <summary>
    /// The rounds census the review and a planner's own read take, with each pawn's outfit filter as the latest review's
    /// comparator chose it (#3285, #3329): its tainted filter and insulation floor. This is the one place GearPawn.AllowTainted,
    /// ColdFloor and HeatFloor are stamped (#3325), so the review's facts, the ledger's gear declarers, the census the
    /// planners share and a planner's fresh read all plan inside the same filter and floor.
    /// </summary>
    internal async Task<RoundsReading> ObserveRoundsAsync(IRoundsSource source, Identity expected, EquatableArray<ConstructionClaim> claims, IReadOnlyList<string> definitions, CancellationToken ct)
    {
        var reading = await RoundsReads.ObserveRoundsOwned(source, Clock, expected, MaxAge, claims, definitions, ct).ConfigureAwait(false);
        var snapshot = Player.Session.State().Snapshot;
        var choices = GearChoices.Get(snapshot);
        var preferences = PreferencesOf(snapshot, ct);
        // The player's colonist and herd goals ride every read of the world the latest review derived them for (#3494).
        var goals = GoalDemands.Get(snapshot);
        return reading.WithFacts(f => WithGearChoices(GoalRules.Stamp(f, goals), choices).WithPreferences(preferences));
    }

    /// <summary>The player's preferences from the goals record of the colony (the default without a record): the one load every owning policy's facts and the combat inputs read.</summary>
    internal PreferencesRecord PreferencesOf(GenerationSnapshot snapshot, CancellationToken ct) => Player.Journal.LoadGoals(Player.PlayerWorld(snapshot), ct).Preferences;

    /// <summary>The facts with each census pawn's filter and floor set from <paramref name="choices"/> (the default without one).</summary>
    internal static RoundsFacts WithGearChoices(RoundsFacts f, EquatableArray<GearChoice> choices)
    {
        var gear = f.Gear;
        GearPawn Stamp(GearPawn p) => choices.FirstOrDefault(c => c.Pawn == p.Pawn.Value) is { } c
            ? p with { AllowTainted = c.AllowTainted, ColdFloor = c.ColdFloor, HeatFloor = c.HeatFloor }
            : p with { AllowTainted = false, ColdFloor = new InsulationFloor.Worn(), HeatFloor = new InsulationFloor.Worn() };
        return f with { Gear = gear with { Pawns = [.. gear.Pawns.Select(Stamp)] } };
    }

    /// <summary>The remembered layout served into a fresh projection, then the food plan, meals, reserve and baby feeding reviews.</summary>
    private ColonyReading ServeAndReview(ColonyReading fresh)
    {
        var projection = Census.ServeLayout(fresh);
        PlanFood(ref projection);
        projection = ReviewMeals(projection);
        ReviewReserve(ref projection);
        return ReviewBabyFeeding(projection);
    }

    internal IRoundsWorkBenchSource BenchSource(IRoundsWorkBenchSource native, Identity expected, bool fresh)
    {
        if (Store is null)
        {
            return native;
        }
        var generation = expected.NativeGeneration as C.ObservedGeneration.AtCase ?? throw new ObservationChangedException();
        var scope = new FactScope(expected.Load.Value, expected.Map.Value, generation.Value);
        return new MirroredBenches(this, native, scope, expected, fresh);
    }

    internal async Task<ColonyReading> ObserveColonyAsync(IColonySource source, Identity expected, IReadOnlyList<string>? definitions, CancellationToken ct)
    {
        var claims = ReadConstructionClaims(expected, ct);
        if (ClockScheduler.ImmediateReview())
        {
            return (await RoundsReads.ObserveRoundsProtection(Native, Clock, expected, MaxAge, claims, ct).ConfigureAwait(false)).Reading;
        }
        using var window = ClockPlanning.StandaloneWindow(source);
        if (definitions is { Count: > 0 })
        {
            if (source is not IRoundsSource routine)
            {
                throw new ObservationContractException("observation contract violated");
            }
            return (await ObserveOwnedAsync(routine, expected, claims, [.. definitions], ct).ConfigureAwait(false)).Reading;
        }
        if (Census.Lookup(source, Native, expected, null, out var cached))
        {
            return cached.Reading;
        }
        var reading = await ColonyReads.ObserveColony(source, Clock, expected, MaxAge, true, ct).ConfigureAwait(false);
        return ServeAndReview(reading with { Facts = reading.Facts with { ConstructionClaims = claims } });
    }
}
