using DomainHostility = RimGovernor.Brain.Domain.HostilityResponse;
using DomainMedicalCare = RimGovernor.Brain.Domain.MedicalCare;

namespace RimGovernor.Brain.GameAccess;

// The rules attach, clear and status calls are wire plumbing and are not part of this file.
public static class PolicyActions
{
    /// <summary>The ReadingPolicyIntent of one per-pawn reading policy, the label and the allowed book definitions; native checks the definitions are books (NativeReadingPolicy.cs).</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action ReadingPolicyAction(DomainAction action)
    {
        var v = action.ReadingPolicy ?? throw BridgeContractException.Contract("not a reading policy action");
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.ReadingPolicyIntent { Name = v.Name, AllowedDefs = [.. v.Definitions] } };
    }

    /// <summary>The FoodPolicyIntent of one per-pawn food policy, the label and the allowed food definitions.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action FoodPolicyAction(DomainAction action)
    {
        var v = action.FoodPolicy ?? throw BridgeContractException.Contract("not a food policy action");
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = new RimGovernor.Brain.GameOrders.Operations.FoodPolicyIntent { Name = v.Name, AllowedDefs = [.. v.Definitions] } };
    }

    /// <summary>The DrugPolicyIntent of one per-pawn drug policy, the label and the entries that allow anything; native checks the drugs are the policy's (NativeDrugPolicy.cs).</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action DrugPolicyAction(DomainAction action)
    {
        var v = action.DrugPolicy ?? throw BridgeContractException.Contract("not a drug policy action");
        var intent = new RimGovernor.Brain.GameOrders.Operations.DrugPolicyIntent { Name = v.Name };
        foreach (var e in v.Entries)
        {
            intent = intent with { Entries = [.. intent.Entries, new RimGovernor.Brain.GameFacts.Operations.DrugPolicyEntry
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
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
    }

    private static RimGovernor.Brain.GameFacts.Operations.HostilityResponse HostilityWire(DomainHostility mode) =>
        mode == DomainHostility.Ignore ? RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Ignore
        : mode == DomainHostility.Attack ? RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Attack
        : mode == DomainHostility.Flee ? RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Flee
        : throw BridgeContractException.Contract("unknown hostility response");

    private static RimGovernor.Brain.GameFacts.Operations.MedicalCare MedicalCareWire(DomainMedicalCare care) =>
        care == DomainMedicalCare.None ? RimGovernor.Brain.GameFacts.Operations.MedicalCare.NoCare
        : care == DomainMedicalCare.NoMeds ? RimGovernor.Brain.GameFacts.Operations.MedicalCare.NoMedicine
        : care == DomainMedicalCare.Herbal ? RimGovernor.Brain.GameFacts.Operations.MedicalCare.HerbalOrWorse
        : care == DomainMedicalCare.Normal ? RimGovernor.Brain.GameFacts.Operations.MedicalCare.NormalOrWorse
        : care == DomainMedicalCare.Best ? RimGovernor.Brain.GameFacts.Operations.MedicalCare.Best
        : throw BridgeContractException.Contract(
            $"unknown medical care \"{Domain.Wire.Name(care)}\""
        );

    /// <summary>
    /// The PawnSettingsIntent of one pawn and one setting. Native re-checks the pawn and
    /// the setting live; a setting that already holds applies again (NativePawnSettings.cs).
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action PawnSettingsAction(DomainAction action)
    {
        var v =
            action.PawnSettings
            ?? throw BridgeContractException.Contract("not a pawn settings action");
        return new RimGovernor.Brain.GameOrders.Operations.Action
        {
            Intent = new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntent { PawnId = v.Pawn.Value, Setting = SettingWire(v) },
        };
    }

    private static RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting SettingWire(PawnSettings v)
    {
        if (v is PawnSettings.HostilitySetting hostility)
        {
            return new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.HostilityResponseCase(
                HostilityWire(hostility.Mode)
            );
        }
        if (v is PawnSettings.SelfTendSetting selfTend)
        {
            return new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.SelfTendCase(selfTend.On);
        }
        if (v is PawnSettings.MedicineCarrySetting carry)
        {
            return new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.MedicineCarryCase(carry.Count);
        }
        if (v is PawnSettings.NicknameSetting nickname)
        {
            return new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.NicknameCase(nickname.LeaveName);
        }
        if (v is PawnSettings.MedicalCareSetting care)
        {
            return new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.MedicalCareCase(MedicalCareWire(care.Care));
        }
        if (v is PawnSettings.ReadingPolicySetting reading)
        {
            return new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.ReadingPolicyCase(reading.Name);
        }
        if (v is PawnSettings.DrugPolicySetting drug)
        {
            return new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.DrugPolicyCase(drug.Name);
        }
        if (v is PawnSettings.FoodPolicySetting food)
        {
            return new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.FoodPolicyCase(food.Name);
        }
        if (v is PawnSettings.MechWorkModeSetting mode)
        {
            return new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.MechWorkModeCase(mode.Mode);
        }
        if (v is PawnSettings.MechControlGroupSetting group)
        {
            return new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.MechControlGroupCase(group.Group);
        }
        if (v is PawnSettings.ChoosePermitSetting permit)
        {
            return new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.ChoosePermitCase(
                new RimGovernor.Brain.GameOrders.Operations.PermitChoice { FactionDef = permit.Faction, Permit = permit.Permit }
            );
        }
        if (v is PawnSettings.ExtractBioferriteSetting bioferrite)
        {
            return new RimGovernor.Brain.GameOrders.Operations.PawnSettingsIntentSetting.ExtractBioferriteCase(bioferrite.On);
        }
        throw BridgeContractException.Contract("unknown pawn setting");
    }
}
