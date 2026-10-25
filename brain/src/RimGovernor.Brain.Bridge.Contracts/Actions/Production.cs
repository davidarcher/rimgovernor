using DomainCorpseOf = RimGovernor.Brain.Domain.CorpseOf;
using Op = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Bridge.Contracts;

// The intents that set a pawn's work and
// queue bills. Native checks the pawn, the bench, the recipe and the worker live when it applies.
public static class Production
{
    /// <summary>The native refusal reason of a production bill whose bench already carries the vanilla 15 bills; the dispatcher reads it as a full bench.</summary>
    public const string BillSlotsFull = "bench_bill_slots_full";

    /// <summary>Whether a refused action result names a full bill stack.</summary>
    public static bool RefusedBillSlotsFull(Op.Refusal? refusal) => refusal is not null && (refusal.Reason ?? "").Contains(BillSlotsFull, StringComparison.Ordinal);

    /// <summary>
    /// The WorkSettingsIntent of one pawn's settings write, any of work priorities,
    /// allowed area, timetable and food additions together. Native checks the pawn and each field live when it
    /// applies; settings that already hold apply again.
    /// </summary>
    internal static Op.Action WorkSettingsAction(DomainAction action)
    {
        var w = action.WorkAssignment ?? throw BridgeContractException.Contract("not a work assignment action");
        return new Op.Action { Intent = WorkSettingsIntent(w) };
    }

    private static Op.WorkSettingsIntent WorkSettingsIntent(WorkAssignment w)
    {
        var intent = new Op.WorkSettingsIntent { PawnId = w.Pawn.Value };
        foreach (var setting in w.Settings)
        {
            intent = intent with { Work = [.. intent.Work, new Op.WorkPriority { WorkTypeDef = setting.Definition, Priority = setting.Priority }] };
        }
        if (w.HasArea)
        {
            intent = intent with { AllowedArea = w.AreaClear ? new Op.Assignment { Value = new Op.AssignmentValue.ClearCase() } : new Op.Assignment { Value = new Op.AssignmentValue.EntityIdCase(w.Area) } };
        }
        if (w.HasSchedule)
        {
            intent = intent with { Schedule = new Op.Schedule { AssignmentDefs = [.. w.Schedule] } };
        }
        return intent;
    }

    /// <summary>One production bill as a ProductionBillIntent; native checks the bench, recipe and a skilled worker when it applies.</summary>
    internal static Op.Action ProductionBillAction(DomainAction action) =>
        new() { Intent = BillIntent(action.ProductionBill ?? throw BridgeContractException.Contract("not a production bill action")) };

    private static Op.ProductionBillIntent BillIntent(ProductionBill bill)
    {
        var settings = new Op.BillSettings
        {
            Repetition = bill.Mode switch
            {
                BillMode.ButcherForever or BillMode.HumanButcherForever => new Op.BillRepetition.Forever(),
                BillMode.GearBatch => new Op.BillRepetition.Count(bill.Target),
                BillMode.BeerReserve or BillMode.FoodTarget or BillMode.StockTarget => new Op.BillRepetition.Target(bill.Target, Math.Max(1, bill.Target / 2)),
                var invalid => throw BridgeContractException.Contract($"unsupported production bill mode {invalid}"),
            },
            BeerReserve = bill.Mode == BillMode.BeerReserve,
            Corpses = bill.Corpses == DomainCorpseOf.None ? new Op.BillCorpseClass.NotApplicable() : new Op.BillCorpseClass.Selected(CorpseClassRules.WireCorpseClass(bill.Corpses)),
        };
        if (bill.Ingredients.Count > 0)
        {
            var selectors = new Op.SelectorList();
            foreach (var name in bill.Ingredients)
            {
                selectors = selectors with { Selectors = [.. selectors.Selectors, new Op.FilterSelector { Definition = new Op.FilterSelectorDefinition.ThingDefCase(name) }] };
            }
            settings = settings with { Ingredients = new Op.FilterPatch { Replace = selectors, HitPoints = new Op.HitPointRange.Preserve() } };
        }
        if (!string.IsNullOrEmpty(bill.Worker))
        {
            settings = settings with { Worker = new Op.Assignment { Value = new Op.AssignmentValue.EntityIdCase(bill.Worker) } };
        }
        var intent = new Op.ProductionBillIntent { BenchId = bill.Bench, RecipeDef = bill.Recipe, Settings = settings, AcknowledgeViolation = false, PartIndex = new Op.SurgeryTarget.WholeBody() };
        if (!string.IsNullOrEmpty(bill.Replaces))
        {
            intent = intent with { ReplaceOwnedBill = IntentActionRules.NewRef(bill.Replaces) };
        }
        return intent;
    }

    /// <summary>
    /// The Actions/Apply remove_production_bill arm of one
    /// remove_production_bill action; native deletes the bill (and, when asked, cancels its unfinished item) or refuses.
    /// </summary>
    internal static Op.Action RemoveProductionBillAction(DomainAction action)
    {
        var removal = action.RemoveProductionBill ?? throw BridgeContractException.Contract("not a remove_production_bill action");
        return new Op.Action
        {
            Intent = new Op.RemoveProductionBillIntent
            {
                BenchId = removal.Bench,
                Bill = IntentActionRules.NewRef(removal.Bill),
                CancelUnfinished = removal.CancelsUnfinished,
            },
        };
    }

    /// <summary>
    /// The medical ProductionBillIntent of one patient, one recipe and one body part. Native
    /// re-checks the patient, the recipe, the part and a violation's acknowledgment live, and queues the vanilla
    /// Bill_Medical; the same recipe and part already queued applies again (NativeSurgery.cs).
    /// </summary>
    internal static Op.Action SurgeryAction(DomainAction action)
    {
        var v = action.Surgery ?? throw BridgeContractException.Contract("not a surgery action");
        var intent = new Op.ProductionBillIntent
        {
            Patient = IntentActionRules.NewRef(v.Pawn.Value),
            RecipeDef = v.Recipe,
            AcknowledgeViolation = v.AcknowledgeViolation, PartIndex = new Op.SurgeryTarget.WholeBody() };
        if (v.Part != SurgeryConstants.NoSurgeryPart)
        {
            intent = intent with { PartIndex = new Op.SurgeryTarget.Part(v.Part) };
        }
        return new Op.Action { Intent = intent };
    }
}
