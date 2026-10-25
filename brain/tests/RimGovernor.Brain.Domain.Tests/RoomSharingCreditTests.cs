using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RoomSharingCreditTests
{
    private const double Day = GameTime.TicksPerDay;

    [Fact]
    public async Task SharedRoomSavingsCannotOverrideTheCanonicalDeathFloorAndTheWinnerRetainsItsEvaluatedGeometry()
    {
        const string beds = "Bed/sleep", bench = "ResearchBench/work";
        var station = new StationNode { Id = bench, Def = "ResearchBench", Purpose = StationPurpose.Work, Count = 1, DailyWork = 20000 };
        var cluster = BedStationPriceTests.Cluster(station, 1, BedStationPriceTests.Colony(false), BedStationPriceTests.Mood);
        cluster = cluster with { Graph = new StationGraph
        {
            Nodes = [new StationNode { Id = beds, Def = "Bed", Purpose = StationPurpose.Sleep, Count = 1 }, station],
            Edges = [new StationEdge { A = beds, B = bench, Weight = 1 }],
        } };
        var evaluated = new List<Embedding>();
        var result = await LayoutRegroupRules.RegroupAsync(new RegroupInput
        {
            Cluster = cluster, Embed = BedStationPriceTests.Embed,
            Price = new() { Work = new(8000, LayoutRegroupRules.CellWalkDays(60)) },
        }, (candidates, _) => Task.FromResult<IReadOnlyList<RegroupValuation>>([.. candidates.Select(candidate =>
        {
            evaluated.Add(candidate.Layout.Embedding);
            var shared = candidate.Layout.Clustering.Groups.Any(g => LayoutRegroupRules.HoldsNode(g, beds) && LayoutRegroupRules.HoldsNode(g, bench));
            var vector = new ForecastConsequenceVector([], [new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0)], new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0), 0, 0, [])
            {
                Value = new(new(new LedgerTerm.Priced(shared ? -1000 : 0), []), new()) { Death = new(shared, shared ? 1 : 0) },
            };
            return (RegroupValuation)new RegroupValuation.Priced(candidate.Id, vector, shared ? ["death_risk_floor"] : []);
        })]), TestContext.Current.CancellationToken);

        Assert.InRange(evaluated.Count, 2, 33);
        Assert.Contains(evaluated, geometry => ReferenceEquals(geometry, result.Embedding));
        Assert.DoesNotContain(result.Clustering.Groups, g => LayoutRegroupRules.HoldsNode(g, beds) && LayoutRegroupRules.HoldsNode(g, bench));
        Assert.Contains(result.Decisions, decision => decision.Effects.Contains("death_risk_floor"));
    }

    [Fact]
    public void SavedStationPurposesDispatchGuestCreditsAndPlacementWithoutARoleWhitelist()
    {
        var room = new PlannedRoom { Id = 1, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new Policy.Rectangle(1, 1, 5, 5)) };
        InteriorPiece Piece(string def, StationPurpose purpose, int x) => new()
        {
            Def = def, Row = LayoutGraphRules.NodeId(def, purpose), Size = new(1, 1), Rect = new Policy.Rectangle(x, 2, 1, 1),
        };
        var plan = new LayoutPlan { Rooms = [room], Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>>
        { [1] = [Piece("Bed", StationPurpose.SleepMed, 1), Piece("ResearchBench", StationPurpose.Work, 2), Piece("Shelf", StationPurpose.StoreCold, 3)] } };
        var catalog = FirstLayoutPlanTests.Facts(FirstLayoutPlanTests.Survey("mild"), 1).Catalog;
        var facts = new ReviewFacts { LayoutPlan = plan, Stations = catalog, Sleeping = new() };
        var functions = PlannedRoomCredits.FunctionsOf(facts)[1];
        Assert.Contains(PlannedRole.Bedroom, functions);
        Assert.Contains(PlannedRole.Hospital, functions);
        Assert.Contains(PlannedRole.Lab, functions);
        Assert.Contains(PlannedRole.Freezer, functions);
        Assert.Same(room, BuildingPlanner.PlannedStationRoom(plan, "ResearchBench", CurrentConstruction.Empty));
        var thermal = PlannedRules.PlannedThermalRequestOf([room], [], [], "Cooler", -5, plan);
        Assert.Equal(1, Assert.Single(thermal!.Coolers).Room);
    }

    private static ForecastCoupledContext Rooms(bool shared, int sleepers, double disturbance)
    {
        var pawn = BedroomCreditTests.Pawn(false, 2);
        var housing = BedroomCreditTests.Context(pawn, 2);
        var lab = LaboratoryCreditTests.Context(new(1, 1), .5);
        var sleepingRoom = shared ? 1 : 2;
        var sleep = PlannedRoomPredictionTests.Closed(sleepingRoom.ToString(), "WoodLog", sleepers > 1 ? "Barracks" : "Bedroom", 30, 2, 9, 0, false);
        var research = lab.Closures[0];
        var closure = research with { Prediction = research.Prediction with { Role = sleep.Role, Stage = 2 } };
        return lab with
        {
            Needs = housing.Needs with { Pawns = EquatableArray.From(Enumerable.Range(0, sleepers).Select(i => pawn with { Pawn = i == 0 ? "pawn" : "second" })), ResearchBenches = lab.Needs.ResearchBenches },
            PlannedRoomRoles = shared ? new Dictionary<int, PlannedRole> { [1] = sleepers > 1 ? PlannedRole.Barracks : PlannedRole.Bedroom }
                : new Dictionary<int, PlannedRole> { [1] = PlannedRole.Lab, [2] = sleepers > 1 ? PlannedRole.Barracks : PlannedRole.Bedroom },
            PlannedRoomFunctions = shared ? new Dictionary<int, EquatableArray<PlannedRole>> { [1] = [PlannedRole.Lab, sleepers > 1 ? PlannedRole.Barracks : PlannedRole.Bedroom] } : new Dictionary<int, EquatableArray<PlannedRole>>(),
            Closures = shared ? [closure] : [research, new(2, "WoodLog", [new(4, 4)], [], [], sleep)],
            BedroomOccupants = new Dictionary<int, EquatableArray<string>> { [sleepingRoom] = sleepers > 1 ? ["pawn", "second"] : ["pawn"] },
            PlannedBeds = SleepFixtures.Beds(sleepers, sleepingRoom), Thermal = housing.Thermal,
            ColonistThoughts = new Dictionary<string, EquatableArray<MoodThought>> { ["pawn"] = [], ["second"] = [] },
            RoomSleepDisturbance = new Dictionary<int, double> { [sleepingRoom] = disturbance },
        };
    }

    private static ForecastCoupledOutcome Outcome(ForecastCoupledContext context)
    {
        var shifts = PlannedRoomCredits.BreakShifts(context);
        var needs = context.Needs.Pawns.ToDictionary(p => p.Pawn, p => BedroomCreditTests.Closing(0, 0).Needs["pawn"] with
        {
            Mood = MoodResults.Flat(0, [0, 0, 0], EquatableArray.From((shifts.GetValueOrDefault(p.Pawn) ?? []).Select(s => new ForecastShiftedBreaks(s, [0, 0, 0])))),
        });
        return LaboratoryCreditTests.Outcome() with { Needs = needs, ServedRooms = EquatableArray.From(context.Closures.Select(c => new ForecastRoomService(c.Room, c.Stuff, 0))) };
    }

    private static ForecastInvestmentContribution[] Credits(ForecastCoupledContext context) =>
        [.. ForecastValue.Of(Outcome(context), context, Day).InvestmentContributions.Where(c => c.Key.StartsWith("lab/", StringComparison.Ordinal)
            || c.Key.StartsWith("bedroom/", StringComparison.Ordinal) || c.Key.StartsWith("barracks/", StringComparison.Ordinal))];

    [Fact]
    public async Task ResearchAndTwoBedsStayApartWhenTheirNamedLossesBeatTheSavedShellButADayResearcherCanShare()
    {
        double Loss(int sleepers, double disturbed) => Credits(Rooms(true, sleepers, disturbed)).Sum(c => c.Value) - Credits(Rooms(false, sleepers, 0)).Sum(c => c.Value);
        var loneLoss = Loss(1, 0);
        var twoLoss = Loss(2, 3);
        Assert.True(twoLoss > loneLoss);
        // This native-shell test double puts the saved ring material between the two canonical credit losses.
        var savedShell = (loneLoss + twoLoss) / 2;
        async Task<Regrouping> Search(int sleepers, double disturbed)
        {
            const string beds = "Bed/sleep", bench = "ResearchBench/work";
            var station = new StationNode { Id = bench, Def = "ResearchBench", Purpose = StationPurpose.Work, Count = 1, DailyWork = 20000 };
            var cluster = BedStationPriceTests.Cluster(station, 1, BedStationPriceTests.Colony(disturbed > 0), BedStationPriceTests.Mood);
            cluster = cluster with { Graph = new StationGraph
            {
                Nodes = [new StationNode { Id = beds, Def = "Bed", Purpose = StationPurpose.Sleep, Count = sleepers }, station],
                Edges = [new StationEdge { A = beds, B = bench, Weight = 1 }],
            } };
            var evaluated = new HashSet<string>(StringComparer.Ordinal);
            return await LayoutRegroupRules.RegroupAsync(new RegroupInput
            {
                Cluster = cluster, Embed = BedStationPriceTests.Embed,
                Price = new() { Work = new(8000, LayoutRegroupRules.CellWalkDays(60)) },
            }, (candidates, _) => Task.FromResult<IReadOnlyList<RegroupValuation>>([.. candidates.Select(candidate =>
            {
                Assert.True(evaluated.Add(candidate.Id), "each candidate identity is evaluated once");
                var shared = candidate.Layout.Clustering.Groups.Any(g => LayoutRegroupRules.HoldsNode(g, beds) && LayoutRegroupRules.HoldsNode(g, bench));
                var context = Rooms(shared, sleepers, shared ? disturbed : 0);
                var ledger = ForecastValue.Of(Outcome(context), context, Day);
                var shell = shared ? 0 : savedShell;
                ledger = ledger with { Investment = ledger.Investment + shell,
                    InvestmentContributions = EquatableArray.From(ledger.InvestmentContributions.Append(new("", "", "material/WoodLog", 1, shell))) };
                var vector = new ForecastConsequenceVector([], [new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0)], new ForecastTerm.Assessed(0), new ForecastTerm.Assessed(0), 0, 0, [])
                { Value = new(ledger, context.Value) };
                return (RegroupValuation)new RegroupValuation.Priced(candidate.Id, vector,
                    shared ? disturbed > 0 ? ["lab/1/pawn", "sleep_disturbed", "barracks/1/pawn", "barracks/1/second"] : ["lab/1/pawn"] : []);
            })]));
        }
        var two = await Search(2, 3);
        Assert.DoesNotContain(two.Clustering.Groups, g => LayoutRegroupRules.HoldsNode(g, "Bed/sleep") && LayoutRegroupRules.HoldsNode(g, "ResearchBench/work"));
        Assert.Contains(two.Decisions, d => d.Verdict == LayoutRegroupRules.RegroupKept && d.Effects.Contains("lab/1/pawn") && d.Effects.Contains("sleep_disturbed"));
        var lone = await Search(1, 0);
        Assert.Contains(lone.Clustering.Groups, g => LayoutRegroupRules.HoldsNode(g, "Bed/sleep") && LayoutRegroupRules.HoldsNode(g, "ResearchBench/work"));
    }

    [Fact]
    public void ResearchAndBothSleepersPayTheirActualMergedRoomLosses()
    {
        var separate = Credits(Rooms(false, 2, 0));
        var shared = Credits(Rooms(true, 2, 3));
        Assert.Single(shared, c => c.Key == "lab/1/pawn");
        Assert.Equal(2, shared.Count(c => c.Key.StartsWith("barracks/", StringComparison.Ordinal)));
        Assert.True(shared.Single(c => c.Key == "lab/1/pawn").Value > separate.Single(c => c.Key == "lab/1/pawn").Value);
        Assert.True(shared.Where(c => c.Key.StartsWith("barracks/", StringComparison.Ordinal)).Sum(c => c.Value)
            > separate.Where(c => c.Key.StartsWith("barracks/", StringComparison.Ordinal)).Sum(c => c.Value));
    }

    [Fact]
    public void ADaysResearcherAndLoneSleeperPayNoDisturbedSleepCreditTwice()
    {
        var quiet = Credits(Rooms(true, 1, 0));
        var disturbed = Credits(Rooms(true, 1, 3));
        Assert.Single(quiet, c => c.Key.StartsWith("bedroom/", StringComparison.Ordinal));
        Assert.Equal(quiet.Single(c => c.Key.StartsWith("lab/", StringComparison.Ordinal)).Value,
            disturbed.Single(c => c.Key.StartsWith("lab/", StringComparison.Ordinal)).Value);
        Assert.True(disturbed.Sum(c => c.Value) > quiet.Sum(c => c.Value));
    }

    [Fact]
    public void FilthPricesBothNativeStagesAtTheExistingDutyInsteadOfTheMeanStage()
    {
        var clean = Rooms(true, 2, 0);
        var closure = clean.Closures[0];
        var dirty = clean with { Closures = [closure with { Prediction = closure.Prediction with { Stage = 0 } }] };
        var duty = clean with { Closures = [closure with { Prediction = closure.Prediction with { Filth = new(0, 0, .25) } }] };
        var cleanRows = Credits(clean);
        var dirtyRows = Credits(dirty);
        var dutyRows = Credits(duty);
        foreach (var row in dutyRows)
            Assert.Equal(.75 * cleanRows.Single(c => c.Key == row.Key).Value + .25 * dirtyRows.Single(c => c.Key == row.Key).Value, row.Value, 12);
    }
}
