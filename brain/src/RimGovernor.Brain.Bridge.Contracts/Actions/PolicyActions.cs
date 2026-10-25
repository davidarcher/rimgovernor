using DomainHostility = RimGovernor.Brain.Domain.HostilityResponse;
using DomainMedicalCare = RimGovernor.Brain.Domain.MedicalCare;
using Op = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Bridge.Contracts;

// The rules attach, clear and status calls are wire plumbing and are not part of this file.
public static class PolicyActions
{
    /// <summary>The ReadingPolicyIntent of one per-pawn reading policy, the label and the allowed book definitions; native checks the definitions are books (NativeReadingPolicy.cs).</summary>
    internal static Op.Action ReadingPolicyAction(DomainAction action)
    {
        var v = action.ReadingPolicy ?? throw BridgeContractException.Contract("not a reading policy action");
        return new Op.Action { Intent = new Op.ReadingPolicyIntent { Name = v.Name, AllowedDefs = [.. v.Definitions] } };
    }

    /// <summary>The FoodPolicyIntent of one per-pawn food policy, the label and the allowed food definitions.</summary>
    internal static Op.Action FoodPolicyAction(DomainAction action)
    {
        var v = action.FoodPolicy ?? throw BridgeContractException.Contract("not a food policy action");
        return new Op.Action { Intent = new Op.FoodPolicyIntent { Name = v.Name, AllowedDefs = [.. v.Definitions] } };
    }

    /// <summary>The DrugPolicyIntent of one per-pawn drug policy, the label and the entries that allow anything; native checks the drugs are the policy's (NativeDrugPolicy.cs).</summary>
    internal static Op.Action DrugPolicyAction(DomainAction action)
    {
        var v = action.DrugPolicy ?? throw BridgeContractException.Contract("not a drug policy action");
        var intent = new Op.DrugPolicyIntent { Name = v.Name };
        foreach (var e in v.Entries)
        {
            intent = intent with { Entries = [.. intent.Entries, new Op.DrugPolicyEntry
                {
                    DrugDef = e.Drug,
                    AllowedForJoy = e.Joy,
                    AllowedForAddiction = e.Addiction,
                    AllowScheduled = e.Scheduled,
                    DaysFrequency = (float)e.DaysFrequency,
                    OnlyIfMoodBelow = (float)e.OnlyIfMoodBelow,
                    OnlyIfJoyBelow = (float)e.OnlyIfJoyBelow,
                    TakeToInventory = e.TakeToInventory,
                }] };
        }
        return new Op.Action { Intent = intent };
    }

    private static Op.HostilityResponse HostilityWire(DomainHostility mode) =>
        mode == DomainHostility.Ignore ? Op.HostilityResponse.Ignore
        : mode == DomainHostility.Attack ? Op.HostilityResponse.Attack
        : mode == DomainHostility.Flee ? Op.HostilityResponse.Flee
        : throw BridgeContractException.Contract("unknown hostility response");

    private static Op.MedicalCare MedicalCareWire(DomainMedicalCare care) =>
        care == DomainMedicalCare.None ? Op.MedicalCare.NoCare
        : care == DomainMedicalCare.NoMeds ? Op.MedicalCare.NoMedicine
        : care == DomainMedicalCare.Herbal ? Op.MedicalCare.HerbalOrWorse
        : care == DomainMedicalCare.Normal ? Op.MedicalCare.NormalOrWorse
        : care == DomainMedicalCare.Best ? Op.MedicalCare.Best
        : throw BridgeContractException.Contract(
            $"unknown medical care \"{Domain.Wire.Name(care)}\""
        );

    /// <summary>
    /// The PawnSettingsIntent of one pawn and one setting. Native re-checks the pawn and
    /// the setting live; a setting that already holds applies again (NativePawnSettings.cs).
    /// </summary>
    internal static Op.Action PawnSettingsAction(DomainAction action)
    {
        var v =
            action.PawnSettings
            ?? throw BridgeContractException.Contract("not a pawn settings action");
        return new Op.Action
        {
            Intent = new Op.PawnSettingsIntent { PawnId = v.Pawn.Value, Setting = SettingWire(v) },
        };
    }

    private static Op.PawnSettingsIntentSetting SettingWire(PawnSettings v)
    {
        if (v is PawnSettings.HostilitySetting hostility)
        {
            return new Op.PawnSettingsIntentSetting.HostilityResponseCase(
                HostilityWire(hostility.Mode)
            );
        }
        if (v is PawnSettings.SelfTendSetting selfTend)
        {
            return new Op.PawnSettingsIntentSetting.SelfTendCase(selfTend.On);
        }
        if (v is PawnSettings.MedicineCarrySetting carry)
        {
            return new Op.PawnSettingsIntentSetting.MedicineCarryCase(carry.Count);
        }
        if (v is PawnSettings.NicknameSetting nickname)
        {
            return new Op.PawnSettingsIntentSetting.NicknameCase(nickname.LeaveName);
        }
        if (v is PawnSettings.MedicalCareSetting care)
        {
            return new Op.PawnSettingsIntentSetting.MedicalCareCase(MedicalCareWire(care.Care));
        }
        if (v is PawnSettings.ReadingPolicySetting reading)
        {
            return new Op.PawnSettingsIntentSetting.ReadingPolicyCase(reading.Name);
        }
        if (v is PawnSettings.DrugPolicySetting drug)
        {
            return new Op.PawnSettingsIntentSetting.DrugPolicyCase(drug.Name);
        }
        if (v is PawnSettings.FoodPolicySetting food)
        {
            return new Op.PawnSettingsIntentSetting.FoodPolicyCase(food.Name);
        }
        if (v is PawnSettings.MechWorkModeSetting mode)
        {
            return new Op.PawnSettingsIntentSetting.MechWorkModeCase(mode.Mode);
        }
        if (v is PawnSettings.MechControlGroupSetting group)
        {
            return new Op.PawnSettingsIntentSetting.MechControlGroupCase(group.Group);
        }
        if (v is PawnSettings.ChoosePermitSetting permit)
        {
            return new Op.PawnSettingsIntentSetting.ChoosePermitCase(
                new Op.PermitChoice { FactionDef = permit.Faction, Permit = permit.Permit }
            );
        }
        if (v is PawnSettings.ExtractBioferriteSetting bioferrite)
        {
            return new Op.PawnSettingsIntentSetting.ExtractBioferriteCase(bioferrite.On);
        }
        throw BridgeContractException.Contract("unknown pawn setting");
    }
}
