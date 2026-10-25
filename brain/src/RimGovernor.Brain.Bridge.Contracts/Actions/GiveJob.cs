using Op = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Native checks the game
// rules live and builds the game's own job; a pawn already running it on the targets applies again.
public static class GiveJobRules
{
    // Vanilla JobDef names a GiveJobIntent orders.
    public const string JobInspectGravEngine = "InspectGravEngine";
    public const string JobRepair = "Repair";
    public const string JobTendPatient = "TendPatient";
    public const string JobEquip = "Equip";

    /// <summary>
    /// This protocol's token, not a JobDef name. Native drops the weapon the pawn holds with
    /// the game's own drop job, which it finds by its driver class.
    /// </summary>
    public const string JobDropWeapon = "DropWeapon";

    public const string JobWear = "Wear";
    public const string JobRescue = "Rescue";
    public const string JobCapture = "Capture";
    public const string JobArrest = "Arrest";
    public const string JobAttackMelee = "AttackMelee";
    public const string JobUseItem = "UseItem";

    // Recovery service and waste hauling.
    public const string JobFixBrokenDownBuilding = "FixBrokenDownBuilding";
    public const string JobRefuel = "Refuel";

    // The void monolith's orders: the Inactive monolith is investigated, every later level activated.
    public const string JobInvestigateMonolith = "InvestigateMonolith";
    public const string JobActivateMonolith = "ActivateMonolith";

    /// <summary>The awakening quest's interaction.</summary>
    public const string JobInteractThing = "InteractThing";

    /// <summary>The GiveJobIntent of one pawn, one vanilla job and its targets in job target order.</summary>
    private static Op.Action GiveJob(PawnId pawn, string job, params string[] targets) => GiveJobTo(pawn, job, false, targets);

    /// <summary><paramref name="self"/> admits the pawn among the targets, for the one job that may target its own user (UseItem).</summary>
    private static Op.Action GiveJobTo(PawnId pawn, string job, bool self, params string[] targets)
    {
        if (targets.Length == 0)
        {
            throw BridgeContractException.Contract("give job intent requires a target");
        }
        foreach (var target in targets)
        {
            if (target == pawn.Value && !self)
            {
                throw BridgeContractException.Contract("give job intent requires targets distinct from the pawn");
            }
        }
        return new Op.Action
        {
            Intent = new Op.GiveJobIntent
            {
                Pawn = IntentActionRules.NewRef(pawn.Value),
                Job = job,
                Targets = [.. IntentActionRules.NewRefs(targets)],
            },
        };
    }

    /// <summary>
    /// The vanilla "Prioritize" order. The pawn takes <paramref name="job"/> (a JobDef name such
    /// as FinishFrame, HaulToContainer or Deconstruct) on <paramref name="target"/> as a player-forced order, built by the
    /// first work giver the pawn may do that makes exactly that job.
    /// </summary>
    public static Op.Action PrioritizedJob(PawnId pawn, string job, string target)
    {
        var output = GiveJob(pawn, job, target);
        return output with { Intent = (output.Intent?.GiveJob ?? new Op.GiveJobIntent()) with { Options = new Op.GiveJobOptions { Prioritized = true } } };
    }

    /// <summary>Drafts one plan-owned pawn.</summary>
    internal static Op.Action DraftAction(DomainAction action)
    {
        if (action.OwnedDraft is not { } v)
        {
            throw BridgeContractException.Contract("not a draft action");
        }
        return new Op.Action { Intent = new Op.DraftIntent { PawnId = v.Pawn.Value, Drafted = true } };
    }

    internal static Op.Action SubdueAction(DomainAction action)
    {
        var v = action.Subdue ?? throw BridgeContractException.Contract("not a subdue action");
        return GiveJob(v.Pawn, JobAttackMelee, v.Target.Value);
    }

    internal static Op.Action RepairAction(DomainAction action)
    {
        var v = action.Repair ?? throw BridgeContractException.Contract("not a repair action");
        return GiveJob(v.Pawn, JobRepair, v.Structure);
    }

