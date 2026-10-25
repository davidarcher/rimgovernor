using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3907: the trader medicine the colony model forecasts is the routine trade's own buy: its reserve, its latch, the cheapest kind and
// its gap's silver claimed before the parts'.
public sealed class MedicineTradeTests
{
    private static readonly Resource Herbal = new("MedicineHerbal");

    [Fact]
    public void TheLatchThresholdIsTheReviewsEntryUntilActiveThenTheTarget()
    {
        var policy = MedicalReservePolicy.Default();
        Assert.Equal((3L, 9L), (policy.Threshold(3, false), policy.Threshold(3, true)));
        var items = ItemFactsFixtureRules.CoreItemFacts();

        // The review stops at the same threshold: 8 usable herbal of 3 colonists is below the target, 9 is not, 2 starts it.
        MedicalReserveReview Review(long herbal, bool active) => MedicalRules.ReviewMedicalReserve(new MedicalReserveObservation
        {
            Colonists = 3, Catalog = items,
            Items = EquatableArray.From(new[] { new MedicineStackRow { Id = "h", Definition = Herbal, Count = herbal, Rot = new RimGovernor.Contracts.Wire.Observations.ItemRot.Never() } }),
            Resources = EquatableArray.From(new[] { new Amount(Herbal, herbal) }),
        }, active, policy);

        Assert.True(Review(8, true).Active);
        Assert.False(Review(9, true).Active);
        Assert.False(Review(3, false).Active);
        Assert.True(Review(2, false).Active);
        Assert.Equal(7, Review(2, false).Replenish);
    }

    [Fact]
    public void TheSpendableSilverIsWhatLiesAboveTheRoutineTradesReserve()
    {
        // 100 a colonist, at least 200: three colonists keep 300, one keeps 200, nobody buys.
        Assert.Equal(700, TradePolicyRules.SpendableSilver(1000, 3));
        Assert.Equal(800, TradePolicyRules.SpendableSilver(1000, 1));
        Assert.Equal(0, TradePolicyRules.SpendableSilver(250, 3));
        Assert.Equal(0, TradePolicyRules.SpendableSilver(1000, 0));
    }

    [Fact]
    public void TheClaimIsTheGapAtTheCheapestMedicinesMarketValueAsTheSilverGapPricesIt()
    {
        var items = ItemFactsFixtureRules.CoreItemFacts();
        Assert.Equal(162, TradePolicyRules.MedicineClaim(items, 9));
        var need = new TradeNeed { MedicineReplenish = 9 };
        Assert.Equal((162.0, true), need.PurchasePrice(items));
    }

    [Fact]
    public void AVisitSellsWhatTheGeneratorsMakeWithinWhatTheSilverPays()
    {
        // 3 to 5 units a visit, each 14 silver.
        var offer = new TraderOffer { UnitValue = 14, Draws = [new StockDraw(1, 1, 1, 1, 3, 5, 0, 0, 10)] };
        Assert.Equal(5, offer.MaxCount);
        Assert.Equal(4, offer.Expected(100), 12);
        // 4 affordable: 3, 4, 4 over the three counts; 3 affordable: 3; none affordable: 0.
        Assert.Equal(11 / 3.0, offer.Expected(4), 12);
        Assert.Equal(3, offer.Expected(3), 12);
        Assert.Equal(0, offer.Expected(0));

        var kinds = EquatableArray.From(new[] { new TradeKindChance(0.5, offer) });
        var source = new TradeSource(Herbal, 0, 0.1) { Kinds = kinds, PriceFactor = 1, Silver = 14 * 3 };
        // 0.1 visits a day, half of them by this kind, each affording 3.
        Assert.Equal(0.1 * 0.5 * 3, source.UnitsPerDay(), 12);
        Assert.Equal(0, (source with { Silver = 13 }).UnitsPerDay());
    }

