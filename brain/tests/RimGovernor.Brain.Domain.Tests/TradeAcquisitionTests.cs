using RimGovernor.Brain.Policy;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The trade acquisition and settlement mission rules read whole (#3785): a native pack is Packed or Unpackable
/// with each leg Reachable or Unreachable, an option's trip facts are plain, and an unread input never reaches
/// the rules (the caller raises or plans nothing).
/// </summary>
public sealed class TradeAcquisitionTests
{
    private static readonly O.TradePackEstimate.Packed Safe = new(80, 200, 10, 12, new O.PackRoute.Reachable(GameTime.TicksPerDay * 2), new O.PackRoute.Reachable(GameTime.TicksPerDay * 3));

    private static readonly ResourceKey Steel = new() { Def = new Resource("Steel") };

    private static TradeAcquisitionOption Settlement(double lead = 5) => new()
    {
        ID = "food:settlement:s1",
        Kind = TradeAcquisitionKind.Settlement,
        Participant = new TradeParticipant(TradeParticipantKind.Settlement, "s1"),
        Compatible = [Steel],
        Eligible = true,
        LeadDays = lead,
        LaborTicks = 100,
        OutboundDays = 2,
        ReturnDays = 3,
        FoodDays = 10,
        RotDays = 12,
    };

    private static TradeAcquisitionOption Request(TradeAcquisitionKind kind) => new()
    {
        ID = "f1/request_caravan/bulk",
        Kind = kind,
        Faction = "f1",
        TraderKind = "bulk",
        Console = "console1",
        Negotiator = "pawn1",
        Compatible = [Steel],
        Eligible = true,
        LeadDays = 1,
        RelationAfterPayment = "Ally",
        OrbitalAvailable = true,
    };

    private static readonly SupplyDemandResult SteelGap = new(new SupplyDemand { Good = Steel, Units = 50, HorizonDays = 30 }, 0, 50);

    [Fact]
    public void APackIsSafeOnlyWhenPackedReachableAndProvisioned()
    {
        Assert.True(TradePolicyRules.SafeTradeMissionPack(Safe));
        Assert.Equal((5.0, true), TradePolicyRules.TradeMissionRouteDays(Safe));
        Assert.False(TradePolicyRules.SafeTradeMissionPack(new O.TradePackEstimate.Unpackable()));
        var homeless = Safe with { Home = new O.PackRoute.Unreachable() };
        Assert.False(TradePolicyRules.SafeTradeMissionPack(homeless));
        Assert.Equal((0.0, false), TradePolicyRules.TradeMissionRouteDays(homeless));
        Assert.False(TradePolicyRules.SafeTradeMissionPack(Safe with { MassUsage = 201 }));
        Assert.False(TradePolicyRules.SafeTradeMissionPack(Safe with { FoodRotDays = 4 }));
    }

    [Fact]
    public void TheGateNamesEveryRefusal()
    {
        Assert.Equal("", TradePolicyRules.AcquisitionAttemptGate(Settlement()));
        Assert.Equal("invalid_option", TradePolicyRules.AcquisitionAttemptGate(Settlement(double.NaN)));
        Assert.Equal("ineligible", TradePolicyRules.AcquisitionAttemptGate(Settlement() with { Eligible = false }));
        Assert.Equal("invalid_target", TradePolicyRules.AcquisitionAttemptGate(Settlement() with { Participant = new TradeParticipant(TradeParticipantKind.Map, "s1") }));
        Assert.Equal("invalid_trip", TradePolicyRules.AcquisitionAttemptGate(Settlement(4)));
        Assert.Equal("insufficient_travel_food", TradePolicyRules.AcquisitionAttemptGate(Settlement() with { FoodDays = 4 }));
        Assert.Equal("spoiling_cargo", TradePolicyRules.AcquisitionAttemptGate(Settlement() with { RotDays = 4 }));
        Assert.Equal("", TradePolicyRules.AcquisitionAttemptGate(Request(TradeAcquisitionKind.Caravan)));
        Assert.Equal("invalid_request", TradePolicyRules.AcquisitionAttemptGate(Request(TradeAcquisitionKind.Caravan) with { GoodwillCost = -1 }));
        Assert.Equal("no_negotiator_console", TradePolicyRules.AcquisitionAttemptGate(Request(TradeAcquisitionKind.Caravan) with { Console = "" }));
        Assert.Equal("alliance_loss", TradePolicyRules.AcquisitionAttemptGate(Request(TradeAcquisitionKind.Caravan) with { RelationAfterPayment = "Neutral" }));
        Assert.Equal("cooldown", TradePolicyRules.AcquisitionAttemptGate(Request(TradeAcquisitionKind.Caravan) with { CooldownTicks = 10 }));
        Assert.Equal("orbital_unavailable", TradePolicyRules.AcquisitionAttemptGate(Request(TradeAcquisitionKind.Orbital) with { OrbitalAvailable = false }));
        Assert.Equal("passing_ships", TradePolicyRules.AcquisitionAttemptGate(Request(TradeAcquisitionKind.Orbital) with { PassingShips = 1 }));
    }

    [Fact]
    public void ThePlanProposesTheSoonestAttemptTheSilverAffords()
    {
        var request = new TradeAcquisitionRequest { Demands = [SteelGap], Options = [Settlement(), Request(TradeAcquisitionKind.Caravan)], Silver = 1000, Reserve = 200 };
        Assert.Equal("f1/request_caravan/bulk", TradePolicyRules.PlanTradeAcquisition(request).Proposal?.Option.ID);
        Assert.Null(TradePolicyRules.PlanTradeAcquisition(request with { Silver = 200 }).Proposal);
        Assert.Null(TradePolicyRules.PlanTradeAcquisition(request with { ActiveID = "other" }).Proposal);
        Assert.Null(TradePolicyRules.PlanTradeAcquisition(request with { Demands = [SteelGap with { Demand = SteelGap.Demand with { HorizonDays = 0.5 } }] }).Proposal);
    }

    [Fact]
    public void CargoTakesOnlySuppliedGoodsAndTheFirstPreparedFood()
    {
        var food = new SupplyDemandResult(new SupplyDemand { Good = SupplyPlanRules.NutritionKey, PerDay = 2 }, 0, 2);
        var definitions = new[] { new MissionSupplyDefinition { Definition = "Pemmican", Nutrition = 0.05 }, new MissionSupplyDefinition { Definition = "MealSimple", Nutrition = 0.9 } };
        var (cargo, goods) = TradePolicyRules.TradeMissionCargo([SteelGap, food], new HashSet<ResourceKey> { SupplyPlanRules.NutritionKey }, definitions, 10);
        Assert.Equal([new CargoItem("MealSimple", 23)], cargo);
        Assert.Equal([SupplyPlanRules.NutritionKey], goods);
    }

    [Fact]
    public void TheBudgetKeepsTheReserve()
    {
        Assert.Equal(800, TradePolicyRules.TradeMissionBudget(1000, 2));
        Assert.Equal(0, TradePolicyRules.TradeMissionBudget(100, 2));
        Assert.Equal(0, TradePolicyRules.TradeMissionBudget(1000, 0));
    }

    [Fact]
    public void TheReturnIsSafeOnlyWithinFoodAndRot()
    {
        var facts = new MissionReturnFacts { Mass = 50, Capacity = 100, FoodDays = 3, RotDays = 3, RouteTicks = GameTime.TicksPerDay * 2 };
        Assert.True(TradePolicyRules.TradeMissionReturnSafe(facts));
        Assert.False(TradePolicyRules.TradeMissionReturnSafe(facts with { Mass = 101 }));
        Assert.False(TradePolicyRules.TradeMissionReturnSafe(facts with { RotDays = 1 }));
        Assert.False(TradePolicyRules.TradeMissionReturnSafe(facts with { RouteTicks = -1 }));
    }

    [Fact]
    public void PurchasesBuyTheStockShortfallWithinSilverAndMass()
    {
        var mission = new TradeMission { SilverBudget = 500, Demand = [new CargoItem("Steel", 100)] };
        var row = new MissionPurchaseRow { LineID = "l1", DefName = "Steel", TraderCount = 200, BuyPrice = 2, UnitMass = 0.5, WillTrade = true };
        var request = new MissionPurchaseRequest
        {
            Mission = mission,
            Targets = new Dictionary<Resource, long> { [new Resource("Steel")] = 100 },
            Stock = [new Amount(new Resource("Steel"), 30), new Amount(new Resource("Steel"), 10)],
            Rows = [row],
            Silver = 1000,
            Mass = 0,
            Capacity = 100,
        };
        var (lines, cargo) = TradePolicyRules.PlanTradeMissionPurchases(request);
        Assert.Equal([new TradeLine("l1", 60)], lines);
        Assert.Equal([new CargoItem("Steel", 60)], cargo);
        Assert.Equal([new TradeLine("l1", 20)], TradePolicyRules.PlanTradeMissionPurchases(request with { Capacity = 10 }).Lines);
        Assert.Empty(TradePolicyRules.PlanTradeMissionPurchases(request with { Mass = 101 }).Lines);
    }

    [Fact]
    public void AFoodPurchaseSizesFromThePlanGap()
    {
        var mission = new TradeMission { SilverBudget = 500, Demand = [new CargoItem("MealSimple", 100)] };
        var row = new MissionPurchaseRow { LineID = "m1", DefName = "MealSimple", TraderCount = 100, BuyPrice = 1, UnitMass = 0, WillTrade = true };
        var request = new MissionPurchaseRequest
        {
            Mission = mission,
            Nutrition = new Dictionary<string, double> { ["MealSimple"] = 0.5 },
            FoodGapPerDay = 1,
            FoodTargetDays = 5,
            Rows = [row],
            Silver = 1000,
            Capacity = 100,
        };
        Assert.Equal([new TradeLine("m1", 10)], TradePolicyRules.PlanTradeMissionPurchases(request).Lines);
    }

    [Fact]
    public void FoodTradeRequiresTheSharedPlan()
    {
        Assert.Equal("food plan", Assert.Throws<RequiredFactException>(() => TradePolicyRules.RoundsTradeFood(new RoundsFacts(), new RoundsPolicy())).Fact);
    }

    [Fact]
    public void ANonFoodLineHasNoFoodClass()
    {
        var food = new TradeFoodGood { Nutrition = 0.5, Class = FoodIngredientClass.Any, Prepared = true };
        var rows = new[]
        {
            new TradeSheetRowFact { LineID = "a", DefName = "MealSimple", Food = food, TraderCount = 10, TraderWillTrade = true, BuyPrice = 5 },
            new TradeSheetRowFact { LineID = "b", DefName = "Steel", TraderCount = 10, TraderWillTrade = true, BuyPrice = 1 },
        };
        var targets = TradePolicyRules.TradeFoodTargets(new TradeFoodNeed { Nutrition = 1 }, rows);
        Assert.Equal([new TradeTarget("MealSimple", 2, 2, 0, 0)], targets);
    }
}
