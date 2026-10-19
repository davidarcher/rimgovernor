using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Amount = RimGovernor.Brain.Policy.Amount;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Gear stuff is chosen by stats among the stuffs that arrive within tier 1 (StuffArrival), cost breaking ties, and its bill makes only that stuff (#2467).</summary>
public sealed class GearStuffChoiceTests
{
    private static readonly Resource Steel = new("Steel");
    private static readonly Resource Plasteel = new("Plasteel");

    private static GearOption Shirt(string stuff, double sharp, double cost = 20) => new()
    {
        ID = "bill:Shirt/" + stuff,
        Definition = new("Shirt"),
        Stuff = new(stuff),
        Slot = GearSlot.SkinTorso,
        Layers = ["OnSkin"],
        Groups = ["Torso"],
        Source = GearSource.Bill,
        Quality = 2,
        Condition = 1,
        Sharp = sharp,
        Cost = cost,
        Ingredients = [new Amount(new(stuff), 50)],
    };

    private static readonly Resource Cloth = new("Cloth");
    private static readonly Resource Leather = new("Leather_Plain");

    private static StuffArrival Arrival(Dictionary<Resource, long> stock, params SupplyCandidate?[] supply) => new(stock, [.. supply.Select(c => c!)]);

    private static GearOption Arrived(GearOption o, StuffArrival arrival) => o with { Arrival = arrival.Of(o) };

    private static Resource? Planned(params GearOption[] options) =>
        GearLoadoutRules.PlanGearLoadout(new GearLoadoutInput { Ambient = 20, ComfortableMin = 10, ComfortableMax = 30, Options = EquatableArray.From(options) })
            .Target.SingleOrDefault()?.Stuff;

    [Fact]
    public void StockCoveringTheBillArrivesNow()
    {
        Assert.Equal(Known(0.0), Arrival(new() { [Plasteel] = 50 }).Of(Shirt("Plasteel", .5)));
        Assert.False(Arrival(new() { [Plasteel] = 49 }).Of(Shirt("Plasteel", .5)).IsKnown);
        // An item, and a bill of a def made from no stuff, are in hand.
        Assert.Equal(Known(0.0), Arrival(new()).Of(Shirt("Plasteel", .5) with { Source = GearSource.Stored }));
    }

    [Fact]
    public void AGrowingFieldArrivesAtItsHarvest()
    {
        var field = ClothingDemandRules.GrowingFieldCandidate(Cloth, "zone1", 30, Known(3.5));
        // The stock and the standing crop together cover the 50 units at the harvest.
        Assert.Equal(Known(3.5), Arrival(new() { [Cloth] = 20 }, field).Of(Shirt("Cloth", .1)));
        Assert.False(Arrival(new() { [Cloth] = 10 }, field).Of(Shirt("Cloth", .1)).IsKnown);
        Assert.Null(ClothingDemandRules.GrowingFieldCandidate(Cloth, "zone1", 30, Unknown<double>()));
    }

    [Fact]
    public void AHuntableAnimalArrivesAtItsHuntLead()
    {
        var deer = new AcquisitionSource { Id = "deer", Hunt = true, Products = [new SourceProduct(Leather, 30)] };
        var hunt = ClothingDemandRules.HuntYieldCandidate(deer);
        Assert.Equal(Known(0.0), hunt!.LeadDays);
        Assert.Equal(Known(0.0), Arrival(new(), hunt, ClothingDemandRules.HuntYieldCandidate(deer with { Id = "doe" })).Of(Shirt(Leather.Value, .1)));
        // One deer's leather is short of the bill.
        Assert.False(Arrival(new(), hunt).Of(Shirt(Leather.Value, .1)).IsKnown);
        Assert.Null(ClothingDemandRules.HuntYieldCandidate(deer with { Hunt = false }));
    }

    [Fact]
    public void AStoredCorpseArrivesAfterItsButchering()
    {
        // A deer corpse's race yields 30 leather; one butchering's 450 work at speed 1 is 0.0075 days.
        var lead = 450.0 / GameTime.TicksPerDay;
        var corpse = ClothingDemandRules.CorpseYieldCandidate("corpse1", [new SourceProduct(Leather, 30)], lead);
        Assert.Equal(Known(lead), Arrival(new() { [Leather] = 20 }, corpse).Of(Shirt(Leather.Value, .1)));
        Assert.False(Arrival(new(), corpse).Of(Shirt(Leather.Value, .1)).IsKnown);
        Assert.Null(ClothingDemandRules.CorpseYieldCandidate("corpse1", [], lead));
    }

    [Fact]
    public void AStuffWithNoSourceHasNoArrivalAndIsNotOffered()
    {
        GearOption[] options = [Shirt("Plasteel", .5), Shirt("Steel", .2)];
        Assert.Equal(Plasteel, Planned(options)); // Stats alone pick the plasteel.
        var arrival = Arrival(new() { [Steel] = 100 });
        Assert.False(arrival.Of(options[0]).IsKnown);
        Assert.Equal(Steel, Planned([.. options.Select(o => Arrived(o, arrival))]));
    }

    [Fact]
    public void AnArrivalBeyondTheTierOneHorizonIsNotOffered()
    {
        // The better cloth shirt waits on a field; the steel one is in stock.
        GearOption[] options = [Shirt("Cloth", .5), Shirt("Steel", .2)];
        Dictionary<Resource, long> stock = new() { [Steel] = 100 };
        var later = Arrival(stock, ClothingDemandRules.GrowingFieldCandidate(Cloth, "zone1", 50, Known(ForecastCoupled.TierOneHorizonDays + 2)));
        Assert.Equal(Steel, Planned([.. options.Select(o => Arrived(o, later))]));
        var soon = Arrival(stock, ClothingDemandRules.GrowingFieldCandidate(Cloth, "zone1", 50, Known(ForecastCoupled.TierOneHorizonDays / 2)));
        Assert.Equal(Cloth, Planned([.. options.Select(o => Arrived(o, soon))]));
    }

    [Fact]
    public void AmongEqualStatsTheCheaperStuffWins()
    {
        // By id the cloth shirt would come first; the synthread one costs less at the same stats.
        Assert.Equal(new Resource("Synthread"), Planned(Shirt("Cloth", .2, cost: 30), Shirt("Synthread", .2, cost: 10)));
    }

    [Fact]
    public void TheBillFilterAdmitsOnlyTheChosenStuffAndPicksTheCheapestElsewhere()
    {
        var items = new ItemFacts
        {
            StuffCategories = new Dictionary<Resource, IReadOnlyList<string>> { [Steel] = ["Metallic"], [new("Bronze")] = ["Metallic"] },
            Market = new Dictionary<Resource, double> { [Steel] = 1.9, [new("Bronze")] = 1.5, [new("Cloth")] = 1.5, [new("Leather")] = 2.1 },
        };
        EquatableArray<EquatableArray<Amount>> slots =
        [
            // Bronze is a cheaper category mate of the chosen steel: the bill still makes steel.
            [new Amount(Steel, 50), new Amount(new("Bronze"), 50)],
            [new Amount(new("Leather"), 10), new Amount(new("Cloth"), 10)],
        ];
        Assert.Equal([new Resource("Cloth"), Steel], GearRules.GearFilter(slots, Steel, items));
    }
}