    [Fact]
    public void TheMedicineGapIsClaimedBeforeThePartsAndTheTradeBuysAtTheVisitsRate()
    {
        // 1000 silver, three colonists: 700 spendable; a gap of 9 herbal costs 162, so a part sees 538.
        var offer = new TraderOffer { UnitValue = 14, Draws = [new StockDraw(1, 1, 1, 1, 3, 5, 0, 0, 10)] };
        var kinds = EquatableArray.From(new[] { new TradeKindChance(1, offer) });
        var arm = new Resource("BionicArm");
        var stock = new StockArrival
        {
            Medicines = [Herbal, new Resource("MedicineIndustrial"), new Resource("MedicineUltratech")],
            Trade =
            [
                new TradeSource(Herbal, 2, 0.5) { Kinds = kinds, PriceFactor = 1, Silver = 700 },
                new TradeSource(arm, 0, 0.1) { Kinds = kinds, PriceFactor = 1, Silver = 700 },
            ],
        };
        var items = ItemFactsFixtureRules.CoreItemFacts();
        var review = new MedicalReserveReview { Active = true, Replenish = 9L };
        var claimed = stock.WithMedicineClaim(items, review, 3, MedicalReservePolicy.Default());

        Assert.Equal([700L, 538L], claimed.Trade.Select(s => s.Silver));
        var trade = claimed.MedicineTrade!;
        Assert.Equal((Herbal, new Resource("MedicineUltratech"), 9.0, 38.0, true, 3L), (trade.Kind, trade.Best, trade.Claimed, trade.Units, trade.Active, trade.Colonists));
        Assert.Equal([new MedicineTradeSource(2, 0.5 * 4)], trade.Sources);
        Assert.Equal([new Resource("MedicineUltratech"), new Resource("MedicineIndustrial")], trade.Better);

        // A medicine gap larger than the spendable silver leaves the part nothing.
        Assert.Equal(0, stock.WithMedicineClaim(items, review with { Replenish = 100L }, 3, MedicalReservePolicy.Default()).Trade[1].Silver);
        // No visit of a kind carrying medicine, or no silver for a unit, buys none.
        Assert.Null((stock with { Trade = [stock.Trade[1]] }).WithMedicineClaim(items, review, 3, MedicalReservePolicy.Default()).MedicineTrade);
        Assert.Null((stock with { Trade = [stock.Trade[0] with { Silver = 10 }, stock.Trade[1]] }).WithMedicineClaim(items, review, 3, MedicalReservePolicy.Default()).MedicineTrade);
    }
    [Fact]
    public void MedicineReserveRequiresItsCatalogAndCountsOnlyUnexpiredStacks()
    {
        Assert.Throws<DomainException>(() => MedicalRules.ReviewMedicalReserve(new(), false, MedicalReservePolicy.Default()));
        Assert.Throws<DomainException>(() => MedicalRules.ColonistCareBase(new(), [], 1, MedicalReservePolicy.Default()));
        Assert.Equal(MedicalCare.Herbal, MedicalRules.ColonistCareBase(ItemFactsFixtureRules.CoreItemFacts(), [], 1, MedicalReservePolicy.Default()));
        Assert.Equal(MedicalCare.Normal, MedicalRules.ColonistCareBase(ItemFactsFixtureRules.CoreItemFacts(), [new(new Resource("MedicineIndustrial"), 3)], 1, MedicalReservePolicy.Default()));
        var observation = new MedicalReserveObservation
        {
            Colonists = 1,
            Catalog = ItemFactsFixtureRules.CoreItemFacts(),
            Resources = [new(Herbal, 20)],
            Items = [
                new() { Id = "lasting", Definition = Herbal, Count = 1, Rot = new RimGovernor.Contracts.Wire.Observations.ItemRot.Never() },
                new() { Id = "fresh", Definition = Herbal, Count = 1, Rot = new RimGovernor.Contracts.Wire.Observations.ItemRot.Rotting(1) },
                new() { Id = "expired", Definition = Herbal, Count = 10, Rot = new RimGovernor.Contracts.Wire.Observations.ItemRot.Rotting(0) }],
        };
        var result = MedicalRules.ReviewMedicalReserve(observation, true, MedicalReservePolicy.Default());
        Assert.Equal(2, result.Stock);
        Assert.Equal(1, result.Replenish);
        Assert.True(result.Active);
    }
}
