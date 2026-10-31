using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// RG-HABITAT-005: the workshop host of an order no standing bench lists is not chosen before the shared layout comparison.
/// Each catalog host and deferral (or reuse of the held host) is a station graph of its own, seeded, read and priced by the
/// canonical ledger, so a room's temperature or a missing power grid can change which bench definition is planned. The
/// evaluator stands in for the native read: it maps each trial's planned host to the same planned workshop's facts and
/// runs the real ForecastValue ledger over them. The pairs below share the load and the two host definitions and vary
/// only one site or infrastructure fact.
/// </summary>
public sealed class WorkshopHostAlternativeTests
{
    private const double Day = GameTime.TicksPerDay;
    private const string Spot = "CraftingSpot", Smithy = "Smithy", Knife = "MakeKnife";
    private static readonly Resource KnifeItem = new("Knife"), Wood = new("WoodLog");
    private static readonly Cell Position = new(1, 1);
    private static readonly OrderSpec KnifeOrder = new() { Recipe = Knife, Target = 1, Mode = BillMode.StockTarget };

    /// <summary>What the planned workshop offers each host: heat, a reachable grid, the knife's value and the smithy's build.</summary>
    private sealed record Site(bool Heated = true, bool Grid = true, double Value = 10, long SmithyBuild = 1, bool SpotStands = false, BenchPower? SmithyPower = null);

    // The spot is half speed and indifferent to cold; the smithy is full speed in a heated room and 0.4 below 9 degrees.
    private static readonly Dictionary<string, BenchSpeedModel> Speeds = new()
    {
        [Spot] = new(.5, .1, []),
        [Smithy] = new(1, .1, [new BenchTemperaturePart(9, 35, .4)]),
    };

