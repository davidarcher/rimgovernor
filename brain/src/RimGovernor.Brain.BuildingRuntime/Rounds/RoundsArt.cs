using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The rooms/art Need's work: its pinned sculpture bills, declared to the work ledger and filed under the Need. The
/// ledger reads the Need's standing as of the last declaration, so no owner's open work gates the bills.
/// </summary>
public sealed class RoundsArtPlanner : IPurposeReporter
{
    /// <summary>The ledger key the art bills report under, a subject of the rooms/art Need.</summary>
    internal static readonly ConcernId ArtBillsKey = NeedId.Of(RoomsFamily.FamilyId, "art", "bills").FilingKey;

    private readonly Rounder _reviewer;
    private readonly object _mu = new();

    /// <summary>Whether a sculpture is in progress at a bench (the last declaration's reading).</summary>
    private bool _sculpting;

    /// <summary>The rooms/art Need as of the last declaration; unknown before the first.</summary>
    private Need? _need;

    public RoundsArtPlanner(Rounder reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("NewRoundsArtPlanner: reviewer == nil");
        }
        _reviewer = reviewer;
    }

    /// <summary>Declares the art orders of this review and returns the rooms/art Need's share.</summary>
    public Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct) =>
        Task.FromResult(DeclareOrdersAll(snapshot, projection, benches, ct).For(RoomsFamily.Art.FilingKey.Value));

    internal Declared DeclareOrdersAll(GenerationSnapshot snapshot, ColonyProjection projection, IReadOnlyList<GearBench> benches, CancellationToken ct)
    {
        var sculpting = ArtOrdersRules.SculptureInProgress(benches);
        var pawns = projection.WorkPawns.Require();
        var profiles = PawnRules.Profiles(pawns);
        var request = new ArtOrderRequest
        {
            Benches = [.. benches],
            Demand = ArtDemand(projection, profiles),
            Colonists = projection.Facts.Colonists,
            Items = projection.Facts.Items,
            RoomsOwed = projection.Facts.SculptureRoomsOwed,
            Profiles = [.. profiles],
        };
        var need = RoomsFamily.ArtNeed(new RoomsFacts { Profiles = Known(request.Profiles), SculptureRoomsOwed = request.RoomsOwed });
        lock (_mu)
        {
            _sculpting = sculpting;
            _need = need;
        }
        return ArtOrdersRules.DeclareArtOrders(request);
    }

    (ConcernId Key, ConcernId Need) IPurposeReporter.ReportTarget() => ReportTarget();

    PurposeReport IPurposeReporter.Report() => Report();

    internal static (ConcernId Key, ConcernId Need) ReportTarget() => (ArtBillsKey, RoomsFamily.Art.FilingKey);

    internal PurposeReport Report()
    {
        lock (_mu)
        {
            // Reports follow the declare that set the Need; a declare that raised never reaches here.
            var need = _need ?? throw new RequiredFactException("art need");
            if (!_sculpting)
            {
                return new PurposeReport { Need = need, Order = BuildingReasonNoDeficit };
            }
            return new PurposeReport { Need = need, Order = WaitFor(Cause.ExistingWork, "sculpture") };
        }
    }

    internal static ArtDemand ArtDemand(ColonyProjection facts, IReadOnlyList<PawnProfile> profiles)
    {
        var stock = facts.Resources.TryGetValue(out var known) ? known : throw new RequiredFactException("resources");
        var rooms = facts.Rooms.TryGetValue(out var room) ? room : throw new RequiredFactException("rooms");
        var census = facts.Facts.CurrentConstruction ?? throw new RequiredFactException("construction census");
        var obs = facts.Facts.Sleeping.TryGetValue(out var sleeping) ? sleeping : throw new RequiredFactException("sleeping census");
        var traits = RoundsSleepingUpkeepPlanner.SleepingTraits(facts) ?? throw new RequiredFactException("sleeping traits");
        facts.TechTier.TryGetValue(out var tier);
        return ArtRules.NewArtDemand(obs, RoomQualityRules.RoomQualityTargets(obs, traits, tier, facts.Impressiveness), FurnitureRoomsRules.FurnitureRooms(rooms, census, facts.Cells), stock, profiles, facts.Facts.Items);
    }
}
