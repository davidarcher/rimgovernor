using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;
using Op = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The policy census, the stockpile filter read, the outfit read and the stockpile settings order are total
/// (#3767): every fact member is <c>required</c>, the order's priority and preset are the brain's own
/// <see cref="StockpilePriority"/> and <see cref="FilterBase"/> (no Unspecified arm), and each round-trips through
/// <c>WireJsonContext</c> exactly.
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
        Child = false,
        Slave = false,
        IncapableOfViolence = false,
        Work = [],
        Drafted = false,
        Skills = [],
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
                    DrugEntries = [new Op.DrugPolicyEntry { DrugDef = "Beer", AllowedForJoy = true, AllowedForAddiction = false, AllowScheduled = false, DaysFrequency = 1, OnlyIfMoodBelow = 1, OnlyIfJoyBelow = 0.5f, TakeToInventory = 0 }],
                },
            ],
            AllowedAreas = [new AllowedArea { Id = "Area_3", Label = "Safe", Pawns = [], CellCount = 40 }],
            FoodEaters = [new FoodEater { Pawn = new PawnId("Thing_Human9"), Kind = FoodEaterKind.Prisoner, Precepts = ["Cannibalism_Preferred"], Traits = [new Trait { DefName = "Ascetic", Degree = 0 }] }],
        };

        var json = WireJson.Serialize(policies);

        Assert.Equal(
            """{"outfit":[],"drug":[{"id":"DrugPolicy_1","label":"Social","pawns":["Thing_Human5"],"allowedDefs":[],"drugEntries":[{"drugDef":"Beer","allowedForJoy":true,"allowedForAddiction":false,"allowScheduled":false,"daysFrequency":1,"onlyIfMoodBelow":1,"onlyIfJoyBelow":0.5,"takeToInventory":0}]}],"food":[],"reading":[],"allowedAreas":[{"id":"Area_3","label":"Safe","pawns":[],"cellCount":40}],"foodEaters":[{"pawn":"Thing_Human9","kind":"prisoner","precepts":["Cannibalism_Preferred"],"traits":[{"defName":"Ascetic","degree":0}]}]}""",
            json);
        Assert.Equal(policies, WireJson.Deserialize<Policies>(json));
    }

    [Fact]
    public void StockpileFilterRoundTripIsExact()
    {
        var filter = new O.StockpileFilter { AllowedDefNames = ["Steel"], HitPointsMin = 0.25, HitPointsMax = 1, QualityMin = Quality.Normal, QualityMax = Quality.Legendary, AllowFresh = false, AllowRotten = true };

        var json = WireJson.Serialize(filter);

        Assert.Equal("""{"allowedDefNames":["Steel"],"hitPointsMin":0.25,"hitPointsMax":1,"qualityMin":"Normal","qualityMax":"Legendary","allowFresh":false,"allowRotten":true}""", json);
        Assert.Equal(filter, WireJson.Deserialize<O.StockpileFilter>(json));
    }

    [Fact]
    public void StockpileSettingsCarryTheBrainEnums()
    {
        var settings = new Op.StockpileSettings { Priority = StockpilePriority.Important, Preset = FilterBase.OutdoorSafeWaste };

        var json = WireJson.Serialize(settings);

        Assert.Equal("""{"priority":"important","preset":"outdoor_safe_waste","filter":null}""", json);
        Assert.Equal(settings, WireJson.Deserialize<Op.StockpileSettings>(json));
        Assert.Equal(new Op.StockpileSettings(), WireJson.Deserialize<Op.StockpileSettings>("""{"priority":null,"preset":null,"filter":null}"""));
    }

    [Fact]
    public void OutfitRoundTripIsExact()
    {
        var json = WireJson.Serialize(EmptyOutfit);

        Assert.Equal(
            """{"token":"","name":"","allowedDefs":[],"minHitPoints":0,"maxHitPoints":1,"minQuality":0,"maxQuality":6,"excludesTainted":false,"child":false,"slave":false,"incapableOfViolence":false,"work":[],"drafted":false,"skills":[],"pawnName":"","policyId":"","requiredDefs":[],"nude":false}""",
            json);
        Assert.Equal(EmptyOutfit, WireJson.Deserialize<ApparelPolicyState>(json));
    }
}