    /// <summary>
    /// The planned workshop of room 1 (PlannedRoomCreditTests: three builder-days of its own work) hosting the knife at
    /// <paramref name="host"/>, or nothing. A recipe run is two days of work at unit speed. The smithy's own build and,
    /// with no grid in reach, its generator and conduit are the room's prerequisite sites: their wood is charged and their
    /// owed work delays service.
    /// </summary>
    private static ForecastCoupledContext Context(string? host, Site site)
    {
        var context = PlannedRoomCreditTests.Context(PlannedRoomCredits.Shipped);
        var closure = context.Closures[0];
        PlannedBuildSite Required(string id, long wood) => new(id, 1, OpKind.Build, id, "", Rotation.North, [new(9, 1)]) { Material = [new(Wood, wood)] };
        var sites = host != Smithy ? [] : site.Grid ? new[] { Required("smithy", site.SmithyBuild) }
            : [Required("smithy", site.SmithyBuild), Required("generator", 2), Required("conduit", 1)];
        BenchPlacement placement = host == Spot && site.SpotStands ? new BenchPlacement.Standing(.5) : new BenchPlacement.Planned();
        return context with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Workshop },
            LayoutRooms = new Dictionary<int, PlannedRoom> { [1] = new() { Id = 1, Role = PlannedRole.Workshop, Production = host is null ? [] : [new(host, Knife, 1)] } },
            Closures = [closure with { Prediction = PlannedRoomPredictionTests.Closed("1", "WoodLog", "Workshop", 10, 1, 4, 0, false) with
                { WorkBenches = host is null ? [] : [new(host, Position, 0, 0, host == Smithy ? site.SmithyPower ?? new BenchPower.Unneeded() : new BenchPower.Unneeded(), placement)] } }],
            // A smithy read on a power network is the electric one: it runs no bills unpowered.
            WorkBenchSpeeds = site.SmithyPower is null ? Speeds : new Dictionary<string, BenchSpeedModel>(Speeds) { [Smithy] = Speeds[Smithy] with { RequiresPower = true } },
            ProductionOrders = new() { Status = WorkRules.LedgerViewReconciled, Orders = [KnifeOrder] },
            ProductionRecipes = new Dictionary<string, RecipeFlow> { [Knife] = new() { Products = [new(KnifeItem, 1)], Work = 2 * Day } },
            MaterialValues = new Dictionary<Resource, ConstructionMaterialValue>
                { [KnifeItem] = new(MaterialValueBasis.Market, site.Value), [Wood] = new(MaterialValueBasis.Labor, 1) },
            Shelters = [new(new(ForecastShelterSource.Planned, "1"), [Position], [], ShelterOutcomes.Estimated(0, 0, .001),
                site.Heated ? [new("heater", Position, true, .35, 20, CoupledThermalTests.Powered(10 * Day))] : [], []) { PlannedRoom = 1 }],
            RoomPrerequisites = sites.Length == 0 ? new Dictionary<int, EquatableArray<PlannedBuildSite>>()
                : new Dictionary<int, EquatableArray<PlannedBuildSite>> { [1] = EquatableArray.From(sites) },
            OwedWork = context.OwedWork.Concat(sites.Select(s => new KeyValuePair<string, double>(s.Id, s.Id == "smithy" ? 0 : Day))).ToDictionary(p => p.Key, p => p.Value),
        };
    }

    private static ForecastCoupledOutcome Outcome(ForecastCoupledContext context) => PlannedRoomCreditTests.Outcome(context) with
    {
        Terminal = new(new() { Origin = new(0), HorizonDays = 1, Coupled = context }, new Dictionary<string, ForecastCommitment>(),
            new Dictionary<Resource, double> { [KnifeItem] = 0 }, new Dictionary<string, double>(), null, new Dictionary<Resource, double>()),
    };

    private static ForecastValueResult Ledger(string? host, Site site)
    {
        var context = Context(host, site);
        return ForecastValue.Of(Outcome(context), context, Day);
    }

    /// <summary>The bench a trial's station graph plans for the knife, or null where it defers.</summary>
    private static string? HostOf(StationGraph graph) =>
        graph.Nodes.SelectMany(n => n.Production).Where(p => p.Recipe == Knife).Select(p => p.Bench).Distinct().SingleOrDefault();

    private static StationFacts Prepared(StationFacts? facts = null)
    {
        var f = facts ?? FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild", [KnifeOrder]);
        return LayoutDeriveRules.FirstPlanFacts(f.Survey, f, []);
    }

    private static HostedRegroupInputs Inputs(StationFacts prepared, HostAlternatives hosts) => new(
        [.. hosts.Alternatives.Select(h => new HostedRegroupInput(h, LayoutStationPlanRules.RegroupInputOf(prepared, h) with
            { Price = new() { Work = new(8000, LayoutRegroupRules.CellWalkDays(60)) } }))], hosts.Omitted);

    private static async Task<Regrouping> Compare(HostedRegroupInputs inputs, Func<string?, ForecastCoupledContext> contextOf, List<string?>? read = null) =>
        await LayoutRegroupRules.RegroupAsync(inputs, (candidates, _) => Task.FromResult<IReadOnlyList<RegroupValuation>>([.. candidates.Select(candidate =>
        {
            var host = HostOf(candidate.Layout.Graph);
            read?.Add(host);
            return LayoutRegroupRules.Assess(candidate.Id, () =>
            {
                var context = contextOf(host);
                var vector = new ForecastConsequenceVector([], [new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0)],
                    new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0), 0, []) { Value = new(ForecastValue.Of(Outcome(context), context, Day), context.Value) };
                return new RegroupValuation.Evaluated(candidate.Id, vector, []);
            });
        })]), TestContext.Current.CancellationToken);

    private static Task<Regrouping> Compare(Site site, List<string?>? read = null)
    {
        var prepared = Prepared();
        return Compare(Inputs(prepared, prepared.HostAlternatives()), host => Context(host, site), read);
    }

    private static RegroupDecision HostDecision(Regrouping result, string host) =>
        Assert.Single(result.Decisions, d => d.Kind == LayoutHostRules.HostMove && d.A == Knife && d.B == host);

    [Fact]
    public void TheLedgerPricesAnUnhostedLoadsWholeOutputLessItsLaborAtTheRoomsSpeed()
    {
        // Two work-days a knife at unit speed, a knife worth ten: the spot works it in four days, the smithy in two when
        // heated and five at 0.4 in the cold. The smithy's build is one wood; the spot is free.
        Assert.Equal(-(10 - 4), Ledger(Spot, new()).Investment, 9);
        Assert.Equal(-(10 - 2) + 1, Ledger(Smithy, new()).Investment, 9);
        Assert.Equal(-(10 - 5) + 1, Ledger(Smithy, new(Heated: false)).Investment, 9);
        Assert.Equal(-(10 - 2) + 1 + 3, Ledger(Smithy, new(Grid: false)).Investment, 9);
        Assert.Equal(0, Ledger(null, new()).Investment, 9);
        var unread = Context(Spot, new()) with { ProductionRecipes = new Dictionary<string, RecipeFlow> { [Knife] = new() { Products = [new(KnifeItem, 1)] } } };
        Assert.Equal("workshop recipe work:" + Knife, Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Outcome(unread), unread, Day)).Fact);
    }

    [Theory]
    [InlineData(true, Smithy)]
    [InlineData(false, Spot)]
    public async Task TheRoomsTemperatureChangesWhichBenchDefinitionWins(bool heated, string winner)
    {
        var read = new List<string?>();
        var result = await Compare(new(Heated: heated), read);

        Assert.Equal(winner, HostOf(result.Graph));
        // The baseline defers and both catalog hosts are compared in the one batch, each a graph of its own.
        Assert.Equal(new string?[] { null, Spot, Smithy }, read.Take(3));
        Assert.Equal(LayoutRegroupRules.RegroupApplied, HostDecision(result, winner).Verdict);
        Assert.Equal(LayoutRegroupRules.RegroupKept, HostDecision(result, winner == Spot ? Smithy : Spot).Verdict);
        Assert.True(HostDecision(result, winner).After < 0);
    }

    [Theory]
    [InlineData(true, Smithy)]
    [InlineData(false, Spot)]
    public async Task AGeneratorAndConduitOwedForTheSmithyChangeWhichBenchDefinitionWins(bool grid, string winner)
    {
        var result = await Compare(new(Grid: grid));

        Assert.Equal(winner, HostOf(result.Graph));
        var smithy = Context(Smithy, new(Grid: grid));
        // The prerequisites' owed work delays the smithy's room by two days; their wood is charged once.
        Assert.Equal((grid ? 3 : 5) * Day, Assert.Single(Outcome(smithy).ServedRooms).ServedAt);
    }

    [Theory]
    [InlineData(4L, Spot)]
    [InlineData(1L, Smithy)]
    public async Task TheHeldSpotIsReusedUntilANewSmithysGainExceedsItsBuild(long smithyBuild, string winner)
    {
        var prepared = Prepared();
        var held = new LayoutPlan { Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Workshop, Production = [new(Spot, Knife, 1)] }] };
        var hosts = LayoutHostRules.Of(prepared.Catalog, prepared.LedgerOrders(), held);
        Assert.Equal(new HostChoice.Retained(Spot), Assert.Single(hosts.Baseline.Loads).Host);
        Assert.Equal(["host:baseline", "host:MakeKnife/Smithy", "host:MakeKnife/deferred"], hosts.Alternatives.Select(a => a.Id));

        var result = await Compare(Inputs(prepared, hosts), host => Context(host, new(SmithyBuild: smithyBuild, SpotStands: true)));

        Assert.Equal(winner, HostOf(result.Graph));
        Assert.Equal(winner == Smithy ? LayoutRegroupRules.RegroupApplied : LayoutRegroupRules.RegroupKept, HostDecision(result, Smithy).Verdict);
        Assert.Equal(LayoutRegroupRules.RegroupKept, HostDecision(result, LayoutHostRules.DeferredName).Verdict);
    }

    [Fact]
    public async Task DeferralIsRetainedWhenNoHostsOutputIsWorthItsLaborAndBuild()
    {
        var result = await Compare(new(Value: 1));

        Assert.Null(HostOf(result.Graph));
        Assert.DoesNotContain(result.Graph.Nodes, n => n.Def is Spot or Smithy);
        Assert.All(new[] { Spot, Smithy }, host =>
        {
            var decision = HostDecision(result, host);
            Assert.Equal((LayoutRegroupRules.RegroupKept, "regroup_canonical_credit_loss"), (decision.Verdict, decision.Reason));
            Assert.True(decision.After > 0);
        });
    }

    [Fact]
    public async Task AHostPastTheBoundIsNamedAsTruncatedAndNeverRead()
    {
        var f = FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild", [KnifeOrder]);
        var stations = new Dictionary<string, Station>(f.Catalog.Stations);
        foreach (var i in Enumerable.Range(0, 10))
        {
            var def = "Bench" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            stations[def] = stations[Spot] with { Shape = new InteriorPieceDef(def, new Cell(1, 1), RoomRole.None, new InteractionCell.None()) };
        }
        var prepared = Prepared(f with { Catalog = f.Catalog with { Stations = stations } });
        var hosts = prepared.HostAlternatives();

        // Twelve hosts, eight compared beside the baseline in host-name order; the rest are named, not dropped silently.
        Assert.Equal(1 + LayoutHostRules.HostTrials, hosts.Alternatives.Count);
        Assert.Equal(["Bench08", "Bench09", Spot, Smithy], hosts.Omitted.Select(o => o.Host.Bench));
        var read = new List<string?>();
        var result = await Compare(Inputs(prepared, hosts), _ => Context(null, new()), read);

        Assert.DoesNotContain(Smithy, read);
        Assert.Null(HostOf(result.Graph));
        var skipped = result.Decisions.Where(d => d.Verdict == LayoutRegroupRules.RegroupSkipped).ToArray();
        Assert.Equal(["Bench08", "Bench09", Spot, Smithy], skipped.Select(d => d.B));
        Assert.All(skipped, d => Assert.Equal((LayoutHostRules.HostMove, LayoutHostRules.HostTruncated, Knife), (d.Kind, d.Reason, d.A)));
        Assert.Equal(LayoutHostRules.HostTrials, result.Decisions.Count(d => d.Kind == LayoutHostRules.HostMove && d.Verdict == LayoutRegroupRules.RegroupKept));
    }

    /// <summary>
    /// Task 1.2: a planned electric smithy is powered only where native reads a network whose surplus covers its draw
    /// (BenchPower). Unpowered it runs no bills, so the same room and load flip the host to the spot.
    /// </summary>
    [Theory]
    [InlineData(1000d, Smithy)]
    [InlineData(100d, Spot)]
    public async Task TheSmithysNetworkSurplusChangesWhichBenchDefinitionWins(double surplus, string winner)
    {
        var result = await Compare(new(SmithyPower: new BenchPower.Connected("net", 250, surplus)));

        Assert.Equal(winner, HostOf(result.Graph));
        Assert.Equal(LayoutRegroupRules.RegroupApplied, HostDecision(result, winner).Verdict);
    }

    [Fact]
    public void AnUnconnectedElectricSmithyIsAKnownZeroWhileAnUnreadSurplusIsNamed()
    {
        Assert.Equal(-(10 - 2) + 1, Ledger(Smithy, new(SmithyPower: new BenchPower.Connected("net", 250, 1000))).Investment, 9);
        // Read and unpowered: no output, only its build is charged.
        Assert.Equal(1, Ledger(Smithy, new(SmithyPower: new BenchPower.Unconnected())).Investment, 9);
        var unread = Context(Smithy, new(SmithyPower: new BenchPower.Connected("net", 250, double.NaN)));
        Assert.Equal("workbench power surplus:net", Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Outcome(unread), unread, Day)).Fact);
    }

    [Fact]
    public void BenchesOnOneNetworkShareItsSurplusOnceEachInAnchorOrder()
    {
        PlannedBenchSpeed Bench(int x) => new(Smithy, new(x, 1), 0, 0, new BenchPower.Connected("net", 250, 300), new BenchPlacement.Planned());
        var context = Context(Smithy, new());
        var closure = context.Closures[0];
        // The same bench in a second stuff's prediction draws once.
        context = context with { Closures = [closure with { Prediction = closure.Prediction with { WorkBenches = [Bench(2), Bench(1)] } },
            closure with { Stuff = "Steel", Prediction = closure.Prediction with { WorkBenches = [Bench(1)] } }] };

        Assert.True(ForecastWorkshop.Powered(context, Bench(1)));
        Assert.False(ForecastWorkshop.Powered(context, Bench(2)));
        Assert.False(ForecastWorkshop.Powered(context, Bench(1) with { Power = new BenchPower.Unconnected() }));
    }

    [Fact]
    public async Task AFactOneAlternativeLeavesUnreadExcludesOnlyThatAlternative()
    {
        var result = await Compare(new(SmithyPower: new BenchPower.Connected("net", 250, double.NaN)));

        Assert.Equal(Spot, HostOf(result.Graph));
        var smithy = HostDecision(result, Smithy);
        Assert.Equal((LayoutRegroupRules.RegroupKept, LayoutRegroupRules.RegroupUnassessed), (smithy.Verdict, smithy.Reason));
        Assert.Equal(["workbench power surplus:net"], smithy.Effects);
        Assert.Equal(LayoutRegroupRules.RegroupApplied, HostDecision(result, Spot).Verdict);
    }
}
