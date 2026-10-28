using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Whole bill-census wire rows for the decoder tests: one bench holding the given bills.</summary>
internal static class BillWire
{
    internal static readonly RimGovernor.Brain.GameFacts.Common.ObservationContext Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 };

    internal static RimGovernor.Brain.Policy.IngredientReservation Reservation(string bill, string pawn, params RimGovernor.Brain.Policy.Amount[] items) => new()
    { Bill = bill, Observed = new RimGovernor.Brain.GameFacts.Observations.IngredientReservation { PawnId = pawn, Items = RimGovernor.Brain.Domain.EquatableArray.From(items.Select(a => new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = a.Resource.Value, Units = a.Count })) } };

    internal static RimGovernor.Brain.GameFacts.Observations.BillState Bill(string id) => new()
    {
        Id = id,
        Recipe = "CookMealSimple",

        Suspended = false,
        ManagedUnchanged = false,
        DefaultIngredients = true,
        UnrestrictedWorker = true,
        RepeatMode = RimGovernor.Brain.GameFacts.Operations.RepeatMode.Forever,
        RepeatCount = 1,
        TargetCount = 10,
        Finished = false,
        UnfinishedWork = new RimGovernor.Brain.GameFacts.Observations.BillUnfinishedWork.None(),
        IngredientFilter = new RimGovernor.Brain.GameFacts.Observations.StockpileFilter { AllowedDefNames = [], HitPointsMin = 0, HitPointsMax = 1, QualityMin = Quality.Awful, QualityMax = Quality.Legendary, AllowFresh = true, AllowRotten = false },
    };

    internal static RimGovernor.Brain.GameFacts.Observations.BillsSnapshot Census(params RimGovernor.Brain.GameFacts.Observations.BillState[] bills) => new()
    {
        Context = Context,
        Benches = [new RimGovernor.Brain.GameFacts.Observations.BillStackRow { Snapshot = new RimGovernor.Brain.GameFacts.Observations.SnapshotRef { Context = Context, EntityId = "Thing_Campfire1", Token = "t" }, Bench = "Thing_Campfire1", BenchDef = "Campfire", Usable = true, WorkSpeed = 1,  Bills = [.. bills] }],
    };
}
