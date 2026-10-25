using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3868: the stock-arrival model. A source's not-arrived chance is a step at the day its iterations make the count; independent
// sources multiply those chances; a consumer prices the expected wait capped at the days it has left.
public sealed class StockArrivalTests
{
    private static readonly Resource Arm = new("BionicArm");
    private static readonly Resource Herbal = new("MedicineHerbal");
    private static readonly Resource Industrial = new("MedicineIndustrial");

    private static StockArrival Of(params StockSource[] sources) => new() { Sources = EquatableArray.From(sources), Medicines = [Industrial, Herbal] };

    private static StockNeed Need(Resource resource, long count = 1) => new([resource], count);

    [Fact]
    public void OneSourceArrivesOnTheDayItsIterationsMakeTheCountAndNeverBeyondItsUnits()
    {
        // Two a batch, a unit every half day: 3 units take two batches, 4 units' worth of work, 2 days.
        var source = new StockSource(Arm, 2, 6, 2);
        Assert.Equal(0, source.ArrivalDay(0));
        Assert.Equal(1, source.ArrivalDay(2));
        Assert.Equal(2, source.ArrivalDay(3));
        Assert.Equal(double.PositiveInfinity, source.ArrivalDay(7));

        var stock = Of(source);
        Assert.Equal(1, stock.NotArrived(Need(Arm, 3), 1.9));
        Assert.Equal(0, stock.NotArrived(Need(Arm, 3), 2));
        Assert.True(stock.Arrives([Need(Arm, 3)], 2));
        Assert.False(stock.Arrives([Need(Arm, 3)], 1.9));
        // A resource no source makes never arrives.
        Assert.Equal(1, stock.NotArrived(Need(Herbal), 1000));
        Assert.False(stock.Arrives([Need(Herbal)], 1000));
    }

    [Fact]
    public void TwoIndependentSourcesMultiplyTheirNotArrivedChancesAndTheNeedsOfAReplacementAllMustArrive()
    {
        // Two benches make the arm, on day 3 and day 5: the arm has not arrived only while neither has made it.
        var stock = Of(new StockSource(Arm, 1.0 / 3, 10, 1), new StockSource(Arm, 1.0 / 5, 10, 1),
            new StockSource(Industrial, 1.0 / 4, 10, 1));
        Assert.Equal(1, stock.NotArrived(Need(Arm), 2.9));
        Assert.Equal(0, stock.NotArrived(Need(Arm), 3), 12);
        // A need of any medicine kind takes the industrial source.
        Assert.Equal(0, stock.NotArrived(new StockNeed([Industrial, Herbal], 1), 4), 12);

        // The arm on day 3 and a medicine on day 4: the surgery waits for the later.
        IReadOnlyList<StockNeed> both = [Need(Arm), new StockNeed(stock.Medicines, 1)];
        Assert.Equal(1, stock.NotReady(both, 3.5));
        Assert.Equal(0, stock.NotReady(both, 4), 12);
        Assert.Equal(4, stock.ExpectedWait(both, 30), 12);
    }

    [Fact]
    public void TheExpectedWaitIsCappedAtTheDaysLeftAndANeedNoSourceMeetsWaitsTheWholeCap()
    {
        var stock = Of(new StockSource(Arm, 0.1, 10, 1));
        // E[min(W, R)]: the arm arrives on day 10.
        Assert.Equal(10, stock.ExpectedWait([Need(Arm)], 30), 12);
        Assert.Equal(6, stock.ExpectedWait([Need(Arm)], 6), 12);
        Assert.Equal(30, stock.ExpectedWait([Need(Herbal)], 30), 12);
        Assert.Equal(0, stock.ExpectedWait([], 30));
        Assert.Equal(0, new StockWait(stock, []).Expected(30));
    }

    private static TradeSource Trade(Resource resource, double perDay, double from = 0) => new(resource, from, perDay)
    {
        Kinds = [new TradeKindChance(1, new TraderOffer { UnitValue = 100, Draws = [new StockDraw(1, 1, 1, 1, 1, 1, 0, 0, 100)] })],
        PriceFactor = 1,
        Silver = 1000,
    };

