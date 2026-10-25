using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastSettlementMissionTests
{
    private const long Day = GameTime.TicksPerDay;
    internal static Settlement Destination(string kind = "exotic") => new()
    {
        Id = "settlement", Tile = 2, Relation = SettlementRelation.Neutral, CanTrade = true, TraderKind = kind,
    };
    internal static TradePackEstimate.Packed Pack(string kind = "exotic", bool fresh = true, long restock = 30 * Day,
        EquatableArray<SettlementStockItem> held = default) => new(20, 75, 10, 10, new PackRoute.Reachable(Day), new PackRoute.Reachable(2 * Day))
    {
        SettlementTrade = new("settlement", kind, fresh, restock, .98, held),
    };
    private static ForecastSettlementMission Option(TradePackEstimate? pack = null, long silver = 100) =>
        Assert.IsType<ForecastSettlementMission>(ForecastSettlementMissions.Option(ForecastTradeRequestTests.Catalog(new("arm"), 100), Destination(), pack ?? Pack(), ["pawn"], silver));

    [Fact]
    public void ExactPackSettlementStockRoundTripsThroughTheNativeContract()
    {
        TradePackEstimate pack = Pack(fresh: false, held: [new("arm", 2, 60)]);
        var json = RimGovernor.Brain.GameOrders.OrdersJson.Serialize(pack);
        Assert.Equal(pack, RimGovernor.Brain.GameOrders.OrdersJson.Deserialize<TradePackEstimate>(json));
        var factsJson = RimGovernor.Brain.GameFacts.FactsJson.Serialize(pack);
        Assert.Equal(pack, RimGovernor.Brain.GameFacts.FactsJson.Deserialize<TradePackEstimate>(factsJson));
    }

    [Fact]
    public void AnUnseenSettlementDrawsOnceAndGoodsArriveOnlyAfterBothAsymmetricLegs()
    {
        var option = Option();
        var source = Assert.Single(option.Sources);
        Assert.Equal(3, option.Days);
        Assert.Equal(3, source.From);
        Assert.Equal(1, source.NotArrived(1, 2.999));
        Assert.Equal(0, source.NotArrived(1, 3));
        Assert.Equal(1, source.NotArrived(2, 60));
        Assert.Equal(.98, source.PriceFactor);
        Assert.Equal(option.Id, option.Commitment().TradeMission);
        Assert.Empty(option.Commitment().StockChanges);
        Assert.Equal("trade/mission", option.Commitment().ExclusiveOutcome);
        var stock = new StockArrival { Missions = [option] };
        Assert.Equal(10, stock.ExpectedWait([new([new("arm")], 1)], 10));
        var outcome = new ForecastCoupledOutcome(new([], 0, 0), true, new Dictionary<string, ForecastNeedsResult>(), []);
        var context = ForecastValueTests.Context(ForecastValueTests.Pawn()) with { Stock = stock };
        Assert.Same(context, ForecastTradeRequests.Context(outcome, [], context, Day));
        var selected = ForecastTradeRequests.Context(outcome, [option.Commitment()], context, Day);
        Assert.Equal(10, selected.Stock.ExpectedWait([new([new("arm")], 1)], 10)); // No chosen StockWait authorizes a purchase.
        Assert.Empty(context.Stock.Trade);
    }

    [Fact]
    public void UsefulHeldStockUsesItsLivePriceAndDepletedGoodsStayAbsentUntilNativeRestock()
    {
        var held = Pack(fresh: false, held: [new("arm", 2, 60), new("junk", 1, 1)]);
        var option = Option(held, 60);
        var arm = Assert.Single(option.Sources, s => s.Resource == new Resource("arm"));
        Assert.Equal(1, arm.PriceFactor);
        Assert.Equal(0, arm.NotArrived(1, 3));
        Assert.Equal(1, arm.NotArrived(2, 3));
        var depleted = Option(held with { SettlementTrade = held.SettlementTrade! with { CurrentStock = [new("junk", 1, 1)] } });
        Assert.DoesNotContain(depleted.Sources, s => s.Resource == new Resource("arm"));
        var restocked = Option(held with { SettlementTrade = held.SettlementTrade! with { RestockInTicks = Day } });
        Assert.Equal(0, Assert.Single(restocked.Sources).NotArrived(1, 3));
        var before = Option(held with { SettlementTrade = held.SettlementTrade! with { RestockInTicks = Day + 1, CurrentStock = [new("junk", 1, 1)] } });
        Assert.DoesNotContain(before.Sources, s => s.Resource == new Resource("arm"));
    }

    [Fact]
    public void UnreachableUnsafeHostileAndUnreadTripsNeverPromiseStock()
    {
        Assert.Null(ForecastSettlementMissions.Option(ForecastTradeRequestTests.Catalog(new("arm")), Destination(), Pack() with { Home = new PackRoute.Unreachable() }, ["pawn"], 100));
        Assert.Null(ForecastSettlementMissions.Option(ForecastTradeRequestTests.Catalog(new("arm")), Destination(), Pack() with { FoodDays = 2 }, ["pawn"], 100));
        Assert.Null(ForecastSettlementMissions.Option(ForecastTradeRequestTests.Catalog(new("arm")), Destination() with { Relation = SettlementRelation.Hostile }, Pack(), ["pawn"], 100));
        Assert.Throws<RequiredFactException>(() => Option(Pack() with { SettlementTrade = null }));
        Assert.Throws<RequiredFactException>(() => Option(Pack() with { SettlementTrade = Pack().SettlementTrade! with { TraderKind = "missing" } }));
    }

    [Fact]
    public void CrewTimeCostsTheWorkThisOutcomeLeavesShortAndStopsAtTheInvestmentHorizon()
    {
        var catalog = Game.WorkSpeedFactsTests.Catalog();
        var facts = ForecastCoupledTests.ConstructionReview(1, 100);
        var needs = facts.ForecastObserved.Needs!;
        facts = facts with
        {
            WorkSpeeds = catalog.WorkSpeedFacts(),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = needs with { Pawns = [needs.Pawns[0] with { Upkeep = NeedsFixtures.Upkeep with { Speeds = [new("ConstructionSpeed", 1)] } }] },
                Coupled = facts.ForecastObserved.Coupled! with
                {
                    Construction = facts.ForecastObserved.Coupled!.Construction with
                    {
                        Options = [.. facts.ForecastObserved.Coupled!.Construction.Options.Select(o => o with { WorkGiver = "ConstructFinishFrames", Finish = new ConstructionFinish.Finishes(3 * Day, (o.Finish as ConstructionFinish.Finishes)?.SkillWork) })],
                    },
                },
            },
        };
        var candidates = Assert.IsType<ForecastCoupledCandidates>(ForecastCoupledCandidates.GenerateCoupledActivities(facts));
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(ForecastCoupled.CoupledForecastRequestOf(new(100), 1, facts with { CoupledAlternatives = candidates }, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []), candidates);
        var model = ColonyModelFixtures.Devilstrand();
        model = model with { Pawns = EquatableArray.From(model.Pawns.Select(p => p with { Pawn = "pawn" })) };
        var context = candidates.Context with
        {
            InvestmentHorizonDays = 10, ModelBasis = new(model, new() { MedicineTendsPerDay = 0 }),
            Joiners = new() { Roots = [.. candidates.Alternatives, .. candidates.Incumbents], Work = facts.WorkSpeeds, Food = facts.FoodWork },
        };
        var roots = candidates.Alternatives.Where(c => selected.Selection.Selected.Contains(c.Id)).ToArray();
        var mission = Option();
        var cost = ForecastSettlementMissions.LostWork(mission, selected.Selected, roots, context, selected.Horizon);
        Assert.True(cost > 0);
        Assert.Equal(2 * cost, ForecastSettlementMissions.LostWork(mission with { Days = 6 }, selected.Selected, roots, context, selected.Horizon), 10);
        Assert.Equal(ForecastSettlementMissions.LostWork(mission with { Days = 10 }, selected.Selected, roots, context, selected.Horizon),
            ForecastSettlementMissions.LostWork(mission with { Days = 100 }, selected.Selected, roots, context, selected.Horizon));
        Assert.Equal(0, ForecastSettlementMissions.LostWork(mission, selected.Selected, roots, context with { Joiners = context.Joiners with { Roots = [] } }, selected.Horizon));
    }
}
