using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class TotalTradeFactsTests
{
    [Fact]
    public void MissingTraderCensusRefusesInsteadOfRecoveringTheCaravanNeed() =>
        Assert.Throws<BridgeUnavailableException>(() => RoundsReads.FrameTraders(null));

    [Fact]
    public void EmptyTraderCensusAndAnUnpricedPresentTraderHaveDifferentNeeds()
    {
        Assert.True(TradePolicyRules.TradeRecovered([], [], new TradeNeed { ComponentShortfall = 1 }));
        var trader = new O.Trader { Id = "trader", Orbital = false, Kind = "bulk", FactionId = "faction", CanTrade = true, Travelling = false, GoodsStacks = 1 };
        Assert.False(TradePolicyRules.TradeRecovered([trader], [trader.Key()], new TradeNeed()));
        Assert.True(TradePolicyRules.TradeRecovered([trader], [], new TradeNeed()));
    }

    [Fact]
    public void SilverGapPricesPurchasesAndNeverTurnsMissingPricesIntoZeroDemand()
    {
        var items = ItemFactsFixtureRules.CoreItemFacts();
        var need = new TradeNeed { MedicineReplenish = 9 };
        Assert.Equal(62, SilverGapRules.SilverGap(items, need, 400, 3));
        Assert.Equal(0, SilverGapRules.SilverGap(items, need, 500, 3));
        Assert.Equal(0, SilverGapRules.SilverGap(items, new TradeNeed(), 0, 3));
        Assert.Throws<DomainException>(() => SilverGapRules.SilverGap(items, new TradeNeed { Shortfall = [new(new Resource("missing-price"), 1)] }, 400, 3));
        Assert.Throws<RequiredFactException>(() => SilverGapRules.SilverStock(new ItemFacts(), []));
    }

    [Fact]
    public void TradeFoodRequiresTheSharedPlan() =>
        Assert.Throws<RequiredFactException>(() => TradePolicyRules.RoundsTradeFood(new RoundsFacts(), RoundsRules.DefaultRoundsPolicy()));
    [Fact]
    public void AwaitingFoodPlanIsLazyUntilAnActualJoinerOfferNeedsCapacity()
    {
        var facts = new RoundsFacts { Population = PopulationFixtures.Census() };
        var capacity = PopulationRules.JoinerCapacity(facts.JoinerCapacity());
        Assert.IsType<PopulationCapacity.AwaitingFoodPlan>(capacity);
        Assert.False(PopulationRules.JoinerDeficit([], capacity, facts));
        Assert.False(PopulationRules.JoinerLetterDeficit([], capacity));
        Assert.Equal(JoinerPlanReason.NoOffer, PopulationRules.SelectJoinerMethod([], capacity, facts).Reason);
        var need = new TradeNeed { ComponentShortfall = 2 };
        Assert.Equal(need, TradePolicyRules.PopulationTradeNeed(need, capacity));
        Assert.True(TradePolicyRules.PopulationTradeNeed(need, new PopulationCapacity.Assessed(true)).Population);
        Assert.False(TradePolicyRules.PopulationTradeNeed(need, new PopulationCapacity.Assessed(false)).Population);
    }
    [Fact]
    public void NoEatersCreatesNoFoodPurchaseWithoutInventingANumericRunway()
    {
        var food = TradePolicyRules.ReviewTradeFood(new TradeFoodContext
        {
            Plan = new FoodPlan(), Runway = new FoodRunway.NoEaters(), MinDays = 2, TargetDays = 5,
        });
        Assert.Equal(new TradeFoodNeed(), food);
    }
}