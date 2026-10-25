using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3907: the trader medicine the colony model forecasts is the routine trade's own buy: its reserve, its latch, the cheapest kind and
// its gap's silver claimed before the parts'.
public sealed class MedicineTradeTests
{
    private static readonly Resource Herbal = new("MedicineHerbal");
    private static readonly Resource Industrial = new("MedicineIndustrial");

    [Fact]
    public void PresentTraderHeldStockSharesTheCanonicalMedicineClaimWithItsSelectedVisit()
    {
        var arm = new Resource("BionicArm");
        var trader = new RimGovernor.Brain.GameFacts.Observations.Trader
        {
            Id = "arrived", Kind = "medic", FactionId = "ally", Orbital = false, CanTrade = true,
            Travelling = false, GoodsStacks = 2,
            Visit = new(new("pawn"), new(2, 3), 40000, [new(Herbal.Value, 2, 14), new(arm.Value, 1, 20)]),
        };
        var visits = ForecastTradeVisits.Options([trader], 100, 100);
        var stock = new StockArrival { Medicines = [Herbal, Industrial], Visits = visits,
            Trade = EquatableArray.From(visits.SelectMany(v => v.Sources)) };
        var claimed = stock.WithMedicineClaim(ItemFactsFixtureRules.CoreItemFacts(),
            new() { Replenish = 9, Active = true }, 3, MedicalReservePolicy.Default());
        var source = claimed.Trade.Single(s => s.Resource == arm);
        Assert.Equal(28, source.Kinds[0].MedicineSilverClaim);
        Assert.Equal(1, source.Supplies(1));
        Assert.Equal(source, claimed.Visits[0].Sources.Single(s => s.Resource == arm));
        Assert.Equal(9, claimed.MedicineReplenish);
    }

    [Fact]
    public void OneMultiDefDrawBuysTheOtherMedicineWhenTheCheapestIsAbsent()
    {
        // The same generator picks one of two definitions, never both. Multiplying marginal absences would halve industrial.
        TraderOffer Offer(double price) => new() { UnitValue = price, Draws = [new(2, 1, 1, 1, 4, 4, 0, 0, 10)] };
        var kind = new TraderKindStock { Offers = new Dictionary<Resource, TraderOffer> { [Herbal] = Offer(14), [Industrial] = Offer(28) } };
        Assert.Equal(2, kind.CheapestMedicineUnits(Herbal, [Herbal, Industrial], 100), 12);
        Assert.Equal(2, kind.CheapestMedicineUnits(Industrial, [Herbal, Industrial], 100), 12);
        // Selecting both definitions means industrial is never chosen; a zero count herbal draw does allow it.
        kind = kind with { Offers = kind.Offers.ToDictionary(o => o.Key, o => o.Value with { Draws = [o.Value.Draws[0] with { DefsMin = 2, DefsMax = 2 }] }) };
        Assert.Equal(0, kind.CheapestMedicineUnits(Industrial, [Herbal, Industrial], 100), 12);
        kind = kind with { Offers = new Dictionary<Resource, TraderOffer>(kind.Offers)
        { [Herbal] = kind.Offers[Herbal] with { Draws = [kind.Offers[Herbal].Draws[0] with { CountMin = 0, CountMax = 0 }] } } };
        Assert.Equal(4, kind.CheapestMedicineUnits(Industrial, [Herbal, Industrial], 100), 12);
    }

