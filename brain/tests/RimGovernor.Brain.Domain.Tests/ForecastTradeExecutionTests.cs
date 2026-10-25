using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastTradeExecutionTests
{
    private static readonly Resource Medicine = new("Medicine");
    private static readonly Resource Arm = new("Arm");
    private static ForecastTradeRequest Request() => Assert.Single(ForecastTradeRequests.Options(
        ForecastTradeRequestTests.Catalog(Arm), new TradeVisitFacts
        { Requests = [ForecastTradeRequestTests.Request() with { ConsoleId = "console", ConsoleCell = new(1, 1) }] }, 800));
    private static ForecastActivityAllocation Allocation(string id, double depart, double end) =>
        new(id, "pawn", depart, depart, depart, end, 0, 0, end - depart, end - depart, 0);
    private static ForecastActivityOutcomeComparison Selection(string root, params ForecastActivityAllocation[] allocations) =>
        new([root], new(allocations, allocations.Select(a => a.End).DefaultIfEmpty(0).Max(), 0), [], 0, false);

    [Fact]
    public void EarlierWorkRunsBeforeTheExactRequestAndLaterWorkWaitsForFreshReview()
    {
        var request = Request();
        var stock = new StockArrival { Requests = [request] };
        var work = Assert.Single(ForecastTradeExecution.Work(stock));
        var context = ForecastValueTests.Context(ForecastValueTests.Pawn()) with
        { Stock = stock, TradeWork = [work], Activities = new() { Deadline = 100, Activities = [new("after", "start", "start", [work.Task])] } };
        var selected = Selection(request.Id, Allocation("eat", 0, 10), Allocation(work.Task, 10, 20), Allocation("sleep", 20, 30),
            new("after", "other", 30, 30, 30, 40, 0, 0, 10, 10, 0));
        Assert.Empty(ForecastTradeExecution.Ready(selected, context));
        Assert.Equal("eat", Assert.Single(RimGovernor.Brain.Policy.Methods.Binding.CoordinatedActivityBinding.BeforeExternalWork(selected.Plan!, context).Allocations).Activity);
        var fresh = Selection(request.Id, Allocation(work.Task, 0, 10), Allocation("sleep", 10, 20));
        var ready = Assert.Single(ForecastTradeExecution.Ready(fresh, context));
        var action = Assert.IsType<CommsTradeRequest>(ready.Action);
        Assert.Equal(("console", "pawn", "ally", "exotic"), (action.Console, action.Negotiator, action.Faction, action.TraderKind));
        Assert.Empty(RimGovernor.Brain.Policy.Methods.Binding.CoordinatedActivityBinding.BeforeExternalWork(fresh.Plan!, context).Allocations);
    }

    [Fact]
    public void TripReservesTheCrewAfterEarlierWorkWithoutChargingAwakeLabor()
    {
        var compared = ForecastActivities.CompareActivityPlans(new()
        {
            Deadline = 100,
            Activities = [new("eat", "table", "table", []), new("depart", "start", "start", ["eat"])],
            Options = [new("eat", "pawn", 10, 0), new("depart", "pawn", 0, 0) { AbsenceTicks = 3 * GameTime.TicksPerDay, FromCurrentLocation = true }],
            Pawns = [new("pawn", "table", new ForecastCapacityWindows.Read([new(0, 100)]), 1)],
        });
        var plan = Assert.Single(compared.Alternatives);
        var trip = Assert.Single(plan.Allocations, a => a.Activity == "depart");
        Assert.Equal((10d, 100d, 0d, 0d), (trip.Depart, trip.End, trip.WorkTicks, trip.DisplacedLaborTicks));
    }

    [Fact]
    public void ForecastAndBothExistingBuyersUseOneMedicineFirstTodaysBudget()
    {
        StockNeed[] waits = [new([Arm], 1), new([Medicine], 2)];
        var sources = new[] { new TradeSource(Arm, 2, 0) { Once = true, Silver = 800, PriceFactor = 1, Kinds = [new(1, ForecastTradeRequestTests.Offer(700))] },
            new TradeSource(Medicine, 2, 0) { Once = true, Silver = 800, PriceFactor = 1, Kinds = [new(1, ForecastTradeRequestTests.Offer(100, 2))] } };
        var forecast = ForecastTradeRequests.Authorize(new() { Trade = EquatableArray.From(sources), Medicines = [Medicine] }, waits);
        Assert.Equal(new StockPurchase("Medicine", Medicine, 2), Assert.Single(forecast));
        var mission = new TradeMission { SilverBudget = 800, Demand = [new("Arm", 1), new("Medicine", 2)] };
        var purchased = TradePolicyRules.PlanTradeMissionPurchases(new()
        {
            Mission = mission, Medicines = [Medicine], Silver = 800, Capacity = 100,
            Rows = [new() { LineID = "a", DefName = "Arm", TraderCount = 1, BuyPrice = 700, WillTrade = true },
                new() { LineID = "m", DefName = "Medicine", TraderCount = 2, BuyPrice = 100, WillTrade = true }],
        });
        Assert.Equal(new TradeLine("m", 2), Assert.Single(purchased.Lines));
        var selected = TradePolicyRules.SelectTrade(new([], 200), new()
        {
            Complete = true, SilverKnown = true, ColonySilver = 1000, TraderSilver = 1000, MaxSilverSpend = 800,
            StockNeeds = EquatableArray.From(waits), StockMedicines = [Medicine],
            Rows = [new() { LineID = "s", DefName = "Silver", Currency = true, ColonyCount = 1000, TraderCount = 1000 },
                new() { LineID = "a", DefName = "Arm", TraderCount = 1, BuyPrice = 700, TraderWillTrade = true },
                new() { LineID = "m", DefName = "Medicine", TraderCount = 2, BuyPrice = 100, TraderWillTrade = true }],
        });
        Assert.False(selected.Refused);
        Assert.Equal(new TradeSelectionLine("m", 2), Assert.Single(selected.Selected));
        Assert.Equal(200, selected.SilverReserve);
    }

    [Fact]
    public void ExternalActionsSharePawnClaimsAndReleaseThemOnObservedCompletion()
    {
        var request = Assert.IsType<CommsTradeRequest>(Assert.Single(ForecastTradeExecution.Work(new() { Requests = [Request()] })).Action);
        Assert.True(CaravanDeparture.TryCreate([new("pawn"), new("crew")], [new("Silver", 800)], 2, out var departure));
        DomainAction[] actions = [DomainAction.NewCommsTradeRequestAction(new("request"), request),
            DomainAction.NewCaravanDepartureAction(new("depart"), departure!),
            DomainAction.NewTradeAction(new("open"), Trade.NewTradeOpen("trader", new("pawn"), false).WithParticipant(new(TradeParticipantKind.Map, "trader")))];
        foreach (var action in actions)
        {
            var pending = new WorkAction(new("plan"), action, new() { Action = action.Id, Stage = Stage.Pending });
            var claims = MethodClaimsRules.MethodClaimsOf(new(), [pending]);
            Assert.Contains("pawn", claims.Pawns);
            Assert.Equal("pawn/pawn", MethodClaimsRules.MethodClaimOverlap(claims, new(["pawn"], [], [])));
            Assert.Empty(MethodClaimsRules.MethodClaimsOf(new(), [pending with { Progress = pending.Progress with { Stage = Stage.Completed } }]).Pawns);
        }
    }

    [Fact]
    public void ASelectedMissionUsesTheExactPreviewAndImmediatelyLeavesPlanned()
    {
        Assert.True(CaravanDeparture.TryCreate([new("pawn")], [new("Silver", 800)], 2, out var departure));
        var option = new ForecastSettlementMission("settlement", ["pawn"], 3,
            [new(Arm, 3, 0) { Once = true, Silver = 800, Kinds = [new(1, ForecastTradeRequestTests.Offer(100))] }])
            { Departure = departure, HomeTile = 1 };
        var world = new GenerationSnapshot(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
        var mission = ForecastTradeExecution.Mission(option, world, 800, [new([Arm], 1)], []);
        Assert.Equal(TradeMissionPhase.Departing, mission.Phase);
        Assert.Equal(departure, mission.Departure());
        Assert.Equal(new CargoItem("Arm", 1), Assert.Single(mission.Demand));
        Assert.Equal(1, mission.HomeTile);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FreshColonyGeneratedExternalRootSurvivesTheProductionForecastAndClaimAdmission(int channel)
    {
        var settlement = channel == 1;
        var present = channel >= 2;
        var ct = TestContext.Current.CancellationToken;
        var request = Request();
        Assert.True(CaravanDeparture.TryCreate([new("pawn")], [new("Silver", 800)], 2, out var departure));
        var option = Assert.IsType<ForecastSettlementMission>(ForecastSettlementMissions.Option(
            ForecastTradeRequestTests.Catalog(Arm), ForecastSettlementMissionTests.Destination(),
            ForecastSettlementMissionTests.Pack(), ["pawn"], 800)) with { Departure = departure, HomeTile = 1 };
        var census = new TradersSnapshot
        {
            Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(),
                Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 },
            Negotiators = [new("pawn")],
            Traders = [new() { Id = "actual", Orbital = channel == 3, Kind = "exotic", FactionId = "ally",
                CanTrade = true, Travelling = false, GoodsStacks = 1,
                Visit = new(new("pawn"), request.Native.ConsoleCell, 40000, [new(Arm.Value, 1, 100)]) { Silver = 1000 } }],
        };
        var stock = new StockArrival { Requests = settlement || present ? [] : [request], Missions = settlement ? [option] : [],
            Visits = present ? ForecastTradeVisits.Options(RimGovernor.Brain.Bridge.FrameReads.FrameTraders(census), 1) : [] };
        var facts = ForecastCoupledSelectionTests.Review(level: 1, fall: 0, sourceCount: 0, horizon: GameTime.TicksPerDay);
        facts = facts with { StockArrival = stock, ForecastObserved = facts.ForecastObserved with
        {
            Coupled = facts.ForecastObserved.Coupled! with { Routes =
                [new("pawn", default, request.Native.ConsoleCell, new RouteTicks.Reachable(0)) { FromStart = true }] },
        } };
        var candidates = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts));
        var root = Assert.Single(candidates.Alternatives, c => present ? c.TradeVisit.Length != 0 : settlement ? c.TradeMission.Length != 0 : c.TradeRequest.Length != 0);
        var loss = RaidPartLossTests.ArmLoss();
        var pawn = ForecastValueTests.HarmPawn() with { PartLoss = loss with
        { Replacements = EquatableArray.From(loss.Replacements.Select(r => r with { PartThing = Arm.Value, PartOnMap = false, MedicineOnMap = true })) } };
        var model = ColonyModelFixtures.Devilstrand();
        model = model with { Pawns = EquatableArray.From(model.Pawns.Select(p => p with { Pawn = "pawn" })) };
        candidates = candidates with { Context = candidates.Context with
        {
            Needs = candidates.Context.Needs with { Pawns = [pawn] },
            Harm = ForecastValueTests.RaidDay(), CapacityPenalties = ForecastValueTests.HarmTables(), InvestmentHorizonDays = 10,
            Outfits = new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = ForecastValueTests.Cloth },
            ModelBasis = new(model, new() { MedicineTendsPerDay = 0 }),
            Joiners = new() { Roots = candidates.Alternatives, Work = facts.WorkSpeeds, Food = facts.FoodWork },
        } };
        facts = facts with { CoupledAlternatives = candidates };
        var compared = ForecastCoupledSelection.CompareActivityOutcomes(ForecastCoupled.CoupledForecastRequestOf(new(100), 1, facts,
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []), candidates);
        Assert.Contains(root.Id, compared.Selection.Selected);
        Assert.Contains(ForecastTradeRequests.SelectedWaits(compared, candidates), w => w.AnyOf.Contains(Arm));
        var outcome = compared.Selected;
        var selected = compared.Selection;
        var work = Assert.Single(ForecastTradeExecution.Ready(selected, candidates.Context));
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(new(selected, outcome, outcome,
            ForecastCoupledSelection.CoupledConsequences(outcome), ForecastCoupledSelection.CoupledConsequences(outcome), [], GameTime.TicksPerDay, false),
            candidates, 1, "method", 100));
        var world = new GenerationSnapshot(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
        using var journal = RimGovernor.Brain.Store.Store.Open(Path.Combine(Path.GetTempPath(), "rg-selected-trade-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.Review(new() { Current = world, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("pawn") }, ct);
        var id = new PlanId("selected-trade");
        if (settlement)
        {
            var mission = ForecastTradeExecution.Mission(option, world, 800, [new([Arm], 1)], []);
            var record = journal.LoadTradeMissions(new RimGovernor.Brain.Store.World(world.Colony, world.Load, world.Map), ct);
            var admitted = journal.CommitTradeMissionMethod(EconomyFamily.TradeMission, world, new("selected"), "", new(id, new(1),
                [DomainAction.NewCaravanDepartureAction(new("departure"), mission.Departure())]), record.With(id.Value, mission), ct);
            Assert.Equal(RimGovernor.Brain.Store.ClaimOutcome.Admitted, admitted.Outcome);
            Assert.Equal(TradeMissionPhase.Departing, Assert.Single(journal.LoadTradeMissions(record.World, ct).Missions).Mission.Phase);
        }
        else if (present)
        {
            var admitted = journal.CommitClaimedMethod(EconomyFamily.Caravan, world, new("selected"), "", new(id, new(1),
                [DomainAction.NewTradeAction(new("open"), Assert.IsType<Trade>(work.Action))]), ct);
            Assert.Equal(RimGovernor.Brain.Store.ClaimOutcome.Admitted, admitted.Outcome);
        }
        else
        {
            var admitted = journal.CommitClaimedMethod(EconomyFamily.Caravan, world, new("selected"), "", new(id, new(1),
                [DomainAction.NewCommsTradeRequestAction(new("request"), Assert.IsType<CommsTradeRequest>(work.Action))]), ct);
            Assert.Equal(RimGovernor.Brain.Store.ClaimOutcome.Admitted, admitted.Outcome);
        }
        var overlap = journal.CommitClaimedMethod(AdminFamily.WorkAssignments, world, new("other"), "", new(new("other"), new(1),
            [DomainAction.NewPawnActivitiesAction(new("other-action"), new(1,
                [new("pawn", "assignment", "other", 0, 1, false, [new(PawnActivityKind.Hold, "", "", "", 0, 100)])]))]), ct);
        Assert.Equal(RimGovernor.Brain.Store.ClaimOutcome.Conflict, overlap.Outcome);
        Assert.Equal("pawn/pawn", overlap.Conflict!.Claim);
    }

    [Fact]
    public void ChosenRaidReplacementWaitAuthorizesTheRequestAndCreditsOnlyLaterHarm()
    {
        var request = Request();
        var loss = RaidPartLossTests.ArmLoss();
        var pawn = ForecastValueTests.HarmPawn() with { PartLoss = loss with
        { Replacements = EquatableArray.From(loss.Replacements.Select(r => r with { PartThing = Arm.Value, PartOnMap = false, MedicineOnMap = true })) } };
        var outcome = new ForecastCoupledOutcome(new([], 0, 0), true,
            new Dictionary<string, ForecastNeedsResult> { ["pawn"] = ForecastValueTests.Timeline(pawn) }, []);
        var context = ForecastValueTests.Context(pawn, ForecastValueTests.HarmTables(), ForecastValueTests.RaidDay(),
            new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = ForecastValueTests.Cloth }) with
        { Stock = new() { Requests = [request] }, InvestmentHorizonDays = 10 };
        var root = request.Commitment();
        var selected = ForecastTradeRequests.Context(outcome, [root], context, GameTime.TicksPerDay);
        var waits = TradeStockPurchases.Waits(outcome, selected, [root], 10 * GameTime.TicksPerDay);
        Assert.Contains(waits, w => w.AnyOf.Contains(Arm) && w.Count == 1);
        Assert.Equal(1, Assert.Single(selected.Stock.Trade).Authorized);
        Assert.Equal(2, selected.Stock.ExpectedWait([new([Arm], 1)], 10));
        Assert.Equal(1, selected.Stock.ExpectedWait([new([Arm], 1)], 1));
        var rows = new List<ForecastInvestmentContribution>();
        ForecastTradeRequests.Credit(outcome, [root], selected, GameTime.TicksPerDay, rows, []);
        Assert.True(Assert.Single(rows).Value < 0);
    }
    [Fact]
    public void AnArrivingTraderUsesItsExactCurrentIdentityStockLocationAndNegotiator()
    {
        var trader = new Trader { Id = "actual-arrival", Kind = "exotic", FactionId = "ally", Orbital = false,
            CanTrade = true, Travelling = false, GoodsStacks = 2,
            Visit = new(new("pawn"), new(5, 6), 40000, [new("Arm", 1, 700), new("Medicine", 2, 100)]) { Silver = 1000 } };
        var other = trader with { Id = "same-faction-other-trader", Visit = trader.Visit! with { Stock = [new("unneeded", 1, 1)] } };
        var visits = ForecastTradeVisits.Options([other, trader], 1);
        var visit = Assert.Single(visits, v => v.Native.Id == trader.Id);
        Assert.Same(trader, visit.Native);
        Assert.NotEqual(visits[0].Id, visits[1].Id);
        var work = Assert.Single(ForecastTradeExecution.Work(new() { Visits = [visit] }));
        var action = Assert.IsType<Trade>(work.Action);
        Assert.Equal(trader.Participant(), action.Participant);
        Assert.Equal(trader.Visit!.Negotiator, action.Negotiator);
        Assert.Equal(new Cell(5, 6), visit.Native.Visit!.Location);
        var authorized = ForecastTradeRequests.Authorize(new() { Trade = visit.Sources, Medicines = [Medicine] },
            [new([Arm], 1), new([Medicine], 2)]);
        Assert.Equal(new StockPurchase("Medicine", Medicine, 2), Assert.Single(authorized));
    }
}
