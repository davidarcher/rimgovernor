using RimGovernor.Contracts.Wire;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Whole bill-census wire rows for the decoder tests: one bench holding the given bills.</summary>
internal static class BillWire
{
    internal static readonly C.ObservationContext Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 };

    internal static O.BillState Bill(string id) => new()
    {
        Id = id,
        Recipe = "CookMealSimple",
        BillClass = "Bill_Production",
        Suspended = false,
        ManagedUnchanged = false,
        DefaultIngredients = true,
        UnrestrictedWorker = true,
        RepeatMode = RimGovernor.Contracts.Wire.Operations.RepeatMode.Forever,
        RepeatCount = 1,
        TargetCount = 10,
        Finished = false,
        UnfinishedWork = new O.BillUnfinishedWork.None(),
        IngredientFilter = new O.StockpileFilter { AllowedDefNames = [], HitPointsMin = 0, HitPointsMax = 1, QualityMin = Quality.Awful, QualityMax = Quality.Legendary, AllowFresh = true, AllowRotten = false },
    };

    internal static O.BillsSnapshot Census(params O.BillState[] bills) => new()
    {
        Context = Context,
        Benches = [new O.BillStackRow { Snapshot = new O.SnapshotRef { Context = Context, EntityId = "Thing_Campfire1", Token = "t" }, Bench = "Thing_Campfire1", BenchDef = "Campfire", Usable = true, WorkSpeed = 1, Bills = [.. bills] }],
    };
}