    [Fact]
    public void MedicineKindsShareSilverAtNativeBuyPricesAndClaimBeforeParts()
    {
        TraderOffer Offer(double price) => new() { UnitValue = price, Draws = [new(2, 1, 1, 1, 4, 4, 0, 0, 10)] };
        var arm = new Resource("BionicArm");
        var kind = new TraderKindStock { Offers = new Dictionary<Resource, TraderOffer> { [Herbal] = Offer(14), [Industrial] = Offer(28),
            [arm] = new() { UnitValue = 20, Draws = [new(1, 1, 1, 1, 1, 1, 0, 0, 10) { Generator = 1 }] } } };
        var stock = new StockArrival { Medicines = [Herbal, Industrial], Trade = EquatableArray.From(kind.Offers.Select(o =>
            new TradeSource(o.Key, 0, 1) { Silver = 42, PriceFactor = 1, Kinds = [new(1, o.Value) { Stock = kind }] })) };
        var claimed = stock.WithMedicineClaim(ItemFactsFixtureRules.CoreItemFacts(), new() { Replenish = 9, Active = true }, 3, MedicalReservePolicy.Default());
        var trade = Assert.IsType<MedicineTrade>(claimed.MedicineTrade);
        Assert.Equal(1.5, trade.Sources.Single(s => s.Kind == Herbal).PerDay, 12); // floor(42 / 14) × 1/2
        Assert.Equal(.5, trade.Sources.Single(s => s.Kind == Industrial).PerDay, 12); // floor(42 / 28) × 1/2
        Assert.Equal(35, trade.SilverPerDayAt(0), 12);
        Assert.Equal(35, claimed.Trade.Single(s => s.Resource == arm).Kinds[0].MedicineSilverClaim, 12);
        Assert.Equal(0, claimed.Trade.Single(s => s.Resource == arm).Supplies(1));
        // The fluid expectation spends the shared 42 once; it does not buy 42/value separately for each medicine.
        var input = MedicineValueTests.Colony(6, 0);
        var run = ColonyModel.Run(input, [.. input.Pawns.Select(p => PawnCycle.Year(input, p).ToArray())],
            MedicineValueTests.Policy() with { HorizonDays = 2, MedicineTrade = trade });
        var end = run.Events[^1];
        Assert.Equal(2.4, end.Medicine.Sum(), 9);
        Assert.Equal(1.2, run.Events.Single(e => e.Kind == ModelEventKind.MedicineTrade && e.Id == "silver").Day, 9);
    }

