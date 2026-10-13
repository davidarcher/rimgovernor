using DomainCorpseOf = RimGovernor.Brain.Domain.CorpseOf;
using Op = RimGovernor.Protocol.Operations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go work_assignment.go, bill.go, remove_production_bill.go and surgery.go: the intents that set a pawn's work and
// queue bills. Native checks the pawn, the bench, the recipe and the worker live when it applies.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>BillSlotsFull</c>: the native refusal reason of a production bill whose bench already carries the vanilla 15 bills; the dispatcher reads it as a full bench.</summary>
    public const string BillSlotsFull = "bench_bill_slots_full";

    /// <summary>Go's <c>RefusedBillSlotsFull</c>: whether a refused action result names a full bill stack.</summary>
    public static bool RefusedBillSlotsFull(Op.Refusal? refusal) => refusal is not null && refusal.Reason.Contains(BillSlotsFull, StringComparison.Ordinal);

    /// <summary>
    /// Go's <c>workSettingsAction</c>: the WorkSettingsIntent of one pawn's settings write, any of work priorities,
    /// allowed area, timetable and food additions together. Native checks the pawn and each field live when it
    /// applies; settings that already hold apply again.
    /// </summary>
    private static Op.Action WorkSettingsAction(DomainAction action)
    {
        var w = action.WorkAssignment ?? throw Contract("not a work assignment action");
        return new Op.Action { WorkSettings = WorkSettingsIntent(w) };
    }

    private static Op.WorkSettingsIntent WorkSettingsIntent(WorkAssignment w)
    {
        var intent = new Op.WorkSettingsIntent { PawnId = w.Pawn.Value };
        foreach (var setting in w.Settings)
        {
            intent.Work.Add(new Op.WorkPriority { WorkTypeDef = setting.Definition, Priority = setting.Priority });
        }
        if (w.HasArea)
        {
            intent.AllowedArea = w.AreaClear ? new Op.Assignment { Clear = new Op.Clear() } : new Op.Assignment { EntityId = w.Area };
        }
        if (w.HasSchedule)
        {
            intent.Schedule = new Op.Schedule { AssignmentDefs = { w.Schedule } };
        }
        return intent;
    }

    /// <summary>Go's <c>productionBillAction</c>: one production bill as a ProductionBillIntent; native checks the bench, recipe and a skilled worker when it applies.</summary>
    private static Op.Action ProductionBillAction(DomainAction action) =>
        new() { ProductionBill = BillIntent(action.ProductionBill ?? throw Contract("not a production bill action")) };

    private static Op.ProductionBillIntent BillIntent(ProductionBill bill)
    {
        var settings = new Op.BillSettings
        {
            Suspended = false,
            IngredientSearchRadius = 40,
            Store = new Op.BillStore { Mode = Op.StoreMode.DropOnFloor },
        };
        if (bill.Mode == BillMode.BeerReserve)
        {
            settings.BeerReserve = true;
        }
        if (bill.Ingredients.Count > 0)
        {
            var selectors = new Op.SelectorList();
            foreach (var name in bill.Ingredients)
            {
                selectors.Selectors.Add(new Op.FilterSelector { ThingDef = name });
            }
            settings.Ingredients = new Op.FilterPatch { Replace = selectors };
        }
        if (bill.Corpses is { } corpses && corpses != DomainCorpseOf.None)
        {
            settings.CorpseClass = WireCorpseClass(corpses);
        }
        if (!string.IsNullOrEmpty(bill.Worker))
        {
            settings.Worker = new Op.Assignment { EntityId = bill.Worker };
        }
        if (bill.Mode is BillMode.ButcherForever or BillMode.HumanButcherForever)
        {
            settings.RepeatMode = Op.RepeatMode.Forever;
        }
        else if (bill.Mode == BillMode.GearBatch)
        {
            settings.RepeatMode = Op.RepeatMode.Count;
            settings.RepeatCount = bill.Target;
        }
        else
        {
            settings.RepeatMode = Op.RepeatMode.Target;
            settings.TargetCount = bill.Target;
            settings.UnpauseThreshold = Math.Max(1, bill.Target / 2);
            settings.PauseWhenSatisfied = true;
        }
        var intent = new Op.ProductionBillIntent { BenchId = bill.Bench, RecipeDef = bill.Recipe, Settings = settings };
        if (!string.IsNullOrEmpty(bill.Replaces))
        {
            intent.ReplaceOwnedBill = NewRef(bill.Replaces);
        }
        return intent;
    }

    /// <summary>
    /// Go's <c>removeProductionBillAction</c>: the Actions/Apply remove_production_bill arm of one
    /// remove_production_bill action; native deletes the bill (and, when asked, cancels its unfinished item) or refuses.
    /// </summary>
    private static Op.Action RemoveProductionBillAction(DomainAction action)
    {
        var removal = action.RemoveProductionBill ?? throw Contract("not a remove_production_bill action");
        return new Op.Action
        {
            RemoveProductionBill = new Op.RemoveProductionBillIntent
            {
                BenchId = removal.Bench,
                Bill = NewRef(removal.Bill),
                CancelUnfinished = removal.CancelsUnfinished,
            },
        };
    }

    /// <summary>
    /// Go's <c>surgeryAction</c>: the medical ProductionBillIntent of one patient, one recipe and one body part. Native
    /// re-checks the patient, the recipe, the part and a violation's acknowledgment live, and queues the vanilla
    /// Bill_Medical; the same recipe and part already queued applies again (NativeSurgery.cs).
    /// </summary>
    private static Op.Action SurgeryAction(DomainAction action)
    {
        var v = action.Surgery ?? throw Contract("not a surgery action");
        var intent = new Op.ProductionBillIntent
        {
            Patient = NewRef(v.Pawn.Value),
            RecipeDef = v.Recipe,
            AcknowledgeViolation = v.AcknowledgeViolation,
        };
        if (v.Part != DomainConstants.NoSurgeryPart)
        {
            intent.PartIndex = v.Part;
        }
        if (v.Surgeon is { } surgeon)
        {
            intent.Surgeon = NewRef(surgeon.Value);
        }
        return new Op.Action { ProductionBill = intent };
    }
}
