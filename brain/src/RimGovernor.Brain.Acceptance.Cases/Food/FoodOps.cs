using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The food stock <c>test/food_channels_prepare</c> leaves on the stripped map: Units of FoodDef.</summary>
internal sealed record FoodStock(string FoodDef, int Units);

/// <summary>The bespoke op the food cases share: strip every food channel off the tribal baseline and seed one stock.</summary>
internal static class FoodOps
{
    /// <summary>Strips food stocks, crops, growing zones, animals, corpses and food producers, then seeds the stock.</summary>
    public static readonly FixtureOp<FoodStock, Fx.FixtureDone> EmptyChannels = new(
        "test/food_channels_prepare",
        stock => ServedOps.Args(("foodDef", stock.FoodDef), ("stockUnits", stock.Units)),
        ServedOps.Done("test/food_channels_prepare"));

    /// <summary>The tribal baseline save the food cases strip.</summary>
    public static readonly Start Baseline = new SaveStart { Name = Baselines.BaselineName };
}