    [Fact]
    public void TheLatchThresholdIsTheReviewsEntryUntilActiveThenTheTarget()
    {
        var policy = MedicalReservePolicy.Default();
        Assert.Equal((3L, 9L), (policy.Threshold(3, false), policy.Threshold(3, true)));
        var items = ItemFactsFixtureRules.CoreItemFacts();

        // The review stops at the same threshold: 8 usable herbal of 3 colonists is below the target, 9 is not, 2 starts it.
        MedicalReserveReview Review(long herbal, bool active) => MedicalRules.ReviewMedicalReserve(new MedicalReserveObservation
        {
            Catalog = items,
            Items = EquatableArray.From(new[] { new RimGovernor.Brain.GameFacts.Observations.UpkeepItem { Id = "h", Definition = Herbal.Value, Count = herbal, Forbidden = false, Rot = new RimGovernor.Brain.GameFacts.Observations.ItemRot.Never() } }),
            Census = ColonyFactsCoreTests.Snapshot() with { ColonistCount = 3, Resources = [new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = Herbal.Value, Units = herbal }] },
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
        var kind = new TraderKindStock { Offers = new Dictionary<Resource, TraderOffer> { [Herbal] = offer } };
        double Expected(long cap) => kind.CheapestMedicineUnits(Herbal, [Herbal], cap);
        Assert.Equal(4, Expected(100), 12);
        // 4 affordable: 3, 4, 4 over the three counts; 3 affordable: 3; none affordable: 0.
        Assert.Equal(11 / 3.0, Expected(4), 12);
        Assert.Equal(3, Expected(3), 12);
        Assert.Equal(0, Expected(0));

    }

    [Fact]
    public void TheMedicineGapIsClaimedBeforeThePartsAndTheTradeBuysAtTheVisitsRate()
    {
        // 1000 silver, three colonists: 700 spendable; the visit carries four herbal on average at 14 silver.
        var offer = new TraderOffer { UnitValue = 14, Draws = [new StockDraw(1, 1, 1, 1, 3, 5, 0, 0, 10)] };
        var arm = new Resource("BionicArm");
        var catalogKind = new TraderKindStock { Offers = new Dictionary<Resource, TraderOffer> { [Herbal] = offer, [arm] = offer } };
        var kinds = EquatableArray.From(new[] { new TradeKindChance(1, offer) { Stock = catalogKind } });
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

        Assert.Equal([700L, 700L], claimed.Trade.Select(s => s.Silver));
        Assert.Equal(56, claimed.Trade[1].Kinds[0].MedicineSilverClaim, 12);
        var trade = claimed.MedicineTrade!;
        Assert.Equal((new Resource("MedicineUltratech"), 9.0, 700.0, true, 3L), (trade.Best, trade.Claimed, trade.Silver, trade.Active, trade.Colonists));
        var source = Assert.Single(trade.Sources);
        Assert.Equal((Herbal, 2d), (source.Kind, source.From));
        Assert.Equal(2, source.PerDay, 12);
        Assert.Equal(28, source.SilverPerDay, 12);
        Assert.Equal([new Resource("MedicineUltratech"), new Resource("MedicineIndustrial"), Herbal], trade.KindsBestFirst);


        // No visit of a kind carrying medicine, or no silver for a unit, buys none.
        Assert.Null((stock with { Trade = [stock.Trade[1]] }).WithMedicineClaim(items, review, 3, MedicalReservePolicy.Default()).MedicineTrade);
        Assert.Null((stock with { Trade = [stock.Trade[0] with { Silver = 10 }, stock.Trade[1]] }).WithMedicineClaim(items, review, 3, MedicalReservePolicy.Default()).MedicineTrade);
    }
    [Fact]
    public void MedicineReserveRequiresItsCatalogAndCountsOnlyUnexpiredStacks()
    {
        Assert.Throws<DomainException>(() => MedicalRules.ReviewMedicalReserve(new() { Census = ColonyFactsCoreTests.Snapshot() }, false, MedicalReservePolicy.Default()));
        Assert.Throws<DomainException>(() => MedicalRules.ColonistCareBase(new(), [], 1, MedicalReservePolicy.Default()));
        Assert.Equal(MedicalCare.Herbal, MedicalRules.ColonistCareBase(ItemFactsFixtureRules.CoreItemFacts(), [], 1, MedicalReservePolicy.Default()));
        Assert.Equal(MedicalCare.Normal, MedicalRules.ColonistCareBase(ItemFactsFixtureRules.CoreItemFacts(), [new(new Resource("MedicineIndustrial"), 3)], 1, MedicalReservePolicy.Default()));
        var observation = new MedicalReserveObservation
        {

            Catalog = ItemFactsFixtureRules.CoreItemFacts(),
            Census = ColonyFactsCoreTests.Snapshot() with { ColonistCount = 1, Resources = [new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = Herbal.Value, Units = 20 }] },
            Items = [
                new() { Id = "lasting", Definition = Herbal.Value, Count = 1, Forbidden = false, Rot = new RimGovernor.Brain.GameFacts.Observations.ItemRot.Never() },
                new() { Id = "fresh", Definition = Herbal.Value, Count = 1, Forbidden = false, Rot = new RimGovernor.Brain.GameFacts.Observations.ItemRot.Rotting(1) },
                new() { Id = "expired", Definition = Herbal.Value, Count = 10, Forbidden = false, Rot = new RimGovernor.Brain.GameFacts.Observations.ItemRot.Rotting(0) }],
        };
        var result = MedicalRules.ReviewMedicalReserve(observation, true, MedicalReservePolicy.Default());
        Assert.Equal(2, result.Stock);
        Assert.Equal(1, result.Replenish);
        Assert.True(result.Active);
    }
}
