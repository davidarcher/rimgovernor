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
    internal Identity FoodIdentity;
    internal Fact<FoodPlan> FoodPlanFact;
    internal ulong FoodGeneration;
    internal double FoodMin;
    internal double FoodTarget;
    internal ulong FoodOffers;
    internal ResourceSupply? Supply;
    internal ResourceSupplyKey SupplyKey;
    internal Fact<LayoutPlan> Layout;
    internal Identity LayoutScope;
    internal Fact<RoyaltyFacts> Royalty;
    internal Identity RoyaltyScope;
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
        var ok = FactsApi.TryGetTable(m, scope, ClockMirror.BenchSectionName, out table);
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
    internal void ServeLayout(ColonyProjection projection)
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
            projection.LayoutPlan = layout;
        }
        if (!projection.Royalty.IsKnown && royalty.IsKnown && Same(royaltyScope))
        {
            projection.Royalty = royalty;
        }
    }

    internal void Retain(RoundsReading reading, bool rooms, Fact<IReadOnlyList<ConstructionClaim>> claims)
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
    internal (Fact<RoyaltyFacts> Royalty, Fact<IReadOnlyList<WorkPawn>> Pawns) Psycasters()
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
            return (Unknown<RoyaltyFacts>(), Unknown<IReadOnlyList<WorkPawn>>());
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

    internal bool Lookup(object? source, object? reviewerSource, Identity expected, bool rooms, Fact<IReadOnlyList<ConstructionClaim>> claims, IReadOnlyList<string>? definitions, out RoundsReading reading)
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

    public (IReadOnlyList<GearBenchRead> Reads, BridgeResult Result) ReadGearBenches(C.Identity id, CancellationToken ct)
    {
        var r = _reviewer;
        if (!_fresh && r.Census.BenchTable(r.Store!, _scope, _expected, out var cached))
        {
            return (BenchRows(cached.Rows), default!);
        }
        var tick = (long)_expected.Tick;
        var table = PublishBenches(ct, r.Store!, _scope, _native, id, tick);
        if (_fresh)
        {
            r.Census.RememberBenches(table.Version);
        }
        return (BenchRows(table.Rows), default!);
    }

    private static IReadOnlyList<GearBenchRead> BenchRows(IReadOnlyDictionary<string, GearBenchRead> rows) => ClockMirror.BenchRows(rows);

    private static Table<string, GearBenchRead> PublishBenches(CancellationToken ct, FactStore m, FactScope scope, IRoundsWorkBenchSource native, C.Identity id, long tick) =>
        ClockMirror.PublishBenches(ct, m, scope, native, id, tick);
}

internal static partial class BuildingRuntimePackage
{
    internal static bool SameObservedIdentity(Identity census, Identity expected)
    {
        var ak = census.NativeGeneration.TryGetValue(out var a);
        var bk = expected.NativeGeneration.TryGetValue(out var b);
        var lag = (long)expected.Tick - (long)census.Tick;
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
    internal RoundsReading ObserveOwned(CancellationToken ct, IRoundsSource source, Identity expected, Fact<IReadOnlyList<ConstructionClaim>> claims, params string[] definitions)
    {
        if (ImmediateReview(ct))
        {
            return ObservationApi.ObserveRoundsProtection(ct, source, Clock, expected, MaxAge);
        }
        ct = StandaloneWindow(ct, source);
        if (Census.Lookup(source, Native, expected, false, claims, definitions, out var cached))
        {
            return cached;
        }
        var reading = ObservationApi.ObserveRoundsOwned(ct, source, Clock, expected, MaxAge, claims, definitions);
        Census.ServeLayout(reading.Projection);
        PlanFood(reading.Projection);
        ReviewMeals(reading.Projection);
        ReviewReserve(reading.Projection);
        ReviewBabyFeeding(reading.Projection);
        if (Player is not null)
        {
            var state = Player.Session.State();
            if (state.ObservationKnown && state.Snapshot.IsValid)
            {
                if (MethodEnabled(PolicyApi.MaintainRituals))
                {
                    ReviewRituals(reading, state.Snapshot);
                }
                if (MethodEnabled(PolicyApi.HoldGatherings))
                {
                    ReviewGathering(reading, state.Snapshot);
                }
            }
        }
        return reading;
    }

    internal IRoundsWorkBenchSource? BenchSource(IRoundsWorkBenchSource? native, Identity expected, bool fresh)
    {
        if (native is null || Store is null)
        {
            return native;
        }
        expected.NativeGeneration.TryGetValue(out var generation);
        var scope = new FactScope { Load = expected.Load.Value, Map = (int)expected.Map, Generation = (ulong)generation };
        return new MirroredBenches(this, native, scope, expected, fresh);
    }

    internal RoundsReading ObserveRooms(CancellationToken ct, IRoundsSource source, Identity expected, Fact<IReadOnlyList<ConstructionClaim>> claims, params string[] definitions)
    {
        if (ImmediateReview(ct))
        {
            return ObservationApi.ObserveRoundsProtection(ct, source, Clock, expected, MaxAge);
        }
        ct = StandaloneWindow(ct, source);
        if (Census.Lookup(source, Native, expected, true, claims, definitions, out var cached))
        {
            return cached;
        }
        var reading = ObservationApi.ObserveRoundsRooms(ct, source, Clock, expected, MaxAge, claims, definitions);
        Census.ServeLayout(reading.Projection);
        PlanFood(reading.Projection);
        ReviewMeals(reading.Projection);
        ReviewReserve(reading.Projection);
        ReviewBabyFeeding(reading.Projection);
        return reading;
    }

    internal ColonyReading ObserveColony(CancellationToken ct, IColonySource source, Identity expected, IReadOnlyList<string>? definitions)
    {
        if (ImmediateReview(ct))
        {
            return ObservationApi.ObserveRoundsProtection(ct, Native, Clock, expected, MaxAge).ColonyReading;
        }
        ct = StandaloneWindow(ct, source);
        if (definitions is { Count: > 0 })
        {
            if (source is not IRoundsSource routine)
            {
                throw new ObservationContractException("observation contract violated");
            }
            return ObserveOwned(ct, routine, expected, Unknown<IReadOnlyList<ConstructionClaim>>(), [.. definitions]).ColonyReading;
        }
        if (Census.Lookup(source, Native, expected, false, Unknown<IReadOnlyList<ConstructionClaim>>(), null, out var cached))
        {
            return cached.ColonyReading;
        }
        var reading = ObservationApi.ObserveColony(ct, source, Clock, expected, MaxAge, true);
        Census.ServeLayout(reading.Projection);
        PlanFood(reading.Projection);
        ReviewMeals(reading.Projection);
        ReviewReserve(reading.Projection);
        ReviewBabyFeeding(reading.Projection);
        return reading;
    }
}
