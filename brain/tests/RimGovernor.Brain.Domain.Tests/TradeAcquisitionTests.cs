using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The trade acquisition and settlement mission rules read whole (#3785): a native pack is Packed or Unpackable
/// with each leg Reachable or Unreachable, an option's trip facts are plain, and an unread input never reaches
/// the rules (the caller raises or plans nothing).
/// </summary>
public sealed class TradeAcquisitionTests
{
    private static readonly RimGovernor.Brain.GameFacts.Observations.TradePackEstimate.Packed Safe = new(80, 200, 10, 12, new RimGovernor.Brain.GameFacts.Observations.PackRoute.Reachable(GameTime.TicksPerDay * 2), new RimGovernor.Brain.GameFacts.Observations.PackRoute.Reachable(GameTime.TicksPerDay * 3));

    [Fact]
    public void APackIsSafeOnlyWhenPackedReachableAndProvisioned()
    {
        Assert.True(TradePolicyRules.SafeTradeMissionPack(Safe));
        Assert.Equal((5.0, true), TradePolicyRules.TradeMissionRouteDays(Safe));
        Assert.False(TradePolicyRules.SafeTradeMissionPack(new RimGovernor.Brain.GameFacts.Observations.TradePackEstimate.Unpackable()));
        var homeless = Safe with { Home = new RimGovernor.Brain.GameFacts.Observations.PackRoute.Unreachable() };
        Assert.False(TradePolicyRules.SafeTradeMissionPack(homeless));
        Assert.Equal((0.0, false), TradePolicyRules.TradeMissionRouteDays(homeless));
        Assert.False(TradePolicyRules.SafeTradeMissionPack(Safe with { MassUsage = 201 }));
        Assert.False(TradePolicyRules.SafeTradeMissionPack(Safe with { FoodRotDays = 4 }));
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
    public void PurchasesBuyTheSelectedAuthorizationWithinSilverAndMass()
    {
        var mission = new TradeMission { SilverBudget = 500, Demand = [new CargoItem("Steel", 100)] };
        var row = new MissionPurchaseRow { LineID = "l1", DefName = "Steel", TraderCount = 200, BuyPrice = 2, UnitMass = 0.5, WillTrade = true };
        var request = new MissionPurchaseRequest
        {
            Mission = mission,
            Rows = [row],
            Silver = 1000,
            Mass = 0,
            Capacity = 100,
        };
        var (lines, cargo) = TradePolicyRules.PlanTradeMissionPurchases(request);
        Assert.Equal([new TradeLine("l1", 100)], lines);
        Assert.Equal([new CargoItem("Steel", 100)], cargo);
        Assert.Equal([new TradeLine("l1", 20)], TradePolicyRules.PlanTradeMissionPurchases(request with { Capacity = 10 }).Lines);
        Assert.Empty(TradePolicyRules.PlanTradeMissionPurchases(request with { Mass = 101 }).Lines);
    }

    [Fact]
    public void FoodTradeRequiresTheSharedPlan()
    {
        Assert.Equal("food plan", Assert.Throws<RequiredFactException>(() => TradePolicyRules.TradeFood(new ReviewFacts(), new PlannerSettings())).Fact);
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
