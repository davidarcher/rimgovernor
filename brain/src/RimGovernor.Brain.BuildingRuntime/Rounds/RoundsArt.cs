namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsArtPlanner
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
    public Declared DeclareOrders(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        return DeclareOrdersAll(snapshot, projection, benches, ct).For(PolicyApi.MaintainArt);
    }

    internal Declared DeclareOrdersAll(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        lock (_mu)
        {
            _sculpting = PolicyApi.SculptureInProgress(benches);
        }
        var request = new ArtOrderRequest
        {
            Benches = benches,
            Colonists = projection.Facts.Colonists,
            Items = projection.Facts.Items,
            RoomsOwed = projection.Facts.SculptureRoomsOwed,
            Profiles = Unknown<IReadOnlyList<PawnProfile>>(),
        };
        if (projection.WorkPawns.TryGetValue(out var pawns))
        {
            var profiles = PolicyApi.Profiles(pawns);
            request.Profiles = Known(profiles);
            request.Demand = ArtDemand(projection, profiles);
        }
        return PolicyApi.DeclareArtOrders(request);
    }

    internal (ConcernId Key, ConcernId Need) ReportTarget() => (PolicyApi.MaintainArt, PolicyApi.MaintainArt);

    internal PurposeReport Report()
    {
        lock (_mu)
        {
            if (!_sculpting)
            {
                return new PurposeReport { Order = BuildingReasonNoDeficit };
            }
            return new PurposeReport { Order = WaitFor(PolicyApi.CauseExistingWork, "sculpture"), WorkTicks = ArtNativeWorkTicks };
        }
    }

    internal static ArtDemand ArtDemand(ColonyProjection facts, IReadOnlyList<PawnProfile> profiles)
    {
        facts.Resources.TryGetValue(out var stock);
        var rk = facts.Rooms.TryGetValue(out var rooms);
        var ck = facts.Facts.CurrentConstruction.TryGetValue(out var census);
        var sk = facts.Facts.Sleeping.TryGetValue(out var obs);
        var traits = SleepingTraits(facts);
        if (!rk || !ck || !sk || !census!.Colony || traits is null)
        {
            return PolicyApi.NewArtDemand(Unknown<SleepingObservation>(), [], [], stock, profiles, facts.Facts.Items);
        }
        facts.TechTier.TryGetValue(out var tier);
        return PolicyApi.NewArtDemand(facts.Facts.Sleeping, PolicyApi.RoomQualityTargets(obs!, traits, tier, facts.Impressiveness), PolicyApi.FurnitureRooms(rooms!, census, facts.Cells), stock, profiles, facts.Facts.Items);
    }
}