    [Fact]
    public void ATradeSourceIsExponentialFromItsStartAndTheWaitIntegratesItExactlyWithTheCraftSteps()
    {
        // A visit every 10 days that always carries the arm: E[min(W, 30)] = (1 − e^−3) × 10.
        var trade = new StockArrival { Trade = [Trade(Arm, 0.1)] };
        Assert.Equal(Math.Exp(-0.5), trade.NotArrived(Need(Arm), 5), 12);
        Assert.Equal((1 - Math.Exp(-3)) * 10, trade.ExpectedWait([Need(Arm)], 30), 12);

        // Starting on day 5: five days certain, then the exponential over the 25 left.
        var later = new StockArrival { Trade = [Trade(Arm, 0.1, from: 5)] };
        Assert.Equal(1, later.NotArrived(Need(Arm), 5));
        Assert.Equal(5 + ((1 - Math.Exp(-2.5)) * 10), later.ExpectedWait([Need(Arm)], 30), 12);

        // With a bench making the arm on day 4: the trade until then, nothing after.
        var both = new StockArrival { Sources = [new StockSource(Arm, 0.25, 10, 1)], Trade = [Trade(Arm, 0.1)] };
        Assert.Equal((1 - Math.Exp(-0.4)) * 10, both.ExpectedWait([Need(Arm)], 30), 12);

        // Two needs by trade at 0.1 and 0.2 a day: ∫ 1 − (1 − e^−0.1t)(1 − e^−0.2t) = 1/0.1 + 1/0.2 − 1/0.3 with no cap that binds.
        var two = new StockArrival { Trade = [Trade(Arm, 0.1), Trade(Herbal, 0.2)] };
        Assert.Equal(10 + 5 - (1 / 0.3), two.ExpectedWait([Need(Arm), Need(Herbal)], 1e6), 6);

        // #3909: no cap on the needs. Twenty needs each met at 0.1 a day: ∫ 1 − (1 − e^−0.1t)²⁰ = 10 × H₂₀, the expansion's equal
        // rates merged into 21 terms.
        var harmonic = Enumerable.Range(1, 20).Sum(k => 1.0 / k);
        var twenty = Enumerable.Range(0, 20).Select(_ => Need(Arm)).ToList();
        Assert.Equal(10 * harmonic, trade.ExpectedWait(twenty, 1e6), 6);
        // Mixed with a bench step and a later trade start, the expansion still matches the product it integrates.
        var mixed = new StockArrival { Sources = [new StockSource(Herbal, 0.25, 10, 1)], Trade = [Trade(Arm, 0.1), Trade(Herbal, 0.2, from: 2)] };
        var needs = Enumerable.Range(0, 18).Select(i => Need(i % 2 == 0 ? Arm : Herbal)).ToList();
        var numeric = 0.0;
        for (var t = 0.0005; t < 30; t += 0.001)
        {
            numeric += mixed.NotReady(needs, t) * 0.001;
        }

        Assert.Equal(numeric, mixed.ExpectedWait(needs, 30), 4);
    }

    [Fact]
    public void AnUnreadModelThrowsOnceAConsumerWaitsOnIt()
    {
        var unread = new StockArrival { Unread = "stock_arrival_bills" };
        Assert.EndsWith("stock_arrival_bills", Assert.Throws<RequiredFactException>(() => unread.NotArrived(Need(Arm), 1)).Message, StringComparison.Ordinal);
        Assert.Throws<RequiredFactException>(() => unread.MedicineCraft([Industrial], 3));
    }

    private static StockRecipe Recipe(Resource product, string stat = "GeneralLaborSpeed", string bench = "FabricationBench", long batch = 1) => new()
    {
        Recipe = "Make_" + product.Value,
        Product = product,
        Batch = batch,
        WorkAmount = 10_000,
        WorkSpeedStat = stat,
        BenchSpeedStat = StockArrivalRules.BenchSpeedStat,
        Benches = [new StockRecipeBench(bench, WorkType.Crafting)],
        Skills = [new SkillRequirement("Crafting", 8)],
        Ingredients = [[new Amount(new Resource("Plasteel"), 15)], [new Amount(new Resource("ComponentSpacer"), 2)]],
    };

    private static PawnState Crafter(string pawn, int crafting, double speed, WorkType work = WorkType.Crafting) =>
        StockArrivalTestFacts.Crafter(pawn, crafting, speed, work);

