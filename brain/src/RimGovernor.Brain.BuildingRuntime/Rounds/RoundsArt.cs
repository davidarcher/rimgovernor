namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsArtPlanner : IPurposeReporter
{
    /// <summary>The native work ticks one sculpture takes at a bench.</summary>
    internal const int ArtNativeWorkTicks = 2500;

    private readonly Rounder _reviewer;
    private readonly object _mu = new();

    /// <summary>Whether a sculpture is in progress at a bench (the last declaration's reading).</summary>
    private bool _sculpting;

    public RoundsArtPlanner(Rounder reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("NewRoundsArtPlanner: reviewer == nil");
        }
        _reviewer = reviewer;
    }

    /// <summary>Declares the art orders of this review and returns the MaintainArt share.</summary>
    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        Task.FromResult(DeclareOrdersAll(snapshot, projection, benches, ct).For(PolicyApi.MaintainArt));

    internal Declared DeclareOrdersAll(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        lock (_mu)
        {
            _sculpting = PolicyApi.SculptureInProgress(benches);
        }
        var request = new ArtOrderRequest
        {
            Benches = [.. benches],
            Demand = new ArtDemand(),
            Colonists = projection.Facts.Colonists,
            Items = projection.Facts.Items,
            RoomsOwed = projection.Facts.SculptureRoomsOwed,
            Profiles = Unknown<EquatableArray<PawnProfile>>(),
        };
        if (projection.WorkPawns.TryGetValue(out var pawns))
        {
            var profiles = PolicyApi.Profiles(pawns);
            request = request with { Profiles = Known<EquatableArray<PawnProfile>>([.. profiles]) };
            request = request with { Demand = ArtDemand(projection, profiles) };
        }
        return PolicyApi.DeclareArtOrders(request);
    }

    (ConcernId Key, ConcernId Need) IPurposeReporter.ReportTarget() => ReportTarget();

    PurposeReport IPurposeReporter.Report() => Report();

    internal static (ConcernId Key, ConcernId Need) ReportTarget() => (PolicyApi.MaintainArt, PolicyApi.MaintainArt);

    internal PurposeReport Report()
    {
        lock (_mu)
        {
            if (!_sculpting)
            {
                return new PurposeReport { Order = BuildingReasonNoDeficit };
            }
            return new PurposeReport { Order = WaitFor(Cause.ExistingWork, "sculpture"), WorkTicks = ArtNativeWorkTicks };
        }
    }

    internal static ArtDemand ArtDemand(ColonyProjection facts, IReadOnlyList<PawnProfile> profiles)
    {
        facts.Resources.TryGetValue(out var known);
        IReadOnlyDictionary<Resource, long> stock = known ?? new Dictionary<Resource, long>();
        if (!facts.Rooms.TryGetValue(out var rooms) || !facts.Facts.CurrentConstruction.TryGetValue(out var census) || !facts.Facts.Sleeping.TryGetValue(out var obs) || !census.Colony || SleepingTraits(facts) is not { } traits)
        {
            return PolicyApi.NewArtDemand(Unknown<SleepingObservation>(), new Dictionary<string, RoomTarget>(), [], stock, profiles, facts.Facts.Items);
        }
        facts.TechTier.TryGetValue(out var tier);
        return PolicyApi.NewArtDemand(facts.Facts.Sleeping, PolicyApi.RoomQualityTargets(obs, traits, tier, facts.Impressiveness), PolicyApi.FurnitureRooms(rooms, census, facts.Cells), stock, profiles, facts.Facts.Items);
    }
}
