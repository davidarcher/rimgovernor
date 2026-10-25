using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The policy census, the stockpile filter read, the outfit read and the stockpile settings order are total
/// (#3767): every fact member is <c>required</c>, the order's priority and preset are the brain's own
/// <see cref="StockpilePriority"/> and <see cref="FilterBase"/> (no Unspecified arm), and each round-trips through
/// <c>OrdersJsonContext</c> exactly.
/// </summary>
public sealed class PoliciesTests
{
    /// <summary>A colony with no policy, no allowed area and no prisoner or animal food policy holder.</summary>
    internal static readonly Policies NoPolicies = new() { Outfit = [], Drug = [], Food = [], Reading = [], AllowedAreas = [], FoodEaters = [] };

    /// <summary>An adult free colonist on an outfit that allows no apparel, with no work, skills or requirements.</summary>
    internal static readonly ApparelPolicyState EmptyOutfit = new()
    {
        Token = "",
        Name = "",
        AllowedDefs = [],
        MinHitPoints = 0,
        MaxHitPoints = 1,
        MinQuality = 0,
        MaxQuality = 6,
        ExcludesTainted = false,
        Slave = false,
        PawnName = "",
        PolicyId = "",
        RequiredDefs = [],
        Nude = false,
    };

    [Fact]
    public void JsonRoundTripIsExact()
    {
        var policies = NoPolicies with
        {
            Drug =
            [
                new PolicyEntry
                {
                    Id = "DrugPolicy_1", Label = "Social", Pawns = [new PawnId("Thing_Human5")], AllowedDefs = [],
                    DrugEntries = [new RimGovernor.Brain.GameFacts.Operations.DrugPolicyEntry { DrugDef = "Beer", AllowedForJoy = true, AllowedForAddiction = false, AllowScheduled = false, DaysFrequency = 1, OnlyIfMoodBelow = 1, OnlyIfJoyBelow = 0.5f, TakeToInventory = 0 }],
                },
            ],
            AllowedAreas = [new AllowedArea { Id = "Area_3", Label = "Safe", Pawns = [], CellCount = 40 }],
            FoodEaters = [new FoodEater { Pawn = new PawnId("Thing_Human9"), Kind = FoodEaterKind.Prisoner, Precepts = ["Cannibalism_Preferred"], Traits = [new Trait { DefName = "Ascetic", Degree = 0 }] }],
        };

        var json = OrdersJson.Serialize(policies);

        Assert.Equal(
            """{"outfit":[],"drug":[{"id":"DrugPolicy_1","label":"Social","pawns":["Thing_Human5"],"allowedDefs":[],"drugEntries":[{"drugDef":"Beer","allowedForJoy":true,"allowedForAddiction":false,"allowScheduled":false,"daysFrequency":1,"onlyIfMoodBelow":1,"onlyIfJoyBelow":0.5,"takeToInventory":0}]}],"food":[],"reading":[],"allowedAreas":[{"id":"Area_3","label":"Safe","pawns":[],"cellCount":40}],"foodEaters":[{"pawn":"Thing_Human9","kind":"prisoner","precepts":["Cannibalism_Preferred"],"traits":[{"defName":"Ascetic","degree":0}]}]}""",
            json);
        Assert.Equal(policies, OrdersJson.Deserialize<Policies>(json));
    }

    [Fact]
    public void StockpileFilterRoundTripIsExact()
    {
        var filter = new RimGovernor.Brain.GameFacts.Observations.StockpileFilter { AllowedDefNames = ["Steel"], HitPointsMin = 0.25, HitPointsMax = 1, QualityMin = Quality.Normal, QualityMax = Quality.Legendary, AllowFresh = false, AllowRotten = true };

        var json = OrdersJson.Serialize(filter);

        Assert.Equal("""{"allowedDefNames":["Steel"],"hitPointsMin":0.25,"hitPointsMax":1,"qualityMin":"Normal","qualityMax":"Legendary","allowFresh":false,"allowRotten":true}""", json);
        Assert.Equal(filter, OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.StockpileFilter>(json));
    }

    [Fact]
    public void StockpileSettingsCarryTheBrainEnums()
    {
        var settings = new RimGovernor.Brain.GameOrders.Operations.StockpileSettings { Priority = new RimGovernor.Brain.GameOrders.Operations.StockpilePriorityChange.Selected(StockpilePriority.Important), Preset = new RimGovernor.Brain.GameOrders.Operations.StockpilePresetChange.Selected(FilterBase.OutdoorSafeWaste)};

        var json = OrdersJson.Serialize(settings);

        Assert.Equal("""{"priority":{"type":"set","value":"important"},"preset":{"type":"set","value":"outdoor_safe_waste"},"filter":null}""", json);
        Assert.Equal(settings, OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.StockpileSettings>(json));
        Assert.Equal(new RimGovernor.Brain.GameOrders.Operations.StockpileSettings { Priority = new RimGovernor.Brain.GameOrders.Operations.StockpilePriorityChange.Preserve(), Preset = new RimGovernor.Brain.GameOrders.Operations.StockpilePresetChange.Preserve() }, OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.StockpileSettings>("""{"priority":{"type":"preserve"},"preset":{"type":"preserve"},"filter":null}"""));
    }

    [Fact]
    public void OutfitRoundTripIsExact()
    {
        var json = OrdersJson.Serialize(EmptyOutfit);

        Assert.Equal(
            """{"token":"","name":"","allowedDefs":[],"minHitPoints":0,"maxHitPoints":1,"minQuality":0,"maxQuality":6,"excludesTainted":false,"slave":false,"pawnName":"","policyId":"","requiredDefs":[],"nude":false}""",
            json);
        Assert.Equal(EmptyOutfit, OrdersJson.Deserialize<ApparelPolicyState>(json));
    }
}
