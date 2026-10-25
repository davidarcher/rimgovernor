using System.Text.Json;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class OperationArgumentTests
{
    [Fact]
    public void MissingRequiredArgumentsCannotDecodeAsZero()
    {
        Missing<RimGovernor.Brain.GameOrders.Operations.DefCount>();
        Missing<RimGovernor.Brain.GameOrders.Operations.Rectangle>();
        Missing<RimGovernor.Brain.GameOrders.Operations.WorkPriority>();
        Missing<RimGovernor.Brain.GameOrders.Operations.TradeLine>();
        Missing<RimGovernor.Brain.GameOrders.Operations.GiveItemIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.IdeoligionReformIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.AreaRect>();
        Missing<RimGovernor.Brain.GameOrders.Operations.DialogIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.ApparelPolicyIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.CombatDoor>();
        Missing<RimGovernor.Brain.GameOrders.Operations.AreaIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.PolicyPruneIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.EndTrade>();
        Missing<RimGovernor.Brain.GameOrders.Operations.HusbandryIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.PrisonerInteractionIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.DesignateIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.HackDesignationIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.AutoHomeAreaIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.DraftIntent>();
        Missing<RimGovernor.Brain.GameFacts.Operations.ZonePreview>();
        Missing<RimGovernor.Brain.GameOrders.Operations.FishingSettings>();
        Missing<RimGovernor.Brain.GameFacts.Operations.Refusal>();
        Missing<RimGovernor.Brain.GameOrders.Operations.FormCaravanIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.CommsTradeRequestIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.FilterPatch>();
        Missing<RimGovernor.Brain.GameOrders.Operations.StockpileSettings>();
        Missing<RimGovernor.Brain.GameOrders.Operations.ZoneIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.OpenTrade>();
        Missing<RimGovernor.Brain.GameOrders.Operations.SetTradeLines>();
        Missing<RimGovernor.Brain.GameOrders.Operations.AcceptTrade>();
        Missing<RimGovernor.Brain.GameOrders.Operations.ApplyRequest>();
        Missing<RimGovernor.Brain.GameOrders.Operations.GiveJobOptions>();
        Missing<RimGovernor.Brain.GameOrders.Operations.RelocateIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.ProductionBillIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.AssignIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntent>();
        Missing<RimGovernor.Brain.GameOrders.Operations.BuildingIntent>();
    }

    [Fact]
    public void ExplicitZeroIsValidForTradeAndWorkSettings()
    {
        var line = new RimGovernor.Brain.GameOrders.Operations.TradeLine { LineId = "line", AbsoluteCount = 0 };
        Assert.Equal(line, OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.TradeLine>(OrdersJson.Serialize(line)));
        var priority = new RimGovernor.Brain.GameOrders.Operations.WorkPriority { WorkTypeDef = "Hauling", Priority = 0 };
        Assert.Equal(priority, OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.WorkPriority>(OrdersJson.Serialize(priority)));
        var answer = new RimGovernor.Brain.GameOrders.Operations.DialogIntent { WindowId = 1, OptionIndex = 0, OptionLabel = "Accept" };
        Assert.Equal(answer, OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.DialogIntent>(OrdersJson.Serialize(answer)));
    }

    [Fact]
    public void RemovedOrderEnumStatesAreRejected()
    {
        Removed<RimGovernor.Brain.GameFacts.Operations.CombatFireMode>("COMBAT_FIRE_MODE_UNSPECIFIED");
        Removed<RimGovernor.Brain.GameOrders.Operations.CombatDoorMode>("COMBAT_DOOR_MODE_UNSPECIFIED");
        Removed<RimGovernor.Brain.GameOrders.Operations.ThingDesignation>("THING_DESIGNATION_UNSPECIFIED");
        Removed<RimGovernor.Brain.GameOrders.Operations.PowerSetting>("POWER_SETTING_UNSPECIFIED");
        Removed<RimGovernor.Brain.GameFacts.Operations.HostilityResponse>("HOSTILITY_RESPONSE_UNSPECIFIED");
        Removed<RimGovernor.Brain.GameFacts.Operations.MedicalCare>("MEDICAL_CARE_UNSPECIFIED");
        Removed<RimGovernor.Brain.GameFacts.Operations.RepeatMode>("REPEAT_MODE_UNSPECIFIED");
        Removed<RimGovernor.Brain.GameOrders.Operations.ZoneType>("ZONE_TYPE_UNSPECIFIED");
        Removed<RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction>("PRISONER_INTERACTION_UNSPECIFIED");
        Removed<RimGovernor.Brain.GameOrders.Operations.EndTradeKind>("END_TRADE_KIND_UNSPECIFIED");
        Removed<RimGovernor.Brain.GameOrders.Operations.AreaOperation>("AREA_OPERATION_UNSPECIFIED");
        Removed<RimGovernor.Brain.GameOrders.Operations.PolicyDatabase>("POLICY_DATABASE_UNSPECIFIED");
        Removed<RimGovernor.Brain.GameOrders.Operations.DesignationGuard>("DESIGNATION_GUARD_UNSPECIFIED");
        Removed<RimGovernor.Brain.GameOrders.Operations.HusbandryOrder>("HUSBANDRY_ORDER_UNSPECIFIED");
    }

    [Fact]
    public void ScalarChoicePayloadsRoundTripWithoutNullableProjections()
    {
        RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting[] pawnSettings =
        [
            new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.HostilityResponseCase(RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Ignore),
            new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.SelfTendCase(false),
            new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.MedicineCarryCase(0),
            new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.MedicalCareCase(RimGovernor.Brain.GameFacts.Operations.MedicalCare.NoCare),
            new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.MechControlGroupCase(0),
            new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.ExtractBioferriteCase(false),
        ];
        foreach (var setting in pawnSettings)
            Assert.Equal(setting, OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting>(OrdersJson.Serialize(setting)));
        RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange[] buildingSettings =
        [
            new RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.TargetTemperatureCase(0),
            new RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.MedicalCase(false),
            new RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.AutoRefuelCase(false),
        ];
        foreach (var setting in buildingSettings)
            Assert.Equal(setting, OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange>(OrdersJson.Serialize(setting)));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting>("""{"type":"selfTend"}"""));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange>("""{"type":"medical"}"""));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntentLoading>("""{"type":"autoload"}"""));
        RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntentLoading loading = new RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntentLoading.AutoloadCase(false);
        Assert.Equal(loading, OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntentLoading>(OrdersJson.Serialize(loading)));
    }

    [Fact]
    public void ProductionBillsNameTheirSupportedRepetitionAndCorpseStates()
    {
        RimGovernor.Brain.GameOrders.Operations.BillRepetition[] repetitions =
        [new RimGovernor.Brain.GameOrders.Operations.BillRepetition.Forever(), new RimGovernor.Brain.GameOrders.Operations.BillRepetition.Count(2), new RimGovernor.Brain.GameOrders.Operations.BillRepetition.Target(6, 3)];
        foreach (var repetition in repetitions)
        {
            var settings = new RimGovernor.Brain.GameOrders.Operations.BillSettings { Repetition = repetition, BeerReserve = false, Corpses = new RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.NotApplicable() };
            Assert.Equal(settings, OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.BillSettings>(OrdersJson.Serialize(settings)));
        }
        RimGovernor.Brain.GameOrders.Operations.BillCorpseClass[] corpses =
        [
            new RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.NotApplicable(),
            new RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.Selected(RimGovernor.Brain.GameFacts.Common.CorpseClass.Animal),
            new RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.Selected(RimGovernor.Brain.GameFacts.Common.CorpseClass.Stranger),
            new RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.Selected(RimGovernor.Brain.GameFacts.Common.CorpseClass.Colonist),
        ];
        foreach (var corpse in corpses)
            Assert.Equal(corpse, OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.BillCorpseClass>(OrdersJson.Serialize(corpse)));
        Missing<RimGovernor.Brain.GameOrders.Operations.BillSettings>();
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.BillRepetition>("""{"type":"count"}"""));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.BillRepetition>("""{"type":"target","quantity":4}"""));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.BillCorpseClass>("""{"type":"selected"}"""));
    }

    [Fact]
    public void ConditionalArgumentsNamePreservationAndExactSelections()
    {
        RoundTrip<RimGovernor.Brain.GameOrders.Operations.HitPointRange>(new RimGovernor.Brain.GameOrders.Operations.HitPointRange.Preserve(), new RimGovernor.Brain.GameOrders.Operations.HitPointRange.Selected(0, 1));
        RoundTrip<RimGovernor.Brain.GameOrders.Operations.StockpilePriorityChange>(new RimGovernor.Brain.GameOrders.Operations.StockpilePriorityChange.Preserve(), new RimGovernor.Brain.GameOrders.Operations.StockpilePriorityChange.Selected(RimGovernor.Brain.Domain.StockpilePriority.Important));
        RoundTrip<RimGovernor.Brain.GameOrders.Operations.StockpilePresetChange>(new RimGovernor.Brain.GameOrders.Operations.StockpilePresetChange.Preserve(), new RimGovernor.Brain.GameOrders.Operations.StockpilePresetChange.Selected(RimGovernor.Brain.Domain.FilterBase.Nothing));
        RoundTrip<RimGovernor.Brain.GameOrders.Operations.RelocationMode>(new RimGovernor.Brain.GameOrders.Operations.RelocationMode.Uninstall(), new RimGovernor.Brain.GameOrders.Operations.RelocationMode.Move(RimGovernor.Brain.GameFacts.Placement.Rotation.North, new(3, 4)));
        RoundTrip<RimGovernor.Brain.GameOrders.Operations.SurgeryTarget>(new RimGovernor.Brain.GameOrders.Operations.SurgeryTarget.WholeBody(), new RimGovernor.Brain.GameOrders.Operations.SurgeryTarget.Part(0));
        RoundTrip<RimGovernor.Brain.GameOrders.Operations.QuestRewardSelection>(new RimGovernor.Brain.GameOrders.Operations.QuestRewardSelection.NoChoice(), new RimGovernor.Brain.GameOrders.Operations.QuestRewardSelection.Choose(0));
        RoundTrip<RimGovernor.Brain.GameOrders.Operations.FinishingSkillChange>(new RimGovernor.Brain.GameOrders.Operations.FinishingSkillChange.Preserve(), new RimGovernor.Brain.GameOrders.Operations.FinishingSkillChange.Selected(0));
        RoundTrip<RimGovernor.Brain.GameOrders.Operations.ConstructionTierChange>(new RimGovernor.Brain.GameOrders.Operations.ConstructionTierChange.Preserve(), new RimGovernor.Brain.GameOrders.Operations.ConstructionTierChange.Selected(0));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.HitPointRange>("""{"type":"set","minimum":0}"""));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.RelocationMode>("""{"type":"move"}"""));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.SurgeryTarget>("""{"type":"part"}"""));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.QuestRewardSelection>("""{"type":"choose"}"""));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.FinishingSkillChange>("""{"type":"set"}"""));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.ConstructionTierChange>("""{"type":"set"}"""));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.StockpilePriorityChange>("""{"type":"set"}"""));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.StockpilePresetChange>("""{"type":"set"}"""));
    }

    [Fact]
    public void LocationArgumentsNameTheirPhysicalOrNativeSearchState()
    {
        var cell = new RimGovernor.Brain.Domain.Cell(3, 4);
        RoundTrip<RimGovernor.Brain.GameOrders.Operations.RescueBed>(new RimGovernor.Brain.GameOrders.Operations.RescueBed.Search(), new RimGovernor.Brain.GameOrders.Operations.RescueBed.At(cell));
        RoundTrip<RimGovernor.Brain.GameOrders.Operations.MortarAim>(new RimGovernor.Brain.GameOrders.Operations.MortarAim.Cleared(), new RimGovernor.Brain.GameOrders.Operations.MortarAim.At(cell));
        RoundTrip<RimGovernor.Brain.GameOrders.Operations.DesignationLocation>(new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.ByTarget(), new RimGovernor.Brain.GameOrders.Operations.DesignationLocation.At(cell));
        RoundTrip<RimGovernor.Brain.GameOrders.Operations.RitualLocation>(new RimGovernor.Brain.GameOrders.Operations.RitualLocation.NotStarting(), new RimGovernor.Brain.GameOrders.Operations.RitualLocation.At(cell));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.RescueBed>("""{"type":"at"}"""));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.MortarAim>("""{"type":"at"}"""));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.DesignationLocation>("""{"type":"at"}"""));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameOrders.Operations.RitualLocation>("""{"type":"at"}"""));
    }
    private static void RoundTrip<T>(params T[] states)
    {
        foreach (var state in states)
            Assert.Equal(state, OrdersJson.Deserialize<T>(OrdersJson.Serialize(state)));
    }
    private static void Missing<T>() => Assert.Throws<JsonException>(() => OrdersJson.Deserialize<T>("{}"));

    private static void Removed<T>(string oldName) where T : struct, Enum
    {
        Assert.False(Enum.IsDefined(default(T)));
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<T>(JsonSerializer.Serialize(oldName)));
        foreach (var value in Enum.GetValues<T>())
            Assert.Equal(value, OrdersJson.Deserialize<T>(OrdersJson.Serialize(value)));
    }
}
