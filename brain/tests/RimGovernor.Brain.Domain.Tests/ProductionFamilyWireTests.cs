using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3776: the production, bill, recipe and stock-flow reads are plain required records; each round-trips exactly
// through the wire JSON.
public sealed class ProductionFamilyWireTests
{
    private static readonly RimGovernor.Brain.GameFacts.Observations.ConsumptionSnapshot Consumption = new()
    {
        CurrentHour = 40,
        FirstHour = 2,
        Hours =
        [
            new RimGovernor.Brain.GameFacts.Observations.ConsumptionHour { Hour = 38, Rows = [new RimGovernor.Brain.GameFacts.Observations.ConsumptionRow { Definition = "WoodLog", Reason = "fuel_loaded", Count = 12 }] },
            new RimGovernor.Brain.GameFacts.Observations.ConsumptionHour { Hour = 39, Rows = [new RimGovernor.Brain.GameFacts.Observations.ConsumptionRow { Definition = "Shell_HighExplosive", Reason = "shell_loaded", Count = -1 }] },
        ],
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.StorageOccupancySnapshot Occupancy = new()
    {
        CurrentHour = 40,
        Groups = [new RimGovernor.Brain.GameFacts.Observations.StorageOccupancyGroup { Group = "Zone_3", Samples = [new RimGovernor.Brain.GameFacts.Observations.StorageOccupancySample { Hour = 38, Stacks = 4 }, new RimGovernor.Brain.GameFacts.Observations.StorageOccupancySample { Hour = 39, Stacks = 6 }] }],
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.ResourceSourcesSnapshot Sources = new()
    {
        Sources =
        [
            new RimGovernor.Brain.GameFacts.Observations.ResourceSource { Id = "Thing_MineableSteel1", DefName = "MineableSteel", Cell = new Cell(12, 30), Yield = 40, Buried = false, Designated = true, Safety = "open_surface", Distance = 7.5, Taken = false },
            new RimGovernor.Brain.GameFacts.Observations.ResourceSource { Id = "Thing_MineableSteel2", DefName = "MineableSteel", Cell = new Cell(40, 2), Yield = 40, Buried = true, Designated = false, Safety = "supported_roof", Distance = 31, Taken = true },
        ],
        Storage = new RimGovernor.Brain.GameFacts.Observations.StorageCapacity { Capacity = 750, Stored = 120, Haulers = [new PawnId("Thing_Human7")], Candidates = [new Cell(5, 6), new Cell(5, 7)] },
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.SuppliesSnapshot Supplies = new()
    {
        Context = new RimGovernor.Brain.GameFacts.Common.ObservationContext { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 },
        Stocks =
        [
            new RimGovernor.Brain.GameFacts.Observations.ResourceStock
            {
                DefName = "MinifiedSculpture", Units = 2, Stacks = 2, Spawned = 1, Ours = 2, OursUnforbidden = 1, Forbidden = 1, PlayerFaction = 2,
                Items =
                [
                    new RimGovernor.Brain.GameFacts.Observations.StockItem { Id = "Thing_Minified1", Cell = new Cell(3, 4), Snapshot = new RimGovernor.Brain.GameFacts.Observations.SnapshotRef { EntityId = "Thing_Minified1", Token = "t1" },
                        Packed = new RimGovernor.Brain.GameFacts.Observations.PackedBuilding { Id = "Thing_Sculpture1", DefName = "SculptureSmall", Quality = new RimGovernor.Brain.GameFacts.Observations.PackedQuality.Rated { Category = RimGovernor.Brain.GameFacts.Defs.QualityCategory.Excellent }, MarketValue = 410.5 } },
                    new RimGovernor.Brain.GameFacts.Observations.StockItem { Id = "Thing_Minified2", Cell = new Cell(9, 9),
                        Packed = new RimGovernor.Brain.GameFacts.Observations.PackedBuilding { Id = "Thing_Bed2", DefName = "Bed", Quality = new RimGovernor.Brain.GameFacts.Observations.PackedQuality.Unrated(), MarketValue = 80 } },
                ],
                Holders = [new RimGovernor.Brain.GameFacts.Observations.HeldStock { Holder = "Thing_Human7", HolderKind = RimGovernor.Brain.GameFacts.Observations.HolderKind.Carried, Units = 1 }],
                WeaponItems = [],
            },
        ],
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.BillsSnapshot Bills = BillWire.Census(
        BillWire.Bill("Bill_1") with
        {
            Worker = "Thing_Human7", RepeatMode = RimGovernor.Brain.GameFacts.Operations.RepeatMode.Count, RepeatCount = 3, Finished = false,
            Reservations = [new RimGovernor.Brain.GameFacts.Observations.IngredientReservation { PawnId = "Thing_Human7", Items = [new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = "RawPotatoes", Units = 10 }] }],
            UnfinishedWork = new RimGovernor.Brain.GameFacts.Observations.BillUnfinishedWork.None(),
        },
        BillWire.Bill("Bill_2") with { Suspended = true });

    private static readonly RimGovernor.Brain.GameFacts.Observations.RecipesSnapshot Recipes = new()
    {
        Snapshot = new RimGovernor.Brain.GameFacts.Observations.SnapshotRef { Context = BillWire.Context, EntityId = "Thing_Campfire1", Token = "t" },
        BenchDef = "Campfire",
        Recipes = [new RimGovernor.Brain.GameFacts.Observations.RecipeState { Recipe = "CookMealSimple", AvailableNow = true, AvailableOnBench = true }, new RimGovernor.Brain.GameFacts.Observations.RecipeState { Recipe = "Make_Pemmican", AvailableNow = false, AvailableOnBench = true }],
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.RecipeActivityRead Clothing = new()
    {
        Context = BillWire.Context,
        Options =
        [
            new RimGovernor.Brain.GameFacts.Observations.RecipeActivityOption("0", "Thing_Human7", "Thing_TableTailor1", "DoBillsTailor", "Make_Apparel_Parka", "Apparel_Parka", "Cloth", "", "", new Cell(10, 10),
                [new RimGovernor.Brain.GameFacts.Observations.RecipeIngredientObservation("Thing_Cloth1", "Cloth", 80, new Cell(4, 4), 80) { ApproachTicks = 120, CarryTicks = 90.5, ReturnTicks = 88, Capacity = 75 }],
                210.5, 3600, 40, new Cell(12, 10)) { RequiredCount = 80 },
        ],
        Retained = [new RimGovernor.Brain.GameFacts.Observations.RecipeRetainedOutput("Thing_UnfinishedApparel3", "Apparel_Parka", "Cloth", 1250.25)],
    };

    private static void RoundTrips<T>(T payload)
    {
        var json = OrdersJson.Serialize(payload);
        var back = OrdersJson.Deserialize<T>(json);
        Assert.NotNull(back);
        Assert.Equal(json, OrdersJson.Serialize(back));
        Assert.Equal(payload, back);
    }

    [Fact]
    public void TheConsumptionRingRoundTripsExactly() => RoundTrips(Consumption);

    [Fact]
    public void TheOccupancyRingRoundTripsExactly() => RoundTrips(Occupancy);

    [Fact]
    public void ResourceSourcesRoundTripExactly() => RoundTrips(Sources);

    [Fact]
    public void SuppliesRoundTripExactly() => RoundTrips(Supplies);

    [Fact]
    public void TheBillCensusRoundTripsExactly() => RoundTrips(Bills);

    [Fact]
    public void BenchRecipesRoundTripExactly() => RoundTrips(Recipes);

    [Fact]
    public void TheClothingRecipeReadRoundTripsExactly() => RoundTrips(Clothing);
}
