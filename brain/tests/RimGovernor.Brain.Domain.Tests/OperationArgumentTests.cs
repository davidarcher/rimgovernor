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

    private static void Missing<T>() => Assert.Throws<JsonException>(() => WireJson.Deserialize<T>("{}"));

    private static void Removed<T>(string oldName) where T : struct, Enum
    {
        Assert.False(Enum.IsDefined(default(T)));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<T>(JsonSerializer.Serialize(oldName)));
        foreach (var value in Enum.GetValues<T>())
            Assert.Equal(value, WireJson.Deserialize<T>(WireJson.Serialize(value)));
    }
}
