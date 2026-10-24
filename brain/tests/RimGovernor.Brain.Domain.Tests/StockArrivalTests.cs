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

    private static StockCrafter Crafter(string pawn, int crafting, double speed, WorkType work = WorkType.Crafting) =>
        new(pawn, 8, [work], [new SkillLevel("Crafting", crafting)], [new WorkSpeedRead("GeneralLaborSpeed", speed)]);

    private static readonly Dictionary<Resource, long> Stock = new() { [new Resource("Plasteel")] = 100, [new Resource("ComponentSpacer")] = 5 };

    [Fact]
    public void CraftingRunsAtTheBestEligibleCraftersSpeedAtTheBestUsableBenchUpToTheScarcestIngredient()
    {
        // 8 work hours × 2500 ticks × 1.2 × the bench's 0.9 ÷ 10 000 work: 2.16 a day; 5 spacer components pay for 2 arms.
        StockBench[] benches = [new("FabricationBench", true, 0.9), new("FabricationBench", false, 2), new("HandTailoringBench", true, 1)];
        var crafted = StockArrivalRules.Craft([Recipe(Arm)], benches,
            [Crafter("Low", 7, 3), Crafter("Anna", 8, 1.2), Crafter("Bo", 10, 1), Crafter("Cook", 12, 5, WorkType.Cooking)], Stock);
        var source = Assert.Single(crafted.Sources);
        Assert.Equal((Arm, 2L), (source.Resource, source.Units));
        Assert.Equal(8 * 2500 * 1.2 * 0.9 / 10_000, source.PerDay, 12);
        Assert.Contains(StockArrival.TradeUnmodelled, crafted.Findings);

        // No usable bench of the recipe: no source, only the trade finding.
        var none = StockArrivalRules.Craft([Recipe(Arm)], [new("FabricationBench", false, 1)], [Crafter("Anna", 8, 1.2)], Stock);
        Assert.Empty(none.Sources);
        Assert.Equal([StockArrival.TradeUnmodelled], none.Findings);

        // A bench stat the census does not read is named; a crafter without the recipe's speed stat leaves the model unread.
        var otherStat = StockArrivalRules.Craft([Recipe(Arm) with { BenchSpeedStat = "DrugCookingSpeed" }], benches, [Crafter("Anna", 8, 1.2)], Stock);
        Assert.Contains(StockArrivalRules.BenchSpeedUnmodelled + ":Make_BionicArm", otherStat.Findings);
        var unread = StockArrivalRules.Craft([Recipe(Arm, stat: "MedicineSpeed")], benches, [Crafter("Anna", 8, 1.2)], Stock);
        Assert.Equal("work_speed:Anna:MedicineSpeed", unread.Unread);
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
