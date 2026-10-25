using RimGovernor.Contracts.Wire;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// #3776: the production, bill, recipe and stock-flow reads are plain required records; each round-trips exactly
// through the wire JSON.
public sealed class ProductionFamilyWireTests
{
    private static readonly O.ConsumptionSnapshot Consumption = new()
    {
        CurrentHour = 40,
        FirstHour = 2,
        Hours =
        [
            new O.ConsumptionHour { Hour = 38, Rows = [new O.ConsumptionRow { Definition = "WoodLog", Reason = "fuel_loaded", Count = 12 }] },
            new O.ConsumptionHour { Hour = 39, Rows = [new O.ConsumptionRow { Definition = "Shell_HighExplosive", Reason = "shell_loaded", Count = -1 }] },
        ],
    };

    private static readonly O.StorageOccupancySnapshot Occupancy = new()
    {
        CurrentHour = 40,
        Groups = [new O.StorageOccupancyGroup { Group = "Zone_3", Samples = [new O.StorageOccupancySample { Hour = 38, Stacks = 4 }, new O.StorageOccupancySample { Hour = 39, Stacks = 6 }] }],
    };

    private static readonly O.ResourceSourcesSnapshot Sources = new()
    {
        Sources =
        [
            new O.ResourceSource { Id = "Thing_MineableSteel1", DefName = "MineableSteel", Cell = new Cell(12, 30), Yield = 40, Buried = false, Designated = true, Safety = "open_surface", Distance = 7.5, Taken = false },
            new O.ResourceSource { Id = "Thing_MineableSteel2", DefName = "MineableSteel", Cell = new Cell(40, 2), Yield = 40, Buried = true, Designated = false, Safety = "supported_roof", Distance = 31, Taken = true },
        ],
        Storage = new O.StorageCapacity { Capacity = 750, Stored = 120, Haulers = [new PawnId("Thing_Human7")], Candidates = [new Cell(5, 6), new Cell(5, 7)] },
    };

    private static readonly O.SuppliesSnapshot Supplies = new()
    {
        Context = new RimGovernor.Contracts.Wire.Common.ObservationContext { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 },
        Stocks =
        [
            new O.ResourceStock
            {
                DefName = "MinifiedSculpture", Units = 2, Stacks = 2, Spawned = 1, Ours = 2, OursUnforbidden = 1, Forbidden = 1, PlayerFaction = 2,
                Items =
                [
                    new O.StockItem { Id = "Thing_Minified1", Cell = new Cell(3, 4), Snapshot = new O.SnapshotRef { EntityId = "Thing_Minified1", Token = "t1" },
                        Packed = new O.PackedBuilding { Id = "Thing_Sculpture1", DefName = "SculptureSmall", Quality = new O.PackedQuality.Rated { Category = RimGovernor.Contracts.Wire.Defs.QualityCategory.Excellent }, MarketValue = 410.5 } },
                    new O.StockItem { Id = "Thing_Minified2", Cell = new Cell(9, 9),
                        Packed = new O.PackedBuilding { Id = "Thing_Bed2", DefName = "Bed", Quality = new O.PackedQuality.Unrated(), MarketValue = 80 } },
                ],
                Holders = [new O.HeldStock { Holder = "Thing_Human7", HolderKind = O.HolderKind.Carried, Units = 1 }],
                WeaponItems = [],
            },
        ],
    };

    private static readonly O.BillsSnapshot Bills = BillWire.Census(
        BillWire.Bill("Bill_1") with
        {
            Worker = "Thing_Human7", RepeatMode = RimGovernor.Contracts.Wire.Operations.RepeatMode.Count, RepeatCount = 3, Finished = false,
            Reservations = [new O.IngredientReservation { PawnId = "Thing_Human7", Items = [new O.Quantity { DefName = "RawPotatoes", Units = 10 }] }],
            UnfinishedWork = new O.BillUnfinishedWork.None(),
        },
        BillWire.Bill("Bill_2") with { Suspended = true });

    private static readonly O.RecipesSnapshot Recipes = new()
    {
        Snapshot = new O.SnapshotRef { Context = BillWire.Context, EntityId = "Thing_Campfire1", Token = "t" },
        BenchDef = "Campfire",
        Recipes = [new O.RecipeState { Recipe = "CookMealSimple", AvailableNow = true, AvailableOnBench = true }, new O.RecipeState { Recipe = "Make_Pemmican", AvailableNow = false, AvailableOnBench = true }],
    };

    private static readonly O.RecipeActivityRead Clothing = new()
    {
        Context = BillWire.Context,
        Options =
        [
            new O.RecipeActivityOption("0", "Thing_Human7", "Thing_TableTailor1", "DoBillsTailor", "Make_Apparel_Parka", "Apparel_Parka", "Cloth", "", "", new Cell(10, 10),
                [new O.RecipeIngredientObservation("Thing_Cloth1", "Cloth", 80, new Cell(4, 4), 80) { ApproachTicks = 120, CarryTicks = 90.5, ReturnTicks = 88, Capacity = 75 }],
                210.5, 3600, 40, new Cell(12, 10)) { RequiredCount = 80 },
        ],
        Retained = [new O.RecipeRetainedOutput("Thing_UnfinishedApparel3", "Apparel_Parka", "Cloth", 1250.25)],
    };

    private static void RoundTrips<T>(T payload)
    {
        var json = WireJson.Serialize(payload);
        var back = WireJson.Deserialize<T>(json);
        Assert.NotNull(back);
        Assert.Equal(json, WireJson.Serialize(back));
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
