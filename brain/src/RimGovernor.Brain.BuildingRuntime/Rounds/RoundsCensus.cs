namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>One retained review reading with what it covers, so later planners of the same tick reuse it.</summary>
internal sealed class RoundsCensus
{
    public RoundsReading Reading = null!;
    public bool Rooms;
    public bool Claims;
    public HashSet<string> Definitions = new(StringComparer.Ordinal);
    public ulong Generation;
    public IReadOnlyDictionary<string, ulong>? Colony;
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
    internal Identity FoodIdentity = new();
    internal Fact<FoodPlan> FoodPlanFact;
    internal ulong FoodGeneration;
    internal double FoodMin;
    internal double FoodTarget;
    internal ulong FoodOffers;
    internal ResourceSupplyState? Supply;
    internal ResourceSupplyKey SupplyKey;
    internal Fact<LayoutPlan> Layout;
    internal Identity LayoutScope = new();
    internal Fact<RoyaltyFacts> Royalty;
    internal Identity RoyaltyScope = new();
    internal IReadOnlyList<string> Worship = [];
    internal ulong Benches;
    internal IReadOnlyDictionary<string, ulong>? ReadColony;

    internal void RememberColony(IReadOnlyDictionary<string, ulong>? versions)
    {
        lock (Mu)
        {
            ReadColony = versions;
        }
    }

    internal void RememberBenches(ulong version)
    {
        lock (Mu)
        {
            Benches = version;
        }
    }

