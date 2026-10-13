using DomainHostility = RimGovernor.Brain.Domain.HostilityResponse;
using DomainMedicalCare = RimGovernor.Brain.Domain.MedicalCare;
using Op = RimGovernor.Protocol.Operations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go reading_policy.go, drug_policy.go, food_policy.go, pawn_settings.go and rules.go (rulesAttachAction). The rules
// attach, clear and status calls of rules.go are wire plumbing and are not part of this file.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>readingPolicyAction</c>: the ReadingPolicyIntent of one per-pawn reading policy, the label and the allowed book definitions; native checks the definitions are books (NativeReadingPolicy.cs).</summary>
    private static Op.Action ReadingPolicyAction(DomainAction action)
    {
        var v = action.ReadingPolicy ?? throw Contract("not a reading policy action");
        return new Op.Action { ReadingPolicy = new Op.ReadingPolicyIntent { Name = v.Name, AllowedDefs = { v.Definitions } } };
    }

    /// <summary>Go's <c>foodPolicyAction</c>: the FoodPolicyIntent of one per-pawn food policy, the label and the allowed food definitions.</summary>
    private static Op.Action FoodPolicyAction(DomainAction action)
    {
        var v = action.FoodPolicy ?? throw Contract("not a food policy action");
        return new Op.Action { FoodPolicy = new Op.FoodPolicyIntent { Name = v.Name, AllowedDefs = { v.Definitions } } };
    }

    /// <summary>Go's <c>drugPolicyAction</c>: the DrugPolicyIntent of one per-pawn drug policy, the label and the entries that allow anything; native checks the drugs are the policy's (NativeDrugPolicy.cs).</summary>
    private static Op.Action DrugPolicyAction(DomainAction action)
    {
        var v = action.DrugPolicy ?? throw Contract("not a drug policy action");
        var intent = new Op.DrugPolicyIntent { Name = v.Name };
        foreach (var e in v.Entries)
        {
            intent.Entries.Add(
                new Op.DrugPolicyEntry
                {
                    DrugDef = e.Drug,
                    AllowedForJoy = e.Joy,
                    AllowedForAddiction = e.Addiction,
                    AllowScheduled = e.Scheduled,
                    DaysFrequency = (float)e.DaysFrequency,
                    OnlyIfMoodBelow = (float)e.OnlyIfMoodBelow,
                    OnlyIfJoyBelow = (float)e.OnlyIfJoyBelow,
                    TakeToInventory = e.TakeToInventory,
                }
            );
        }
        return new Op.Action { DrugPolicy = intent };
    }

    // Go hostilityWire.
    private static Op.HostilityResponse HostilityWire(DomainHostility mode) =>
        mode switch
        {
            DomainHostility.Ignore => Op.HostilityResponse.Ignore,
            DomainHostility.Attack => Op.HostilityResponse.Attack,
            DomainHostility.Flee => Op.HostilityResponse.Flee,
            _ => throw Contract("unknown hostility response"),
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
            _ => throw Contract($"unknown medical care \"{Domain.Wire.Name(care)}\""),
        };

    /// <summary>
    /// Go's <c>pawnSettingsAction</c>: the PawnSettingsIntent of one pawn and one setting. Native re-checks the pawn and
    /// the setting live; a setting that already holds applies again (NativePawnSettings.cs).
    /// </summary>
    private static Op.Action PawnSettingsAction(DomainAction action)
    {
        var v = action.PawnSettings ?? throw Contract("not a pawn settings action");
        var intent = new Op.PawnSettingsIntent { PawnId = v.Pawn.Value };
        switch (v.Kind)
        {
            case SettingKind.Hostility:
                intent.HostilityResponse = HostilityWire(v.Hostility ?? throw Contract("hostility setting without a mode"));
                break;
            case SettingKind.SelfTend:
                intent.SelfTend = v.SelfTend;
                break;
            case SettingKind.MedicineCarry:
                intent.MedicineCarry = v.MedicineCarry ?? 0;
                break;
            case SettingKind.Nickname:
                intent.Nickname = v.LeaveName;
                break;
            case SettingKind.MedicalCare:
                intent.MedicalCare = MedicalCareWire(v.MedicalCare ?? throw Contract("medical care setting without a tier"));
                break;
            case SettingKind.ReadingPolicy:
                intent.ReadingPolicy = v.ReadingPolicy ?? "";
                break;
            case SettingKind.DrugPolicy:
                intent.DrugPolicy = v.DrugPolicy ?? "";
                break;
            case SettingKind.FoodPolicy:
                intent.FoodPolicy = v.FoodPolicy ?? "";
                break;
            case SettingKind.MechWorkMode:
                intent.MechWorkMode = v.MechWorkMode ?? "";
                break;
            case SettingKind.MechControlGroup:
                intent.MechControlGroup = v.MechControlGroup ?? 0;
                break;
            case SettingKind.ChoosePermit:
                var (faction, permit) = v.ChoosePermit ?? throw Contract("choose permit setting without a permit");
                intent.ChoosePermit = new Op.PermitChoice { FactionDef = faction, Permit = permit };
                break;
            case SettingKind.ExtractBioferrite:
                intent.ExtractBioferrite = v.ExtractBioferrite ?? false;
                break;
            case SettingKind.EatTrigger:
                intent.EatTrigger = (float)(v.EatTrigger ?? 0);
                break;
            default:
                throw Contract("unknown pawn setting");
        }
        return new Op.Action { PawnSettings = intent };
    }

    private static Op.RuleTrigger? RuleTriggerWire(RuleTrigger trigger) => trigger == RuleTrigger.PreyKilled ? Op.RuleTrigger.PreyKilled : null;

    private static Op.RuleAction? RuleActionWire(RuleActionKind kind) => kind == RuleActionKind.GiveJob ? Op.RuleAction.GiveJob : null;

    private static Op.RuleTargetSelector? RuleTargetWire(RuleTarget target) =>
        target == RuleTarget.NearestDesignatedPrey ? Op.RuleTargetSelector.NearestDesignatedPrey : null;

    private static Op.RulePredicate? RulePredicateWire(RulePredicate predicate) =>
        predicate switch
        {
            RulePredicate.ActorUndrafted => Op.RulePredicate.ActorUndrafted,
            RulePredicate.ActorHuntingWorkActive => Op.RulePredicate.ActorHuntingWorkActive,
            RulePredicate.TargetAvailable => Op.RulePredicate.TargetAvailable,
            _ => null,
        };

    /// <summary>Go's <c>rulesAttachAction</c>: the RulesAttachIntent that replaces every native rule and sets the lease; the journal holds it before native is written.</summary>
    private static Op.Action RulesAttachAction(DomainAction action)
    {
        var attach = action.RulesAttach ?? throw Contract("not a rules attach action");
        var intent = new Op.RulesAttachIntent { LeaseTicks = attach.LeaseTicks };
        foreach (var rule in attach.Rules)
        {
            if (RuleTriggerWire(rule.Trigger) is not { } trigger || RuleActionWire(rule.Action) is not { } kind || RuleTargetWire(rule.Target) is not { } target)
            {
                throw Contract($"unsupported rule {rule.Id}");
            }
            var wire = new Op.Rule
            {
                Id = rule.Id,
                Trigger = trigger,
                Action = kind,
                Job = rule.Job,
                Target = target,
                Radius = rule.Radius,
                PredatorMarginCells = rule.PredatorMarginCells,
            };
            foreach (var predicate in rule.Predicates)
            {
                wire.Predicates.Add(RulePredicateWire(predicate) ?? throw Contract($"unsupported rule predicate {Domain.Wire.Name(predicate)}"));
            }
            intent.Rules.Add(wire);
        }
        return new Op.Action { RulesAttach = intent };
    }
}
