using DomainCorpseOf = RimGovernor.Brain.Domain.CorpseOf;

namespace RimGovernor.Brain.GameAccess;

// The intents that set a pawn's work and
// queue bills. Native checks the pawn, the bench, the recipe and the worker live when it applies.
public static class Production
{
    /// <summary>The native refusal reason of a production bill whose bench already carries the vanilla 15 bills; the dispatcher reads it as a full bench.</summary>
    public const string BillSlotsFull = "bench_bill_slots_full";

    /// <summary>Whether a refused action result names a full bill stack.</summary>
    public static bool RefusedBillSlotsFull(RimGovernor.Brain.GameFacts.Operations.Refusal? refusal) => refusal is not null && (refusal.Reason ?? "").Contains(BillSlotsFull, StringComparison.Ordinal);

    /// <summary>
    /// The WorkSettingsIntent of one pawn's settings write, any of work priorities,
    /// allowed area, timetable and food additions together. Native checks the pawn and each field live when it
    /// applies; settings that already hold apply again.
    /// </summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action WorkSettingsAction(DomainAction action)
    {
        var w = action.WorkAssignment ?? throw BridgeContractException.Contract("not a work assignment action");
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = WorkSettingsIntent(w) };
    }

    private static RimGovernor.Brain.GameOrders.Operations.WorkSettingsIntent WorkSettingsIntent(WorkAssignment w)
    {
        var intent = new RimGovernor.Brain.GameOrders.Operations.WorkSettingsIntent { PawnId = w.Pawn.Value };
        foreach (var setting in w.Settings)
        {
            intent = intent with { Work = [.. intent.Work, new RimGovernor.Brain.GameOrders.Operations.WorkPriority { WorkTypeDef = setting.Definition, Priority = setting.Priority }] };
        }
        if (w.HasArea)
        {
            intent = intent with { AllowedArea = w.AreaClear ? new RimGovernor.Brain.GameOrders.Operations.Assignment { Value = new RimGovernor.Brain.GameOrders.Operations.AssignmentValue.ClearCase() } : new RimGovernor.Brain.GameOrders.Operations.Assignment { Value = new RimGovernor.Brain.GameOrders.Operations.AssignmentValue.EntityIdCase(w.Area) } };
        }
        if (w.HasSchedule)
        {
            intent = intent with { Schedule = new RimGovernor.Brain.GameOrders.Operations.Schedule { AssignmentDefs = [.. w.Schedule] } };
        }
        return intent;
    }

    /// <summary>One production bill as a ProductionBillIntent; native checks the bench, recipe and a skilled worker when it applies.</summary>
    internal static RimGovernor.Brain.GameOrders.Operations.Action ProductionBillAction(DomainAction action) =>
        new() { Intent = BillIntent(action.ProductionBill ?? throw BridgeContractException.Contract("not a production bill action")) };

    private static RimGovernor.Brain.GameOrders.Operations.ProductionBillIntent BillIntent(ProductionBill bill)
    {
        var settings = new RimGovernor.Brain.GameOrders.Operations.BillSettings
        {
            Repetition = bill.Mode switch
            {
                BillMode.ButcherForever or BillMode.HumanButcherForever => new RimGovernor.Brain.GameOrders.Operations.BillRepetition.Forever(),
                BillMode.GearBatch => new RimGovernor.Brain.GameOrders.Operations.BillRepetition.Count(bill.Target),
                BillMode.BeerReserve or BillMode.FoodTarget or BillMode.StockTarget => new RimGovernor.Brain.GameOrders.Operations.BillRepetition.Target(bill.Target, Math.Max(1, bill.Target / 2)),
                var invalid => throw BridgeContractException.Contract($"unsupported production bill mode {invalid}"),
            },
            BeerReserve = bill.Mode == BillMode.BeerReserve,
            Corpses = bill.Corpses == DomainCorpseOf.None ? new RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.NotApplicable() : new RimGovernor.Brain.GameOrders.Operations.BillCorpseClass.Selected(CorpseClassRules.WireCorpseClass(bill.Corpses)),
        };
        if (bill.Ingredients.Count > 0)
        {
            var selectors = new RimGovernor.Brain.GameOrders.Operations.SelectorList();
            foreach (var name in bill.Ingredients)
            {
                selectors = selectors with { Selectors = [.. selectors.Selectors, new RimGovernor.Brain.GameOrders.Operations.FilterSelector { Definition = new RimGovernor.Brain.GameOrders.Operations.FilterSelectorDefinition.ThingDefCase(name) }] };
            }
            settings = settings with { Ingredients = new RimGovernor.Brain.GameOrders.Operations.FilterPatch { Replace = selectors, HitPoints = new RimGovernor.Brain.GameOrders.Operations.HitPointRange.Preserve() } };
        }
        if (!string.IsNullOrEmpty(bill.Worker))
        {
            settings = settings with { Worker = new RimGovernor.Brain.GameOrders.Operations.Assignment { Value = new RimGovernor.Brain.GameOrders.Operations.AssignmentValue.EntityIdCase(bill.Worker) } };
        }
        var intent = new RimGovernor.Brain.GameOrders.Operations.ProductionBillIntent { BenchId = bill.Bench, RecipeDef = bill.Recipe, Settings = settings, AcknowledgeViolation = false, PartIndex = new RimGovernor.Brain.GameOrders.Operations.SurgeryTarget.WholeBody() };
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
    internal static RimGovernor.Brain.GameOrders.Operations.Action RemoveProductionBillAction(DomainAction action)
    {
        var removal = action.RemoveProductionBill ?? throw BridgeContractException.Contract("not a remove_production_bill action");
        return new RimGovernor.Brain.GameOrders.Operations.Action
        {
            Intent = new RimGovernor.Brain.GameOrders.Operations.RemoveProductionBillIntent
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
    internal static RimGovernor.Brain.GameOrders.Operations.Action SurgeryAction(DomainAction action)
    {
        var v = action.Surgery ?? throw BridgeContractException.Contract("not a surgery action");
        var intent = new RimGovernor.Brain.GameOrders.Operations.ProductionBillIntent
        {
            Patient = IntentActionRules.NewRef(v.Pawn.Value),
            RecipeDef = v.Recipe,
            AcknowledgeViolation = v.AcknowledgeViolation, PartIndex = new RimGovernor.Brain.GameOrders.Operations.SurgeryTarget.WholeBody() };
        if (v.Part != SurgeryConstants.NoSurgeryPart)
        {
            intent = intent with { PartIndex = new RimGovernor.Brain.GameOrders.Operations.SurgeryTarget.Part(v.Part) };
        }
        return new RimGovernor.Brain.GameOrders.Operations.Action { Intent = intent };
    }
}