    internal bool BenchTable(FactStore m, FactScope scope, Identity expected, out Table<string, GearBenchRead> table)
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
        if (version == 0 || census is null || census.Generation != generation || !SameObservedIdentity(census.Reading.Projection.Identity, expected))
        {
            return false;
        }
        var ok = StoreTables.TryGetTable(m, scope, ClockMirror.BenchSectionName, out table);
        return ok && table.Version == version;
    }

    internal void RememberLayout(Identity identity, Fact<LayoutPlan> layout)
    {
        lock (Mu)
        {
            Layout = layout;
            LayoutScope = identity;
        }
    }

    internal void RememberRoyalty(Identity identity, Fact<RoyaltyFacts> royalty)
    {
        lock (Mu)
        {
            Royalty = royalty;
            RoyaltyScope = identity;
        }
    }

    internal void RememberWorship(IReadOnlyList<string> names)
    {
        lock (Mu)
        {
            Worship = names;
        }
    }

    internal IReadOnlyList<string> RememberedWorship()
    {
        lock (Mu)
        {
            return Worship;
        }
    }

    internal Fact<RoyaltyFacts> Remembered()
    {
        lock (Mu)
        {
            return Royalty;
        }
    }

    /// <summary>Serves the remembered layout plan and royalty facts into a projection that did not read them, within one colony/map/load.</summary>
    internal ColonyProjection ServeLayout(ColonyProjection projection)
    {
        Fact<LayoutPlan> layout;
        Identity scope;
        Fact<RoyaltyFacts> royalty;
        Identity royaltyScope;
        lock (Mu)
        {
            layout = Layout;
            scope = LayoutScope;
            royalty = Royalty;
            royaltyScope = RoyaltyScope;
        }
        var id = projection.Identity;
        bool Same(Identity s) => s.Colony == id.Colony && s.Map == id.Map && s.Load == id.Load;
        if (!projection.LayoutPlan.IsKnown && layout.IsKnown && Same(scope))
        {
            projection = projection with { LayoutPlan = layout };
        }
        if (!projection.Royalty.IsKnown && royalty.IsKnown && Same(royaltyScope))
        {
            projection = projection with { Royalty = royalty };
        }
        return projection;
    }

    internal void Retain(RoundsReading reading, bool rooms, Fact<EquatableArray<ConstructionClaim>> claims)
    {
        var census = new RoundsCensus { Reading = reading, Rooms = rooms, Claims = claims.IsKnown };
        foreach (var definition in reading.Projection.Definitions)
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

    /// <summary>The remembered royalty facts and the work pawns of the retained reading; unknown when the reading is stale.</summary>
    internal (Fact<RoyaltyFacts> Royalty, Fact<EquatableArray<WorkPawn>> Pawns) Psycasters()
    {
        RoundsCensus? census;
        ulong generation;
        Fact<RoyaltyFacts> royalty;
        lock (Mu)
        {
            census = Latest;
            generation = Generation;
            royalty = Royalty;
        }
        if (census is null || census.Generation != generation)
        {
            return (Unknown<RoyaltyFacts>(), Unknown<EquatableArray<WorkPawn>>());
        }
        return (royalty, census.Reading.Projection.WorkPawns);
    }

    internal void Invalidate()
    {
        lock (Mu)
        {
            Generation++;
        }
    }

    internal bool Lookup(object? source, object? reviewerSource, Identity expected, bool rooms, Fact<EquatableArray<ConstructionClaim>> claims, IReadOnlyList<string>? definitions, out RoundsReading reading)
    {
        reading = default!;
        if (!BuildingRuntimePackage.SameNativeSource(source, reviewerSource))
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
        if (census is null || census.Generation != generation || !BuildingRuntimePackage.SameObservedIdentity(census.Reading.Projection.Identity, expected))
        {
            return false;
        }
        if (rooms && !census.Rooms)
        {
            return false;
        }
        if (claims.IsKnown && !census.Claims)
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

    private static bool SameObservedIdentity(Identity census, Identity expected) => BuildingRuntimePackage.SameObservedIdentity(census, expected);
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

    public async Task<(IReadOnlyList<GearBenchRead> Reads, BridgeResult Result)> ReadGearBenchesAsync(C.Identity id, CancellationToken ct)
    {
        var r = _reviewer;
        if (!_fresh && r.Census.BenchTable(r.Store!, _scope, _expected, out var cached))
        {
            return (BenchRows(cached.Rows), new BridgeResult());
        }
        var tick = _expected.Tick.Value;
        var table = await PublishBenchesAsync(r.Store!, _scope, _native, id, tick, ct).ConfigureAwait(false);
        if (_fresh)
        {
            r.Census.RememberBenches(table.Version);
        }
        return (BenchRows(table.Rows), new BridgeResult());
    }

    private static List<GearBenchRead> BenchRows(IReadOnlyDictionary<string, GearBenchRead> rows) => ClockMirror.BenchRows(rows);

    private static async Task<Table<string, GearBenchRead>> PublishBenchesAsync(FactStore m, FactScope scope, IRoundsWorkBenchSource native, C.Identity id, long tick, CancellationToken ct) =>
        await ClockMirror.PublishBenchesAsync(m, scope, native, id, tick, ct).ConfigureAwait(false);
}

internal static partial class BuildingRuntimePackage
{
    internal static bool SameObservedIdentity(Identity census, Identity expected)
    {
        var ak = census.NativeGeneration.TryGetValue(out var a);
        var bk = expected.NativeGeneration.TryGetValue(out var b);
        var lag = expected.Tick.Value - census.Tick.Value;
        return census.SameContext(expected) && lag >= 0 && ak && bk && a == b;
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
    internal async Task<RoundsReading> ObserveOwnedAsync(IRoundsSource source, Identity expected, Fact<EquatableArray<ConstructionClaim>> claims, CancellationToken ct) =>
        await ObserveOwnedAsync(source, expected, claims, [], ct).ConfigureAwait(false);

    internal async Task<RoundsReading> ObserveOwnedAsync(IRoundsSource source, Identity expected, Fact<EquatableArray<ConstructionClaim>> claims, IReadOnlyList<string> definitions, CancellationToken ct)
    {
        if (ImmediateReview())
        {
            return await ObservationApi.ObserveRoundsProtection(source, Clock, expected, MaxAge, ct).ConfigureAwait(false);
        }
        using var window = ClockPlanning.StandaloneWindow(source);
        if (Census.Lookup(source, Native, expected, false, claims, definitions, out var cached))
        {
            return cached;
        }
        var reading = await ObservationApi.ObserveRoundsOwned(source, Clock, expected, MaxAge, claims, definitions, ct).ConfigureAwait(false);
        reading = reading.WithProjection(ServeAndReview(reading.Projection));
        if (Player is not null)
        {
            var state = Player.Session.State();
            if (state.ObservationKnown && state.Snapshot.IsValid)
            {
                if (MethodEnabled(PolicyApi.MaintainRituals))
                {
                    reading = ReviewRituals(reading, state.Snapshot);
                }
                if (MethodEnabled(PolicyApi.HoldGatherings))
                {
                    reading = ReviewGathering(reading, state.Snapshot);
                }
            }
        }
        return reading;
    }

    /// <summary>The remembered layout and royalty served into a fresh projection, then the food plan, meals, reserve and baby feeding reviews (Go ran them in place on <c>&amp;reading.Projection</c>).</summary>
    private ColonyProjection ServeAndReview(ColonyProjection fresh)
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
        expected.NativeGeneration.TryGetValue(out var generation);
        var scope = new FactScope(expected.Load.Value, expected.Map.Value, generation.Value);
        return new MirroredBenches(this, native, scope, expected, fresh);
    }

    internal async Task<RoundsReading> ObserveRoomsAsync(IRoundsSource source, Identity expected, Fact<EquatableArray<ConstructionClaim>> claims, CancellationToken ct) =>
        await ObserveRoomsAsync(source, expected, claims, [], ct).ConfigureAwait(false);

    internal async Task<RoundsReading> ObserveRoomsAsync(IRoundsSource source, Identity expected, Fact<EquatableArray<ConstructionClaim>> claims, IReadOnlyList<string> definitions, CancellationToken ct)
    {
        if (ImmediateReview())
        {
            return await ObservationApi.ObserveRoundsProtection(source, Clock, expected, MaxAge, ct).ConfigureAwait(false);
        }
        using var window = ClockPlanning.StandaloneWindow(source);
        if (Census.Lookup(source, Native, expected, true, claims, definitions, out var cached))
        {
            return cached;
        }
        var reading = await ObservationApi.ObserveRoundsRooms(source, Clock, expected, MaxAge, claims, definitions, ct).ConfigureAwait(false);
        return reading.WithProjection(ServeAndReview(reading.Projection));
    }

    internal async Task<ColonyReading> ObserveColonyAsync(IColonySource source, Identity expected, IReadOnlyList<string>? definitions, CancellationToken ct)
    {
        if (ImmediateReview())
        {
            return (await ObservationApi.ObserveRoundsProtection(Native, Clock, expected, MaxAge, ct).ConfigureAwait(false)).Reading;
        }
        using var window = ClockPlanning.StandaloneWindow(source);
        if (definitions is { Count: > 0 })
        {
            if (source is not IRoundsSource routine)
            {
                throw new ObservationContractException("observation contract violated");
            }
            return (await ObserveOwnedAsync(routine, expected, Unknown<EquatableArray<ConstructionClaim>>(), [.. definitions], ct).ConfigureAwait(false)).Reading;
        }
        if (Census.Lookup(source, Native, expected, false, Unknown<EquatableArray<ConstructionClaim>>(), null, out var cached))
        {
            return cached.Reading;
        }
        var reading = await ObservationApi.ObserveColony(source, Clock, expected, MaxAge, true, ct).ConfigureAwait(false);
        return reading with { Projection = ServeAndReview(reading.Projection) };
    }
}
