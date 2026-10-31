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
    private sealed record Site(bool Heated = true, bool Grid = true, double Value = 10, long SmithyBuild = 1, bool SpotStands = false, BenchPower? SmithyPower = null,
        int? SpotFetch = null, int? SmithyFetch = null, double SmithyBase = 1, double WoodValue = 1, double SmithyWork = 0,
        PlanningFixedConstruction? Conduit = null, OrderSpec? Order = null, double? Butchery = null, double Drawn = 0, double ShareRuns = 0, double? SpotReady = null)
    {
        public string Recipe => (Order ?? KnifeOrder).Recipe;
    }

    // The spot is half speed and indifferent to cold; the smithy is full speed in a heated room and 0.4 below 9 degrees.
    private static readonly Dictionary<string, BenchSpeedModel> Speeds = new()
    {
        [Spot] = new(.5, .1, []),
        [Smithy] = new(1, .1, [new BenchTemperaturePart(9, 35, .4)]),
    };

    /// <summary>
    /// The planned workshop of room 1 (PlannedRoomCreditTests: three builder-days of its own work) hosting the knife at
    /// <paramref name="host"/>, or nothing: one bench (a run a day), a chain <c>first&gt;then</c> (the bridge carries the same
    /// load) or a pair <c>def+also</c> (the kept spare adds <see cref="Site.ShareRuns"/> a day). A recipe run is two days of work at
    /// unit speed. The smithy's own build and, with no grid in reach, its generator and conduit are the room's prerequisite
    /// sites: their wood is charged and their owed work delays service. A planned spot (<see cref="Site.SpotReady"/>) is a
    /// room site finishing at that tick, owing no builder time.
    /// </summary>
    private static ForecastCoupledContext Context(string? host, Site site)
    {
        var context = PlannedRoomCreditTests.Context(PlannedRoomCredits.Shipped);
        var closure = context.Closures[0];
        PlannedBuildSite Required(string id, long wood) => new(id, 1, OpKind.Build, id, "", Rotation.North, [new(9, 1)]) { Material = [new(Wood, wood)] };
        PlannedProduction[] made = host is null ? []
            : host.Split('>') is [var first, var then] ? [new(then, site.Recipe, 1), new(first, site.Recipe, 1) { Step = HostStep.Bridge }]
            : host.Split('+') is [var def, var also] ? [new(def, site.Recipe, 1), new(also, site.Recipe, site.ShareRuns) { Step = HostStep.Share }]
            : [new(host, site.Recipe, 1)];
        var defs = made.Select(p => p.Bench).Distinct().ToArray();
        var sites = !defs.Contains(Smithy) ? [] : site.Grid ? new[] { Required("smithy", site.SmithyBuild) }
            : [Required("smithy", site.SmithyBuild), Required("generator", 2), Required("conduit", 1)];
        var spotSite = PlannedRules.PlannedPieceSitePrefix(1, Spot) + "1/1";
        var speeds = new Dictionary<string, BenchSpeedModel>(Speeds) { [Smithy] = Speeds[Smithy] with { Base = site.SmithyBase, RequiresPower = site.SmithyPower is not null } };
        var recipe = site.Butchery is { } ? new RecipeFlow { Butchery = true, Work = 2 * Day } : new RecipeFlow { Products = [new(KnifeItem, 1)], Work = 2 * Day };
        PlannedBenchSpeed Bench(string def) => new(def, Position, 0, 0, def == Smithy ? site.SmithyPower ?? new BenchPower.Unneeded() : new BenchPower.Unneeded(),
            def == Spot && site.SpotStands ? new BenchPlacement.Standing(.5) : new BenchPlacement.Planned(), (def == Smithy ? site.SmithyFetch : site.SpotFetch) is { } fetch ? new StorageReach(fetch) : null);
        return context with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Workshop },
            LayoutRooms = new Dictionary<int, PlannedRoom> { [1] = new() { Id = 1, Role = PlannedRole.Workshop, Production = [.. made] } },
            Closures = [closure with { Prediction = PlannedRoomPredictionTests.Closed("1", "WoodLog", "Workshop", 10, 1, 4, 0, false) with
                { WorkBenches = [.. defs.Select(Bench)] } }],
            PlannedSites = site.SpotReady is null || !defs.Contains(Spot) ? context.PlannedSites : [.. context.PlannedSites, new(spotSite, 1, "", [], [spotSite + "/finish"])],
            // A smithy read on a power network is the electric one: it runs no bills unpowered.
            WorkBenchSpeeds = speeds,
            ProductionOrders = new() { Status = WorkRules.LedgerViewReconciled, Orders = [site.Order ?? KnifeOrder] },
            ProductionRecipes = new Dictionary<string, RecipeFlow> { [site.Recipe] = recipe },
            ButcheryRunValue = site.Butchery ?? double.NaN,
            PowerConduit = site.Conduit,
            MaterialValues = new Dictionary<Resource, ConstructionMaterialValue>
                { [KnifeItem] = new(MaterialValueBasis.Market, site.Value), [Wood] = new(MaterialValueBasis.Labor, site.WoodValue) },
            Shelters = [new(new(ForecastShelterSource.Planned, "1"), [Position], [], ShelterOutcomes.Estimated(0, 0, .001),
                site.Heated ? [new("heater", Position, true, .35, 20, CoupledThermalTests.Powered(10 * Day))] : [], []) { PlannedRoom = 1 }],
            RoomPrerequisites = sites.Length == 0 ? new Dictionary<int, EquatableArray<PlannedBuildSite>>()
                : new Dictionary<int, EquatableArray<PlannedBuildSite>> { [1] = EquatableArray.From(sites) },
            OwedWork = context.OwedWork.Concat(sites.Select(s => new KeyValuePair<string, double>(s.Id, s.Id == "smithy" ? site.SmithyWork : Day)))
                .Append(new(spotSite, 0)).ToDictionary(p => p.Key, p => p.Value),
        };
    }

    private static ForecastCoupledOutcome Outcome(ForecastCoupledContext context, Site? site = null) => PlannedRoomCreditTests.Outcome(context) with
    {
        Terminal = new(new() { Origin = new(0), HorizonDays = 1, Coupled = context }, new Dictionary<string, ForecastCommitment>(),
            new Dictionary<Resource, double> { [KnifeItem] = 0 },
            site?.SpotReady is { } ready ? new Dictionary<string, double> { [PlannedRules.PlannedPieceSitePrefix(1, Spot) + "1/1/finish"] = ready } : new Dictionary<string, double>(),
            null, new Dictionary<Resource, double> { [KnifeItem] = site?.Drawn ?? 0 }),
    };

    private static ForecastValueResult Ledger(string? host, Site site)
    {
        var context = Context(host, site);
        return ForecastValue.Of(Outcome(context, site), context, Day);
    }

    /// <summary>The host a trial's station graph plans for the knife, named as LayoutHostRules.NameOf names it, or null where it defers.</summary>
    private static string? HostOf(StationGraph graph, string recipe = Knife)
    {
        var made = graph.Nodes.SelectMany(n => n.Production).Where(p => p.Recipe == recipe).ToArray();
        if (made.Length == 0) return null;
        var host = made.Where(p => p.Step == HostStep.Host).Select(p => p.Bench).Distinct().Single();
        return made.FirstOrDefault(p => p.Step == HostStep.Bridge) is { } bridge ? bridge.Bench + ">" + host
            : made.FirstOrDefault(p => p.Step == HostStep.Share) is { } share ? host + "+" + share.Bench : host;
    }

    private static StationFacts Prepared(StationFacts? facts = null)
    {
        var f = facts ?? FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild", [KnifeOrder]);
        return LayoutDeriveRules.FirstPlanFacts(f.Survey, f, []);
    }

    private static HostedRegroupInputs Inputs(StationFacts prepared, HostAlternatives hosts) => new(
        [.. hosts.Alternatives.Select(h => new HostedRegroupInput(h, LayoutStationPlanRules.RegroupInputOf(prepared, h) with
            { Price = new() { Work = new(8000, LayoutRegroupRules.CellWalkDays(60)) } }))], hosts.Omitted);

    private static Task<Regrouping> Compare(HostedRegroupInputs inputs, Func<string?, ForecastCoupledContext> contextOf, List<string?>? read = null, string recipe = Knife) =>
        Compare(inputs, host => { var context = contextOf(host); return (context, Outcome(context)); }, read, recipe);

    /// <summary>The comparison over each trial's planned host of <paramref name="recipe"/>, valued by the evaluator's context and outcome for that host.</summary>
    private static async Task<Regrouping> Compare(HostedRegroupInputs inputs, Func<string?, (ForecastCoupledContext Context, ForecastCoupledOutcome Outcome)> valueOf, List<string?>? read, string recipe) =>
        await LayoutRegroupRules.RegroupAsync(inputs, (candidates, _) => Task.FromResult<IReadOnlyList<RegroupValuation>>([.. candidates.Select(candidate =>
        {
            var host = HostOf(candidate.Layout.Graph, recipe);
            read?.Add(host);
            return LayoutRegroupRules.Assess(candidate.Id, () =>
            {
                var (context, outcome) = valueOf(host);
                var vector = new ForecastConsequenceVector([], [new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0)],
                    new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0), 0, []) { Value = new(ForecastValue.Of(outcome, context, Day), context.Value) };
                return new RegroupValuation.Evaluated(candidate.Id, vector, []);
            });
        })]), TestContext.Current.CancellationToken);

    private static Task<Regrouping> Compare(Site site, List<string?>? read = null)
    {
        var prepared = Prepared();
        return Compare(Inputs(prepared, prepared.HostAlternatives()), host => Context(host, site), read);
    }

    private static RegroupDecision HostDecision(Regrouping result, string host, string recipe = Knife) =>
        Assert.Single(result.Decisions, d => d.Kind == LayoutHostRules.HostMove && d.A == recipe && d.B == host);

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
        var hosts = LayoutHostRules.Of(prepared.Catalog, prepared.LedgerOrders(), held, new HostCensus(prepared.BenchDefinitions, prepared.Recipes, prepared.FinishedResearch));
        Assert.Equal(new HostChoice.Retained(Spot), Assert.Single(hosts.Baseline.Loads).Host);
        Assert.Equal(["host:baseline", "host:MakeKnife/Smithy", "host:MakeKnife/deferred", "host:MakeKnife/CraftingSpot>Smithy", "host:MakeKnife/Smithy>CraftingSpot"],
            hosts.Alternatives.Select(a => a.Id));

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
        var prepared = Prepared(f with { Catalog = f.Catalog with { Stations = stations }, BenchDefinitions = FirstRoomPlanTests.Buildable(stations.Keys) });
        var hosts = prepared.HostAlternatives();

        // Twelve hosts, eight compared beside the baseline in host-name order; the rest, the four single hosts and every
        // spot-now/table-later chain after them, are named, not dropped silently.
        Assert.Equal(1 + LayoutHostRules.HostTrials, hosts.Alternatives.Count);
        Assert.Equal(["Bench08", "Bench09", Spot, Smithy], hosts.Omitted.Take(4).Select(o => o.Host.Bench));
        Assert.All(hosts.Omitted.Skip(4), o => Assert.IsType<HostChoice.Chain>(o.Host));
        Assert.Equal(4 + (12 * 11), hosts.Omitted.Count);
        var read = new List<string?>();
        var result = await Compare(Inputs(prepared, hosts), _ => Context(null, new()), read);

        Assert.DoesNotContain(Smithy, read);
        Assert.Null(HostOf(result.Graph));
        var skipped = result.Decisions.Where(d => d.Verdict == LayoutRegroupRules.RegroupSkipped).ToArray();
        Assert.Equal(["Bench08", "Bench09", Spot, Smithy], skipped.Take(4).Select(d => d.B));
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
        Assert.Equal(1, Ledger(Smithy, new(SmithyPower: new BenchPower.Unconnected(null))).Investment, 9);
        var unread = Context(Smithy, new(SmithyPower: new BenchPower.Connected("net", 250, double.NaN)));
        Assert.Equal("workbench power surplus:net", Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Outcome(unread), unread, Day)).Fact);
    }

    [Fact]
    public void BenchesOnOneNetworkShareItsSurplusOnceEachInAnchorOrder()
    {
        PlannedBenchSpeed Bench(int x) => new(Smithy, new(x, 1), 0, 0, new BenchPower.Connected("net", 250, 300), new BenchPlacement.Planned(), null);
        var context = Context(Smithy, new());
        var closure = context.Closures[0];
        // The same bench in a second stuff's prediction draws once.
        context = context with { Closures = [closure with { Prediction = closure.Prediction with { WorkBenches = [Bench(2), Bench(1)] } },
            closure with { Stuff = "Steel", Prediction = closure.Prediction with { WorkBenches = [Bench(1)] } }] };

        Assert.True(ForecastWorkshop.Powered(context, Bench(1)));
        Assert.False(ForecastWorkshop.Powered(context, Bench(2)));
        Assert.False(ForecastWorkshop.Powered(context, Bench(1) with { Power = new BenchPower.Unconnected(null) }));
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

    /// <summary>
    /// Task 3.1 (a): the held spot stands far from storage; a new smithy of the same speed would stand beside it but owes half a
    /// day of building and a wood. Every run of the spot hauls its ingredients and product over the distance and back; the
    /// smithy's late service loses runs of a load that fills the bench. The fetch decides which single host wins; the chain
    /// the spot bridges wins over both (task 3.2).
    /// </summary>
    [Theory]
    [InlineData(200, Smithy)]
    [InlineData(40, Spot)]
    public async Task ADistantStandingBenchLosesToANearNewOneOnlyWhenItsHaulingExceedsTheConstructionDelay(int fetch, string winner)
    {
        var prepared = Prepared();
        var held = new LayoutPlan { Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Workshop, Production = [new(Spot, Knife, 1)] }] };
        var hosts = LayoutHostRules.Of(prepared.Catalog, prepared.LedgerOrders(), held, new HostCensus(prepared.BenchDefinitions, prepared.Recipes, prepared.FinishedResearch));
        var site = new Site(Value: 4.5, SpotStands: true, SpotFetch: fetch, SmithyFetch: 0, SmithyBase: .5, WoodValue: .1, SmithyWork: Day / 2,
            Order: KnifeOrder with { Target = 10 });
        // Ten knives fill the bench for the whole horizon: the spot serves from day 3, the smithy from day 3.5.
        var haul = ForecastWorkshop.HaulDays(fetch);
        Assert.Equal(2d * fetch * DefenseCorridorRoutes.PathMoveCardinal / Day, haul, 12);
        Assert.Equal(-7 * (.5 - haul), Ledger(Spot, site).Investment, 9);
        Assert.Equal((-6.5 * .5) + .1, Ledger(Smithy, site).Investment, 9);

        // Between the single hosts the fetch decides.
        var singles = hosts with { Alternatives = [.. hosts.Alternatives.Where(a => !a.Id.Contains('>', StringComparison.Ordinal))] };
        var result = await Compare(Inputs(prepared, singles), host => Context(host, site));

        Assert.Equal(winner, HostOf(result.Graph));
        Assert.Equal(winner == Smithy ? LayoutRegroupRules.RegroupApplied : LayoutRegroupRules.RegroupKept, HostDecision(result, Smithy).Verdict);

        // Task 3.2: the standing spot bridging the smithy's half day of building beats both, its runs still worth their haul.
        Assert.Equal(Chain, HostOf((await Compare(Inputs(prepared, hosts), host => Context(host, site))).Graph));
    }

    /// <summary>The planned kitchen a trial hosts the load in, at <paramref name="cleanliness"/> (null: it cooks nothing).</summary>
    private static (ForecastCoupledContext, ForecastCoupledOutcome) Kitchen(double? cleanliness) =>
        (KitchenCreditTests.Context([KitchenCreditTests.Room(1, PlannedRole.Kitchen, meals: cleanliness is null ? 0 : 6)],
            [KitchenCreditTests.Closure(1, cleanliness ?? 0)]), KitchenCreditTests.Serving(1));

    /// <summary>Task 3.1 (b): the same meals cooked in a dirty kitchen poison more of them than in a clean one; the clean host wins.</summary>
    [Theory]
    [InlineData(true, Spot)]
    [InlineData(false, Smithy)]
    public async Task ADirtyKitchenLosesToACleanOneOnPoisoning(bool spotClean, string winner)
    {
        var prepared = Prepared();
        static double Cleanliness(bool clean) => clean ? -1 : -4;
        var result = await Compare(Inputs(prepared, prepared.HostAlternatives()),
            host => Kitchen(host == Spot ? Cleanliness(spotClean) : host == Smithy ? Cleanliness(!spotClean) : null), null, Knife);

        Assert.Equal(winner, HostOf(result.Graph));
        Assert.Equal(LayoutRegroupRules.RegroupKept, HostDecision(result, winner == Spot ? Smithy : Spot).Verdict);
    }

    /// <summary>Task 3.1 (f): an unread planned-room Cleanliness leaves only the alternative that reads that room unassessed, named.</summary>
    [Fact]
    public async Task AnUnreadRoomCleanlinessMakesOnlyThatRoomsAlternativeUnassessed()
    {
        var prepared = Prepared();
        var result = await Compare(Inputs(prepared, prepared.HostAlternatives()),
            host => Kitchen(host == Spot ? -4 : host == Smithy ? double.NaN : null), null, Knife);

        Assert.Equal(Spot, HostOf(result.Graph));
        var smithy = HostDecision(result, Smithy);
        Assert.Equal((LayoutRegroupRules.RegroupKept, LayoutRegroupRules.RegroupUnassessed), (smithy.Verdict, smithy.Reason));
        Assert.Equal(["planned room cleanliness:1"], smithy.Effects);
        Assert.Equal(LayoutRegroupRules.RegroupApplied, HostDecision(result, Spot).Verdict);
    }

    /// <summary>
    /// Task 3.1 (c): an electric smithy with no network of its own is powered through the line to the nearest network whose
    /// surplus covers it, and that line is its cost: a conduit's work a cell. Far from the network the line outweighs the
    /// smithy's doubled throughput and the spot that needs no power (a campfire's place) wins; near, the smithy does.
    /// </summary>
    [Theory]
    [InlineData(2, Smithy)]
    [InlineData(20, Spot)]
    public async Task AnUnconnectedElectricBenchCarriesItsConnectionCost(int cells, string winner)
    {
        var site = new Site(SmithyPower: new BenchPower.Unconnected(new NetworkReach("net", cells, 250, 1000)),
            Conduit: new PlanningFixedConstruction { WorkToBuild = Day / 10, Costs = [] });
        Assert.Equal(-(10 - 2) + 1 + (.1 * cells), Ledger(Smithy, site).Investment, 9);
        var unread = Context(Smithy, site with { Conduit = null });
        Assert.Equal("power_conduit_construction", Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Outcome(unread), unread, Day)).Fact);

        var result = await Compare(site);

        Assert.Equal(winner, HostOf(result.Graph));
        Assert.Equal(LayoutRegroupRules.RegroupApplied, HostDecision(result, winner).Verdict);
    }

    private const string ButcherSpot = "ButcherSpot", ButcherTable = "TableButcher", Butcher = "ButcherCorpseFlesh";

    /// <summary>The first-plan facts with the game's butcher spot (half speed, free) and table (full speed) hosting the flesh recipe, and the filth model butchering is priced through.</summary>
    private static StationFacts ButcherFacts(OrderSpec order)
    {
        var f = FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild", [order]);
        Station Host(string host, string like, int width) => f.Catalog.Stations[like] with
        {
            Shape = new InteriorPieceDef(host, new Cell(width, 1), RoomRole.None, new InteractionCell.None()),
            Capacity = f.Catalog.Stations[like].Capacity with { Recipes = [new RecipeWorkAmount(Butcher, 1000)] },
        };
        var stations = new Dictionary<string, Station>(f.Catalog.Stations) { [ButcherSpot] = Host(ButcherSpot, Spot, 1), [ButcherTable] = Host(ButcherTable, Smithy, 3) };
        return f with
        {
            Catalog = f.Catalog with { Stations = stations, Filth = BedStationPriceTests.Filth }, // Butchering soils its room.
            BenchDefinitions = FirstRoomPlanTests.Buildable(stations.Keys),
            Recipes = new Dictionary<string, RecipeFlow>(f.Recipes) { [Butcher] = new RecipeFlow { Butchery = true, Work = 1000 } },
        };
    }

    private static FoodSupply Supply(params FoodStock[] stocks) =>
        new() { Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] }, Stocks = stocks };

    private static FoodStock Corpse(bool forbidden = false, double nutrition = 10) => new()
    {
        Id = "corpse", DefName = "Corpse_Muffalo", Nutrition = nutrition, Rot = new FoodRot.Rots((long)Day), Place = new FoodPlace.Stands(false, ""),
        TemperatureC = 20, RotRate = 1, Forbidden = forbidden, Form = new FoodForm.Corpse(100, 1),
        IsHumanMeat = false, RawMeat = false, IsHumanlike = false, Vegetable = false, RawClass = FoodIngredientClass.None, Holder = new PawnId(string.Empty), Eaters = [], Count = 0, UnitMass = 0,
    };

    private static AcquisitionSource Prey(double nutrition) => new()
    {
        Observed = new() { Source = new() { Id = "boar" }, Token = "boar", Resource = "Meat_Boar", Yield = 1, Designation = new AcquisitionDesignation.Open(), Taken = false,
            Kind = new AcquisitionKind.Hunt(100, 1, false, false, false, false) },
        Entity = new() { Id = "boar", DefName = "Boar", Label = "Boar", Place = new EntityPlace.OnMap(1, new Cell(10, 10)) },
        NutritionYield = nutrition,
    };

    private static FoodOrderRequest ButcherRequest(ProductionBench[] benches, FoodStock[] stocks, AcquisitionSource[]? prey = null) => new()
    {
        Purpose = ProductionRules.ButcherFood, Benches = EquatableArray.From(benches), Supply = Supply(stocks), Humans = Supply(),
        Facts = ColonyFactsCoreTests.Reading().Facts with { FoodWork = new() { ButcherRecipe = Butcher }, Acquisition = EquatableArray.From(prey ?? []) },
    };

    /// <summary>
    /// Task 3.1 (d): a butchering owed with no butcher bench standing is an unhosted ledger order (no bench kind), never a
    /// spot or table chosen up front: the comparison offers both and deferral, and which wins is the consequence of the
    /// table's build against the spot's slower labor.
    /// </summary>
    [Theory]
    [InlineData(1L, ButcherTable)]
    [InlineData(4L, ButcherSpot)]
    public async Task AButcherOrderWithNoBenchIsUnhostedAndItsSpotOrTableIsChosenByConsequence(long tableBuild, string winner)
    {
        var order = FoodOrdersRules.UnhostedFoodOrder(ButcherRequest([], [Corpse()]));
        Assert.NotNull(order);
        Assert.Equal((Butcher, "", BillMode.ButcherForever, 1d), (order.Recipe, order.BenchKind, order.Mode, (double)order.Target));
        var prepared = Prepared(ButcherFacts(order));
        var hosts = prepared.HostAlternatives();
        Assert.Equal(["host:baseline", "host:" + Butcher + "/" + ButcherSpot, "host:" + Butcher + "/" + ButcherTable, "host:" + Butcher + "/" + ButcherSpot + ">" + ButcherTable,
            "host:" + Butcher + "/" + ButcherTable + ">" + ButcherSpot], hosts.Alternatives.Select(a => a.Id));

        // The evaluator reads the spot as the harness's half-speed spot and the table as its full-speed smithy.
        static string? Role(string? host) => host?.Replace(ButcherSpot, Spot, StringComparison.Ordinal).Replace(ButcherTable, Smithy, StringComparison.Ordinal);
        var site = new Site(SmithyBuild: tableBuild, Order: order, Butchery: 10);
        Assert.Equal(-(10 - 4), Ledger(Spot, site).Investment, 9);
        var result = await Compare(Inputs(prepared, hosts), host => Context(Role(host), site), null, Butcher);

        Assert.Equal(winner, HostOf(result.Graph, Butcher));
        Assert.Equal(LayoutRegroupRules.RegroupApplied, HostDecision(result, winner, Butcher).Verdict);
    }

    [Fact]
    public void AButcherOrderWaitsOnlyForACorpseOrAnOfferedHuntAndNeverOnAStandingBench()
    {
        Assert.Null(FoodOrdersRules.UnhostedFoodOrder(ButcherRequest([ProductionBenchFixtures.Butchering("spot", ButcherSpot, "spot", true)], [Corpse()])));
        Assert.Null(FoodOrdersRules.UnhostedFoodOrder(ButcherRequest([], [Corpse(forbidden: true)])));
        // Native offers no hunt worth taking until a butcher bench stands: an offered food hunt owes one run.
        Assert.Equal(1, FoodOrdersRules.UnhostedFoodOrder(ButcherRequest([], [], [Prey(5)]))!.Target);
        Assert.Equal(2, FoodOrdersRules.UnhostedFoodOrder(ButcherRequest([], [Corpse(), Corpse() with { Id = "corpse-2" }]))!.Target);
        var unread = ButcherRequest([], [Corpse()]);
        Assert.Equal("butcher recipe", Assert.Throws<RequiredFactException>(() =>
            FoodOrdersRules.UnhostedFoodOrder(unread with { Facts = unread.Facts with { FoodWork = new() } })).Fact);
    }

    [Fact]
    public void AButcheringRunIsWorthItsCorpsesMeatElseTheOfferedPreysAtTheCropWorkANutritionCosts()
    {
        var food = new FoodWorkFacts { GrowTicksPerNutrition = Day / 2 };
        Assert.Equal(5, ForecastWorkshop.ButcheryRunValueOf([Corpse()], [Prey(4)], food));
        Assert.Equal(2, ForecastWorkshop.ButcheryRunValueOf([Corpse(forbidden: true)], [Prey(4)], food));
        Assert.True(double.IsNaN(ForecastWorkshop.ButcheryRunValueOf([], [], food)));
        Assert.True(double.IsNaN(ForecastWorkshop.ButcheryRunValueOf([Corpse()], [], new FoodWorkFacts())));
        var unread = Context(Spot, new(Order: KnifeOrder with { Recipe = Butcher, Mode = BillMode.ButcherForever }, Butchery: 1)) with { ButcheryRunValue = double.NaN };
        Assert.Equal("butchery_run_value", Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Outcome(unread), unread, Day)).Fact);
    }

    /// <summary>Task 3.1 (e): one colonist no longer caps the benches; a load past a bench's day plans as many as it fills.</summary>
    [Fact]
    public void PopulationAloneNoLongerCapsTheBenchesALoadNeeds()
    {
        var order = KnifeOrder with { Target = 100 };
        var prepared = Prepared(FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild", [order], colonists: 1));
        var spot = prepared.HostAlternatives().Alternatives.Single(a => a.Id == "host:" + Knife + "/" + Spot);
        var node = Assert.Single(LayoutRegroupRules.Seed(LayoutStationPlanRules.RegroupInputOf(prepared, spot)).Graph.Nodes, n => n.Def == Spot);

        // A hundred knives at the spot's half speed are 200000 ticks a day: four spots, not one for the one colonist.
        Assert.Equal(200000, node.DailyWork, 6);
        Assert.Equal(4, node.Count);
    }

    /// <summary>The prepared knife facts with each named bench gated behind Smithing in the planning census.</summary>
    private static StationFacts Gated(params string[] gated)
    {
        var f = FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild", [KnifeOrder]);
        var census = FirstRoomPlanTests.Buildable(f.Catalog.Stations.Keys);
        foreach (var def in gated) census[def] = new BenchDefinition { Name = def, Available = false, Research = ["Smithing"] };
        return Prepared(f with { BenchDefinitions = census });
    }

    [Fact]
    public void AResearchGatedBenchIsNeverABuildAlternativeAndAnAvailableOneStillIs()
    {
        Assert.Equal(["host:baseline", "host:MakeKnife/CraftingSpot", "host:MakeKnife/Smithy", "host:MakeKnife/CraftingSpot>Smithy", "host:MakeKnife/Smithy>CraftingSpot"],
            Gated().HostAlternatives().Alternatives.Select(a => a.Id));
        var gated = Gated(Smithy).HostAlternatives();
        Assert.Equal(["host:baseline", "host:MakeKnife/CraftingSpot"], gated.Alternatives.Select(a => a.Id));
        Assert.Empty(gated.Omitted);

        // A recipe's own gate shuts every host until its project is finished.
        var f = Gated();
        var recipes = new Dictionary<string, RecipeFlow>(f.Recipes) { [Knife] = f.Recipes[Knife] with { Research = ["Smithing"] } };
        Assert.Equal(["host:baseline"], (f with { Recipes = recipes }).HostAlternatives().Alternatives.Select(a => a.Id));
        Assert.Equal(5, (f with { Recipes = recipes, FinishedResearch = new HashSet<string> { "Smithing" } }).HostAlternatives().Alternatives.Count);

        // A host the census never described is an unread fact, never taken as buildable.
        var unread = f with { BenchDefinitions = new Dictionary<string, BenchDefinition>(f.BenchDefinitions.Where(d => d.Key != Smithy)) };
        Assert.Equal("bench definition:" + Smithy, Assert.Throws<RequiredFactException>(() => unread.HostAlternatives()).Fact);
    }

    [Fact]
    public void ALoadWhoseOnlyHostsAreGatedIsNoComparisonLoadAndTheResearchRungNamesThem()
    {
        var hosts = Gated(Spot, Smithy).HostAlternatives();
        Assert.Equal(["host:baseline"], hosts.Alternatives.Select(a => a.Id));
        Assert.Empty(hosts.Baseline.Loads);
        Assert.Null(HostOf(StationGraph.BuildStationGraph(Gated(Spot, Smithy).GraphInput())));

        var knife = new Resource("Knife");
        RecipeHost[] recipes = [new() { Definition = Knife, Products = [knife], Available = true, Benches = [Spot, Smithy], Ingredients = [], Research = [] }];
        BenchDefinition[] census = [new() { Name = Spot, Available = false, Research = ["Smithing"] }, new() { Name = Smithy, Available = false, Research = ["Smithing"] }];
        Assert.False(WorkRules.WorkshopHostable(knife, recipes, census));
        var rung = WorkRules.WorkshopResearchRung(knife, recipes, census);
        Assert.NotNull(rung);
        Assert.Equal((Spot, Knife), (rung.Bench, rung.Recipe));
        Assert.Equal(["Smithing"], rung.Research);
    }

    [Fact]
    public void AHeldHostStaysRetainedWhateverItsResearchGate()
    {
        var prepared = Gated(Spot, Smithy);
        var held = new LayoutPlan { Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Workshop, Production = [new(Smithy, Knife, 1)] }] };
        var hosts = LayoutHostRules.Of(prepared.Catalog, prepared.LedgerOrders(), held, new HostCensus(prepared.BenchDefinitions, prepared.Recipes, prepared.FinishedResearch));
        Assert.Equal(new HostChoice.Retained(Smithy), Assert.Single(hosts.Baseline.Loads).Host);
        Assert.Equal(["host:baseline", "host:MakeKnife/deferred"], hosts.Alternatives.Select(a => a.Id));
    }

    private const string Chain = Spot + ">" + Smithy, Pair = Smithy + "+" + Spot;

    /// <summary>
    /// Task 3.2: spot-now/table-later is one dependent alternative, priced whole by the same ledger as either host alone. The
    /// standing spot bridges the knives drawn before the smithy's room serves (day 3), then the smithy takes them at its
    /// speed. A draw the smithy's day cannot absorb makes the chain worth more than either host; with nothing drawn the
    /// bridge only spends the spot's slow labor on knives the smithy makes later anyway; a costly smithy leaves the spot alone.
    /// </summary>
    [Theory]
    [InlineData(20d, 1L, Chain)]
    [InlineData(0d, 1L, Smithy)]
    [InlineData(20d, 40L, Spot)]
    public async Task ASpotNowTableLaterChainIsComparedWholeAgainstEitherHostAlone(double drawn, long smithyBuild, string winner)
    {
        var site = new Site(Drawn: drawn, SmithyBuild: smithyBuild, SpotStands: true);
        var prepared = Prepared();
        var result = await Compare(Inputs(prepared, prepared.HostAlternatives()), host => { var context = Context(host, site); return (context, Outcome(context, site)); }, null, Knife);

        Assert.Equal(winner, HostOf(result.Graph));
        Assert.Equal(winner == Chain ? LayoutRegroupRules.RegroupApplied : LayoutRegroupRules.RegroupKept, HostDecision(result, Chain).Verdict);
    }

    /// <summary>
    /// Task 3.2: the chain's later step depends on the earlier one. A bridge that never serves within the forecast (a planned
    /// spot whose site does not finish) bridges nothing, so the chain is the smithy alone; one finishing on day one bridges
    /// the two days to the smithy's service.
    /// </summary>
    [Fact]
    public void AChainsBridgeEarnsOnlyFromItsOwnReadinessUntilTheReplacementServes()
    {
        var site = new Site(Drawn: 20);
        Assert.Equal(Ledger(Smithy, site).Investment, Ledger(Chain, site).Investment, 9);
        Assert.True(Ledger(Chain, site with { SpotReady = Day }).Investment < Ledger(Chain, site with { SpotReady = 2 * Day }).Investment);
        Assert.True(Ledger(Chain, site with { SpotReady = 2 * Day }).Investment < Ledger(Smithy, site).Investment);
        // A bridge with no planned row of its own is an unread fact, never a free bridge.
        var unread = Context(Chain, site with { SpotReady = Day });
        unread = unread with { Closures = [unread.Closures[0] with { Prediction = unread.Closures[0].Prediction with
            { WorkBenches = [.. unread.Closures[0].Prediction.WorkBenches.Where(b => b.Def != Spot)] } }] };
        Assert.Equal("workshop planned benches:" + Spot, Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Outcome(unread, site with { SpotReady = Day }), unread, Day)).Fact);
    }

    /// <summary>
    /// Task 3.2: a kept spare shares the replacement's demand instead of adding a second copy of it. With nothing drawn the
    /// one knife is split by planned runs and the pair earns no more than the better host; with a draw past the smithy's day
    /// the spare's runs are useful and retention wins.
    /// </summary>
    [Fact]
    public void AKeptSpareSharesTheLoadsThroughputOnceAndWinsOnlyWhenTheLoadNeedsTwoHosts()
    {
        var idle = new Site(SpotStands: true, ShareRuns: 1);
        Assert.True(Ledger(Pair, idle).Investment >= Math.Min(Ledger(Smithy, idle).Investment, Ledger(Spot, idle).Investment) - 1e-9);
        var busy = idle with { Drawn = 20 };
        Assert.True(Ledger(Pair, busy).Investment < Ledger(Smithy, busy).Investment);
    }

    /// <summary>
    /// Task 3.2: once the replacement serves (standing, usable, reachable, offering the recipe), the baseline is the
    /// replacement alone and keeping the standing spot beside it is the one alternative fate; a replacement that does not
    /// serve yet offers no fate and a held chain stays a chain. The kept spot is a node of its own, so the layout prices
    /// its footprint: where space is scarce, retention pays for the room it holds.
    /// </summary>
    [Fact]
    public void AServingReplacementComparesRemovingAgainstKeepingTheSpare()
    {
        var prepared = Prepared();
        HostAlternatives Of(LayoutPlan held, params HostBench[] benches) => LayoutHostRules.Of(prepared.Catalog, prepared.LedgerOrders(), held,
            new HostCensus(prepared.BenchDefinitions, prepared.Recipes, prepared.FinishedResearch) { Benches = benches });
        var chain = new LayoutPlan { Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Workshop, Production = [new(Smithy, Knife, 1), new(Spot, Knife, 1) { Step = HostStep.Bridge }] }] };
        HostBench spot = new("spot", Spot, true, [Knife], []), smithy = new("smithy", Smithy, true, [Knife], []);

        Assert.Equal(new HostChoice.Chain(Spot, Smithy), Assert.Single(Of(chain, spot).Baseline.Loads).Host);
        Assert.DoesNotContain(Of(chain, spot).Alternatives, a => a.Id.Contains('+', StringComparison.Ordinal));
        Assert.Equal(new HostChoice.Chain(Spot, Smithy), Assert.Single(Of(chain, spot, smithy with { Serves = false }).Baseline.Loads).Host);

        var serving = Of(chain, spot, smithy);
        Assert.Equal(new HostChoice.Retained(Smithy), Assert.Single(serving.Baseline.Loads).Host);
        var kept = Assert.Single(serving.Alternatives, a => a.Id == "host:" + Knife + "/" + Pair);
        var node = Assert.Single(LayoutRegroupRules.Seed(LayoutStationPlanRules.RegroupInputOf(prepared, kept)).Graph.Nodes, n => n.Def == Spot);
        Assert.Equal(HostStep.Share, Assert.Single(node.Production).Step);
        Assert.DoesNotContain(LayoutRegroupRules.Seed(LayoutStationPlanRules.RegroupInputOf(prepared, serving.Alternatives[0])).Graph.Nodes, n => n.Def == Spot);

        // A held pair compares removing the spare.
        var pair = new LayoutPlan { Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Workshop, Production = [new(Smithy, Knife, 1), new(Spot, Knife, 0) { Step = HostStep.Share }] }] };
        var held = Of(pair, spot, smithy);
        Assert.Equal(new HostChoice.Pair(Smithy, Spot), Assert.Single(held.Baseline.Loads).Host);
        Assert.Contains(held.Alternatives, a => a.Id == "host:" + Knife + "/" + Smithy);
    }
}