    internal static Op.Action TendAction(DomainAction action)
    {
        var v = action.Tend ?? throw BridgeContractException.Contract("not a tend action");
        return GiveJob(v.Doctor, JobTendPatient, v.Patient.Value);
    }

    internal static Op.Action EquipAction(DomainAction action)
    {
        var v = action.Equip ?? throw BridgeContractException.Contract("not an equip action");
        return GiveJob(v.Pawn, JobEquip, v.Thing);
    }

    /// <summary>Orders a pawn to drop the weapon it holds; native checks the weapon is in the pawn's hands live.</summary>
    internal static Op.Action DropEquipmentAction(DomainAction action)
    {
        var v = action.DropEquipment ?? throw BridgeContractException.Contract("not a drop equipment action");
        return GiveJob(v.Pawn, JobDropWeapon, v.Thing);
    }

    internal static Op.Action RescueAction(DomainAction action)
    {
        var v = action.Rescue ?? throw BridgeContractException.Contract("not a rescue action");
        return GiveJob(v.Rescuer, JobRescue, v.Patient.Value);
    }

    /// <summary>
    /// A capture of a downed pawn, or, with a bed, an arrest of a standing one; the arrest
    /// rides the capturer's coupled owned draft.
    /// </summary>
    internal static Op.Action CaptureAction(DomainAction action)
    {
        var v = action.Capture ?? throw BridgeContractException.Contract("not a capture action");
        if (!v.Arrest)
        {
            return GiveJob(v.Capturer, JobCapture, v.Patient.Value);
        }
        if (v.Bed is not { } bed)
        {
            throw BridgeContractException.Contract("arrest intent requires a bed");
        }
        return GiveJob(v.Capturer, JobArrest, v.Patient.Value, bed);
    }

    /// <summary>Orders a pawn to wear one loose apparel item as ordered work, not a forced outfit entry.</summary>
    internal static Op.Action WearAction(DomainAction action)
    {
        var v = action.GearReplace ?? throw BridgeContractException.Contract("not a gear replace action");
        return GiveJob(v.Pawn, JobWear, v.Thing);
    }

    /// <summary>
    /// One colonist uses one targetable item on one pawn, or its own CompUsable item on itself
    /// (a neuroformer: the target is the colonist); native validates the verb or use comp against the target live.
    /// </summary>
    internal static Op.Action UseItemAction(DomainAction action)
    {
        var v = action.UseItem ?? throw BridgeContractException.Contract("not a use item action");
        if (v.Item == v.Target.Value)
        {
            throw BridgeContractException.Contract("use item intent requires a distinct item and target");
        }
        return GiveJobTo(v.Pawn, JobUseItem, v.SelfUse, v.Item, v.Target.Value);
    }

    private static string RecoveryServiceJob(RecoveryMethod method) =>
        method == RecoveryMethod.OfferHelp ? "OfferHelp"
        : method == RecoveryMethod.InspectGravEngine ? JobInspectGravEngine
        : method == RecoveryMethod.Repair ? JobRepair
        : method == RecoveryMethod.Breakdown ? JobFixBrokenDownBuilding
        : method == RecoveryMethod.Refuel ? JobRefuel
        : method == RecoveryMethod.InvestigateMonolith ? JobInvestigateMonolith
        : method == RecoveryMethod.ActivateMonolith ? JobActivateMonolith
        : method == RecoveryMethod.Interact ? JobInteractThing
        : throw BridgeContractException.Contract("recover intent requires a service method");

    /// <summary>
    /// The GiveJobIntent of one pawn servicing one colony building (repair, breakdown,
    /// refuel). Native checks the pawn, the building and whether it still needs the service live, and the game's own
    /// WorkGiver builds the job.
    /// </summary>
    internal static Op.Action RecoverAction(DomainAction action)
    {
        var v = action.RecoveryService ?? throw BridgeContractException.Contract("not a recovery service action");
        return GiveJob(v.Pawn, RecoveryServiceJob(v.Method), v.Thing);
    }
}
