using System.Text.Json;
using RimGovernor.Contracts.Wire;
using Op = RimGovernor.Contracts.Wire.Operations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class OperationArgumentTests
{
    [Fact]
    public void MissingRequiredArgumentsCannotDecodeAsZero()
    {
        Missing<Op.DefCount>();
        Missing<Op.Rectangle>();
        Missing<Op.WorkPriority>();
        Missing<Op.TradeLine>();
        Missing<Op.GiveItemIntent>();
        Missing<Op.IdeoligionReformIntent>();
        Missing<Op.AreaRect>();
        Missing<Op.DialogIntent>();
        Missing<Op.ApparelPolicyIntent>();
        Missing<Op.CombatDoor>();
        Missing<Op.AreaIntent>();
        Missing<Op.PolicyPruneIntent>();
        Missing<Op.EndTrade>();
        Missing<Op.HusbandryIntent>();
        Missing<Op.PrisonerInteractionIntent>();
        Missing<Op.DesignateIntent>();
        Missing<Op.HackDesignationIntent>();
        Missing<Op.AutoHomeAreaIntent>();
        Missing<Op.DraftIntent>();
        Missing<Op.ZonePreview>();
        Missing<Op.FishingSettings>();
        Missing<Op.Refusal>();
        Missing<Op.FormCaravanIntent>();
        Missing<Op.CommsTradeRequestIntent>();
        Missing<Op.FilterPatch>();
        Missing<Op.StockpileSettings>();
        Missing<Op.ZoneIntent>();
        Missing<Op.OpenTrade>();
        Missing<Op.SetTradeLines>();
        Missing<Op.AcceptTrade>();
        Missing<Op.ApplyRequest>();
        Missing<Op.GiveJobOptions>();
        Missing<Op.RelocateIntent>();
        Missing<Op.ProductionBillIntent>();
        Missing<Op.AssignIntent>();
        Missing<Op.QuestShuttleIntent>();
        Missing<Op.BuildingIntent>();
    }

    [Fact]
    public void ExplicitZeroIsValidForTradeAndWorkSettings()
    {
        var line = new Op.TradeLine { LineId = "line", AbsoluteCount = 0 };
        Assert.Equal(line, WireJson.Deserialize<Op.TradeLine>(WireJson.Serialize(line)));
        var priority = new Op.WorkPriority { WorkTypeDef = "Hauling", Priority = 0 };
        Assert.Equal(priority, WireJson.Deserialize<Op.WorkPriority>(WireJson.Serialize(priority)));
        var answer = new Op.DialogIntent { WindowId = 1, OptionIndex = 0, OptionLabel = "Accept" };
        Assert.Equal(answer, WireJson.Deserialize<Op.DialogIntent>(WireJson.Serialize(answer)));
    }

    [Fact]
    public void RemovedOrderEnumStatesAreRejected()
    {
        Removed<Op.CombatFireMode>("COMBAT_FIRE_MODE_UNSPECIFIED");
        Removed<Op.CombatDoorMode>("COMBAT_DOOR_MODE_UNSPECIFIED");
        Removed<Op.ThingDesignation>("THING_DESIGNATION_UNSPECIFIED");
        Removed<Op.PowerSetting>("POWER_SETTING_UNSPECIFIED");
        Removed<Op.HostilityResponse>("HOSTILITY_RESPONSE_UNSPECIFIED");
        Removed<Op.MedicalCare>("MEDICAL_CARE_UNSPECIFIED");
        Removed<Op.RepeatMode>("REPEAT_MODE_UNSPECIFIED");
        Removed<Op.ZoneType>("ZONE_TYPE_UNSPECIFIED");
        Removed<Op.PrisonerInteraction>("PRISONER_INTERACTION_UNSPECIFIED");
        Removed<Op.EndTradeKind>("END_TRADE_KIND_UNSPECIFIED");
        Removed<Op.AreaOperation>("AREA_OPERATION_UNSPECIFIED");
        Removed<Op.PolicyDatabase>("POLICY_DATABASE_UNSPECIFIED");
        Removed<Op.DesignationGuard>("DESIGNATION_GUARD_UNSPECIFIED");
        Removed<Op.HusbandryOrder>("HUSBANDRY_ORDER_UNSPECIFIED");
    }

    [Fact]
    public void ScalarChoicePayloadsRoundTripWithoutNullableProjections()
    {
        Op.PawnSettingsIntentSetting[] pawnSettings =
        [
            new Op.PawnSettingsIntentSetting.HostilityResponseCase(Op.HostilityResponse.Ignore),
            new Op.PawnSettingsIntentSetting.SelfTendCase(false),
            new Op.PawnSettingsIntentSetting.MedicineCarryCase(0),
            new Op.PawnSettingsIntentSetting.MedicalCareCase(Op.MedicalCare.NoCare),
            new Op.PawnSettingsIntentSetting.MechControlGroupCase(0),
            new Op.PawnSettingsIntentSetting.ExtractBioferriteCase(false),
        ];
        foreach (var setting in pawnSettings)
            Assert.Equal(setting, WireJson.Deserialize<Op.PawnSettingsIntentSetting>(WireJson.Serialize(setting)));
        Op.BuildingPatchIntentChange[] buildingSettings =
        [
            new Op.BuildingPatchIntentChange.TargetTemperatureCase(0),
            new Op.BuildingPatchIntentChange.MedicalCase(false),
            new Op.BuildingPatchIntentChange.AutoRefuelCase(false),
        ];
        foreach (var setting in buildingSettings)
            Assert.Equal(setting, WireJson.Deserialize<Op.BuildingPatchIntentChange>(WireJson.Serialize(setting)));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Op.PawnSettingsIntentSetting>("""{"type":"selfTend"}"""));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Op.BuildingPatchIntentChange>("""{"type":"medical"}"""));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Op.QuestShuttleIntentLoading>("""{"type":"autoload"}"""));
        Op.QuestShuttleIntentLoading loading = new Op.QuestShuttleIntentLoading.AutoloadCase(false);
        Assert.Equal(loading, WireJson.Deserialize<Op.QuestShuttleIntentLoading>(WireJson.Serialize(loading)));
    }

    [Fact]
    public void ProductionBillsNameTheirSupportedRepetitionAndCorpseStates()
    {
        Op.BillRepetition[] repetitions =
        [new Op.BillRepetition.Forever(), new Op.BillRepetition.Count(2), new Op.BillRepetition.Target(6, 3)];
        foreach (var repetition in repetitions)
        {
            var settings = new Op.BillSettings { Repetition = repetition, BeerReserve = false, Corpses = new Op.BillCorpseClass.NotApplicable() };
            Assert.Equal(settings, WireJson.Deserialize<Op.BillSettings>(WireJson.Serialize(settings)));
        }
        Op.BillCorpseClass[] corpses =
        [
            new Op.BillCorpseClass.NotApplicable(),
            new Op.BillCorpseClass.Selected(RimGovernor.Contracts.Wire.Common.CorpseClass.Animal),
            new Op.BillCorpseClass.Selected(RimGovernor.Contracts.Wire.Common.CorpseClass.Stranger),
            new Op.BillCorpseClass.Selected(RimGovernor.Contracts.Wire.Common.CorpseClass.Colonist),
        ];
        foreach (var corpse in corpses)
            Assert.Equal(corpse, WireJson.Deserialize<Op.BillCorpseClass>(WireJson.Serialize(corpse)));
        Missing<Op.BillSettings>();
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Op.BillRepetition>("""{"type":"count"}"""));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Op.BillRepetition>("""{"type":"target","quantity":4}"""));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Op.BillCorpseClass>("""{"type":"selected"}"""));
    }

    [Fact]
    public void ConditionalArgumentsNamePreservationAndExactSelections()
    {
        RoundTrip<Op.HitPointRange>(new Op.HitPointRange.Preserve(), new Op.HitPointRange.Selected(0, 1));
        RoundTrip<Op.StockpilePriorityChange>(new Op.StockpilePriorityChange.Preserve(), new Op.StockpilePriorityChange.Selected(RimGovernor.Brain.Domain.StockpilePriority.Important));
        RoundTrip<Op.StockpilePresetChange>(new Op.StockpilePresetChange.Preserve(), new Op.StockpilePresetChange.Selected(RimGovernor.Brain.Domain.FilterBase.Nothing));
        RoundTrip<Op.RelocationMode>(new Op.RelocationMode.Uninstall(), new Op.RelocationMode.Move(RimGovernor.Contracts.Wire.Placement.Rotation.North));
        RoundTrip<Op.SurgeryTarget>(new Op.SurgeryTarget.WholeBody(), new Op.SurgeryTarget.Part(0));
        RoundTrip<Op.QuestRewardSelection>(new Op.QuestRewardSelection.NoChoice(), new Op.QuestRewardSelection.Choose(0));
        RoundTrip<Op.FinishingSkillChange>(new Op.FinishingSkillChange.Preserve(), new Op.FinishingSkillChange.Selected(0));
        RoundTrip<Op.ConstructionTierChange>(new Op.ConstructionTierChange.Preserve(), new Op.ConstructionTierChange.Selected(0));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Op.HitPointRange>("""{"type":"set","minimum":0}"""));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Op.RelocationMode>("""{"type":"move"}"""));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Op.SurgeryTarget>("""{"type":"part"}"""));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Op.QuestRewardSelection>("""{"type":"choose"}"""));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Op.FinishingSkillChange>("""{"type":"set"}"""));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Op.ConstructionTierChange>("""{"type":"set"}"""));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Op.StockpilePriorityChange>("""{"type":"set"}"""));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Op.StockpilePresetChange>("""{"type":"set"}"""));
    }

    private static void RoundTrip<T>(params T[] states)
    {
        foreach (var state in states)
            Assert.Equal(state, WireJson.Deserialize<T>(WireJson.Serialize(state)));
    }
    private static void Missing<T>() => Assert.Throws<JsonException>(() => WireJson.Deserialize<T>("{}"));

    private static void Removed<T>(string oldName) where T : struct, Enum
    {
        Assert.False(Enum.IsDefined(default(T)));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<T>(JsonSerializer.Serialize(oldName)));
        foreach (var value in Enum.GetValues<T>())
            Assert.Equal(value, WireJson.Deserialize<T>(WireJson.Serialize(value)));
    }
}
