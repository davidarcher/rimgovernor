using DomainHostility = RimGovernor.Brain.Domain.HostilityResponse;
using DomainMedicalCare = RimGovernor.Brain.Domain.MedicalCare;
using Op = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go reading_policy.go, drug_policy.go, food_policy.go, pawn_settings.go and rules.go (rulesAttachAction). The rules
// attach, clear and status calls of rules.go are wire plumbing and are not part of this file.
public static class PolicyActions
{
    /// <summary>Go's <c>readingPolicyAction</c>: the ReadingPolicyIntent of one per-pawn reading policy, the label and the allowed book definitions; native checks the definitions are books (NativeReadingPolicy.cs).</summary>
    internal static Op.Action ReadingPolicyAction(DomainAction action)
    {
        var v = action.ReadingPolicy ?? throw BridgeContractException.Contract("not a reading policy action");
        return new Op.Action { Intent = new Op.ReadingPolicyIntent { Name = v.Name, AllowedDefs = [.. v.Definitions] } };
    }

    /// <summary>Go's <c>foodPolicyAction</c>: the FoodPolicyIntent of one per-pawn food policy, the label and the allowed food definitions.</summary>
    internal static Op.Action FoodPolicyAction(DomainAction action)
    {
        var v = action.FoodPolicy ?? throw BridgeContractException.Contract("not a food policy action");
        return new Op.Action { Intent = new Op.FoodPolicyIntent { Name = v.Name, AllowedDefs = [.. v.Definitions] } };
    }

    /// <summary>Go's <c>drugPolicyAction</c>: the DrugPolicyIntent of one per-pawn drug policy, the label and the entries that allow anything; native checks the drugs are the policy's (NativeDrugPolicy.cs).</summary>
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

    // Go hostilityWire.
    private static Op.HostilityResponse HostilityWire(DomainHostility mode) =>
        mode switch
        {
            DomainHostility.Ignore => Op.HostilityResponse.Ignore,
            DomainHostility.Attack => Op.HostilityResponse.Attack,
            DomainHostility.Flee => Op.HostilityResponse.Flee,
            _ => throw BridgeContractException.Contract("unknown hostility response"),
        };

    // Go medicalCareWire.
    private static Op.MedicalCare MedicalCareWire(DomainMedicalCare care) =>
        care switch
        {
            DomainMedicalCare.None => Op.MedicalCare.NoCare,
            DomainMedicalCare.NoMeds => Op.MedicalCare.NoMedicine,
            DomainMedicalCare.Herbal => Op.MedicalCare.HerbalOrWorse,
            DomainMedicalCare.Normal => Op.MedicalCare.NormalOrWorse,
            DomainMedicalCare.Best => Op.MedicalCare.Best,
            _ => throw BridgeContractException.Contract($"unknown medical care \"{Domain.Wire.Name(care)}\""),
        };

    /// <summary>
    /// Go's <c>pawnSettingsAction</c>: the PawnSettingsIntent of one pawn and one setting. Native re-checks the pawn and
    /// the setting live; a setting that already holds applies again (NativePawnSettings.cs).
    /// </summary>
    internal static Op.Action PawnSettingsAction(DomainAction action)
    {
        var v = action.PawnSettings ?? throw BridgeContractException.Contract("not a pawn settings action");
        var intent = new Op.PawnSettingsIntent { PawnId = v.Pawn.Value };
        switch (v.Kind)
        {
            case SettingKind.Hostility:
                intent = intent with { Setting = new Op.PawnSettingsIntentSetting.HostilityResponseCase(HostilityWire(v.Hostility ?? throw BridgeContractException.Contract("hostility setting without a mode"))) };
                break;
            case SettingKind.SelfTend:
                intent = intent with { Setting = new Op.PawnSettingsIntentSetting.SelfTendCase(v.SelfTend) };
                break;
            case SettingKind.MedicineCarry:
                intent = intent with { Setting = new Op.PawnSettingsIntentSetting.MedicineCarryCase(v.MedicineCarry ?? 0) };
                break;
            case SettingKind.Nickname:
                intent = intent with { Setting = new Op.PawnSettingsIntentSetting.NicknameCase(v.LeaveName) };
                break;
            case SettingKind.MedicalCare:
                intent = intent with { Setting = new Op.PawnSettingsIntentSetting.MedicalCareCase(MedicalCareWire(v.MedicalCare ?? throw BridgeContractException.Contract("medical care setting without a tier"))) };
                break;
            case SettingKind.ReadingPolicy:
                intent = intent with { Setting = new Op.PawnSettingsIntentSetting.ReadingPolicyCase(v.ReadingPolicy ?? "") };
                break;
            case SettingKind.DrugPolicy:
                intent = intent with { Setting = new Op.PawnSettingsIntentSetting.DrugPolicyCase(v.DrugPolicy ?? "") };
                break;
            case SettingKind.FoodPolicy:
                intent = intent with { Setting = new Op.PawnSettingsIntentSetting.FoodPolicyCase(v.FoodPolicy ?? "") };
                break;
            case SettingKind.MechWorkMode:
                intent = intent with { Setting = new Op.PawnSettingsIntentSetting.MechWorkModeCase(v.MechWorkMode ?? "") };
                break;
            case SettingKind.MechControlGroup:
                intent = intent with { Setting = new Op.PawnSettingsIntentSetting.MechControlGroupCase(v.MechControlGroup ?? 0) };
                break;
            case SettingKind.ChoosePermit:
                var (faction, permit) = v.ChoosePermit ?? throw BridgeContractException.Contract("choose permit setting without a permit");
                intent = intent with { Setting = new Op.PawnSettingsIntentSetting.ChoosePermitCase(new Op.PermitChoice { FactionDef = faction, Permit = permit }) };
                break;
            case SettingKind.ExtractBioferrite:
                intent = intent with { Setting = new Op.PawnSettingsIntentSetting.ExtractBioferriteCase(v.ExtractBioferrite ?? false) };
                break;
            default:
                throw BridgeContractException.Contract("unknown pawn setting");
        }
        return new Op.Action { Intent = intent };
    }
}