    private static readonly Dictionary<Resource, long> Stock = new() { [new Resource("Plasteel")] = 100, [new Resource("ComponentSpacer")] = 5 };

    [Fact]
    public void CraftingRunsAtTheBestEligibleCraftersSpeedAtTheBestUsableBenchUpToTheScarcestIngredient()
    {
        // 8 work hours × 2500 ticks × 1.2 × the bench's 0.9 ÷ 10 000 work: 2.16 a day; 5 spacer components pay for 2 arms.
        BillStackRow[] benches = [StockArrivalTestFacts.Bench("FabricationBench", true, 0.9), StockArrivalTestFacts.Bench("FabricationBench", false, 2), StockArrivalTestFacts.Bench("HandTailoringBench", true, 1)];
        var crafted = StockArrivalRules.Craft([Recipe(Arm)], benches,
            [Crafter("Low", 7, 3), Crafter("Anna", 8, 1.2), Crafter("Bo", 10, 1), Crafter("Cook", 12, 5, WorkType.Cooking)], Stock);
        var source = Assert.Single(crafted.Sources);
        Assert.Equal((Arm, 2L), (source.Resource, source.Units));
        Assert.Equal(8 * 2500 * 1.2 * 0.9 / 10_000, source.PerDay, 12);
        Assert.Empty(crafted.Findings);

        // No usable bench of the recipe: no source.
        var none = StockArrivalRules.Craft([Recipe(Arm)], [StockArrivalTestFacts.Bench("FabricationBench", false, 1)], [Crafter("Anna", 8, 1.2)], Stock);
        Assert.Empty(none.Sources);
        Assert.Empty(none.Findings);

        // A bench stat the census does not read is named; a crafter without the recipe's speed stat leaves the model unread.
        var otherStat = StockArrivalRules.Craft([Recipe(Arm) with { BenchSpeedStat = "DrugCookingSpeed" }], benches, [Crafter("Anna", 8, 1.2)], Stock);
        Assert.Contains(StockArrivalRules.BenchSpeedUnmodelled + ":Make_BionicArm", otherStat.Findings);
        var unread = StockArrivalRules.Craft([Recipe(Arm, stat: "MedicineSpeed")], benches, [Crafter("Anna", 8, 1.2)], Stock);
        Assert.Equal("work_speed:Anna:MedicineSpeed", unread.Unread);
    }

    [Fact]
    public void NativeTimetableAndEnabledWorkRowsControlCraftArrival()
    {
        var pawn = Crafter("Anna", 10, 1);
        BillStackRow[] benches = [StockArrivalTestFacts.Bench("FabricationBench", true, 1)];
        Assert.Single(StockArrivalRules.Craft([Recipe(Arm)], benches, [pawn], Stock).Sources);
        var disabled = pawn with { Settings = pawn.Settings with { Work = new PawnWork.Works(true, [new RimGovernor.Contracts.Wire.Observations.WorkSetting { DefName = "Crafting", Priority = 1, Disabled = true }]) } };
        Assert.Empty(StockArrivalRules.Craft([Recipe(Arm)], benches, [disabled], Stock).Sources);
        var missingSchedule = pawn with { Settings = pawn.Settings with { Schedule = [] } };
        Assert.Equal("stock_arrival_schedule:Anna", StockArrivalRules.Craft([Recipe(Arm)], benches, [missingSchedule], Stock).Unread);
        Assert.Equal(24, pawn.Settings.Schedule.Count);
        Assert.Single(StockArrivalRules.Craft([Recipe(Arm)], benches, [pawn], Stock).Sources);
    }
    [Fact]
    public void TheMedicineCraftedIsTheBestKindASourceMakesWithTheBetterKindsBeforeIt()
    {
        var stock = Of(new StockSource(Herbal, 4, 20, 1), new StockSource(Herbal, 6, 9, 1));
        var craft = stock.MedicineCraft([new Resource("MedicineUltratech"), Industrial, Herbal], 9);
        Assert.NotNull(craft);
        Assert.Equal((Herbal, 6.0, 9.0, 9.0), (craft.Kind, craft.PerDay, craft.Units, craft.Target));
        Assert.Equal([new Resource("MedicineUltratech"), Industrial], craft.Better);
        Assert.Null(Of().MedicineCraft([Industrial, Herbal], 9));
    }
}
