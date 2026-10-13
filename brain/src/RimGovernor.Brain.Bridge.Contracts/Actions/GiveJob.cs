using Op = RimGovernor.Protocol.Operations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go give_job.go (the GiveJobIntent builders and the draft arm) and recovery_service.go. Native checks the game
// rules live and builds the game's own job; a pawn already running it on the targets applies again.
public static partial class BridgeFunctions
{
    // Vanilla JobDef names a GiveJobIntent orders.
    public const string JobInspectGravEngine = "InspectGravEngine";
    public const string JobRepair = "Repair";
    public const string JobClean = "Clean";
    public const string JobOpen = "Open";
    public const string JobTendPatient = "TendPatient";
    public const string JobEquip = "Equip";

    /// <summary>
    /// Go's <c>JobDropWeapon</c>: this protocol's token, not a JobDef name. Native drops the weapon the pawn holds with
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

    /// <summary>Go's <c>JobInteractThing</c>: the awakening quest's interaction.</summary>
    public const string JobInteractThing = "InteractThing";

    /// <summary>Go's <c>giveJob</c>: the GiveJobIntent of one pawn, one vanilla job and its targets in job target order.</summary>
    private static Op.Action GiveJob(PawnId pawn, string job, params string[] targets) => GiveJobTo(pawn, job, false, targets);

    /// <summary>Go's <c>giveJobTo</c>: <paramref name="self"/> admits the pawn among the targets, for the one job that may target its own user (UseItem).</summary>
    private static Op.Action GiveJobTo(PawnId pawn, string job, bool self, params string[] targets)
    {
        if (!IsValidId(pawn.Value) || !IsValidId(job) || targets.Length == 0)
        {
            throw Contract("give job intent requires a pawn, a job and a target");
        }
        foreach (var target in targets)
        {
            if (!IsValidId(target) || (target == pawn.Value && !self))
            {
                throw Contract("give job intent requires valid targets distinct from the pawn");
            }
        }
        return new Op.Action
        {
            GiveJob = new Op.GiveJobIntent
            {
                Pawn = NewRef(pawn.Value),
                Job = job,
                Targets = { NewRefs(targets) },
            },
        };
    }

    /// <summary>
    /// Go's <c>PrioritizedJob</c>: the vanilla "Prioritize" order. The pawn takes <paramref name="job"/> (a JobDef name such
    /// as FinishFrame, HaulToContainer or Deconstruct) on <paramref name="target"/> as a player-forced order, built by the
    /// first work giver the pawn may do that makes exactly that job.
    /// </summary>
    public static Op.Action PrioritizedJob(PawnId pawn, string job, string target)
    {
        var output = GiveJob(pawn, job, target);
        output.GiveJob.Options = new Op.GiveJobOptions { Prioritized = true };
        return output;
    }

    /// <summary>Go's <c>draftAction</c>: drafts one plan-owned pawn.</summary>
    private static Op.Action DraftAction(DomainAction action)
    {
        if (action.OwnedDraft is not { } v || !IsValidId(v.Pawn.Value))
        {
            throw Contract("not a draft action");
        }
        return new Op.Action { Draft = new Op.DraftIntent { PawnId = v.Pawn.Value, Drafted = true } };
    }

    private static Op.Action SubdueAction(DomainAction action)
    {
        var v = action.Subdue ?? throw Contract("not a subdue action");
        return GiveJob(v.Pawn, JobAttackMelee, v.Target.Value);
    }

    private static Op.Action RepairAction(DomainAction action)
    {
        var v = action.Repair ?? throw Contract("not a repair action");
        return GiveJob(v.Pawn, JobRepair, v.Structure);
    }

    private static Op.Action CleanAction(DomainAction action)
    {
        var v = action.Clean ?? throw Contract("not a clean action");
        return GiveJob(v.Pawn, JobClean, v.Filth);
    }

    private static Op.Action OpenCasketAction(DomainAction action)
    {
        var v = action.OpenCasket ?? throw Contract("not an open casket action");
        return GiveJob(v.Pawn, JobOpen, v.Casket);
    }

    private static Op.Action TendAction(DomainAction action)
    {
        var v = action.Tend ?? throw Contract("not a tend action");
        return GiveJob(v.Doctor, JobTendPatient, v.Patient.Value);
    }

    private static Op.Action EquipAction(DomainAction action)
    {
        var v = action.Equip ?? throw Contract("not an equip action");
        return GiveJob(v.Pawn, JobEquip, v.Thing);
    }

    /// <summary>Go's <c>dropEquipmentAction</c>: orders a pawn to drop the weapon it holds; native checks the weapon is in the pawn's hands live.</summary>
    private static Op.Action DropEquipmentAction(DomainAction action)
    {
        var v = action.DropEquipment ?? throw Contract("not a drop equipment action");
        return GiveJob(v.Pawn, JobDropWeapon, v.Thing);
    }

    private static Op.Action RescueAction(DomainAction action)
    {
        var v = action.Rescue ?? throw Contract("not a rescue action");
        return GiveJob(v.Rescuer, JobRescue, v.Patient.Value);
    }

    /// <summary>
    /// Go's <c>captureAction</c>: a capture of a downed pawn, or, with a bed, an arrest of a standing one; the arrest
    /// rides the capturer's coupled owned draft.
    /// </summary>
    private static Op.Action CaptureAction(DomainAction action)
    {
        var v = action.Capture ?? throw Contract("not a capture action");
        if (!v.Arrest)
        {
            return GiveJob(v.Capturer, JobCapture, v.Patient.Value);
        }
        if (!IsValidId(v.Bed!))
        {
            throw Contract("arrest intent requires a valid bed");
        }
        return GiveJob(v.Capturer, JobArrest, v.Patient.Value, v.Bed!);
    }

    /// <summary>Go's <c>wearAction</c>: orders a pawn to wear one loose apparel item as ordered work, not a forced outfit entry.</summary>
    private static Op.Action WearAction(DomainAction action)
    {
        var v = action.GearReplace ?? throw Contract("not a gear replace action");
        return GiveJob(v.Pawn, JobWear, v.Thing);
    }

    /// <summary>
    /// Go's <c>useItemAction</c>: one colonist uses one targetable item on one pawn, or its own CompUsable item on itself
    /// (a neuroformer: the target is the colonist); native validates the verb or use comp against the target live.
    /// </summary>
    private static Op.Action UseItemAction(DomainAction action)
    {
        var v = action.UseItem ?? throw Contract("not a use item action");
        if (v.Item == v.Target.Value)
        {
            throw Contract("use item intent requires a distinct item and target");
        }
        return GiveJobTo(v.Pawn, JobUseItem, v.SelfUse, v.Item, v.Target.Value);
    }

    /// <summary>
    /// Go's <c>moodReliefAction</c>: offers the pawn the job its own need giver issues for one deficient need, checked
    /// live; the giver picks the target.
    /// </summary>
    private static Op.Action MoodReliefAction(DomainAction action)
    {
        var v = action.MoodRelief ?? throw Contract("not a mood relief action");
        var need = v.Need switch
        {
            MoodReliefNeed.Food => Op.Need.Food,
            MoodReliefNeed.Rest => Op.Need.Rest,
            MoodReliefNeed.Joy => Op.Need.Joy,
            _ => throw Contract("invalid mood relief need"),
        };
        if (!IsValidId(v.Pawn.Value))
        {
            throw Contract("mood relief intent requires a valid pawn");
        }
        return new Op.Action
        {
            GiveJob = new Op.GiveJobIntent { Pawn = NewRef(v.Pawn.Value), Options = new Op.GiveJobOptions { RelieveNeed = need } },
        };
    }

    // Go recoveryServiceJobs.
    private static string RecoveryServiceJob(RecoveryMethod method) =>
        method switch
        {
            RecoveryMethod.OfferHelp => "OfferHelp",
            RecoveryMethod.InspectGravEngine => JobInspectGravEngine,
            RecoveryMethod.Repair => JobRepair,
            RecoveryMethod.Breakdown => JobFixBrokenDownBuilding,
            RecoveryMethod.Refuel => JobRefuel,
            RecoveryMethod.InvestigateMonolith => JobInvestigateMonolith,
            RecoveryMethod.ActivateMonolith => JobActivateMonolith,
            RecoveryMethod.Interact => JobInteractThing,
            _ => throw Contract("recover intent requires a service method"),
        };

    /// <summary>
    /// Go's <c>recoverAction</c>: the GiveJobIntent of one pawn servicing one colony building (repair, breakdown,
    /// refuel). Native checks the pawn, the building and whether it still needs the service live, and the game's own
    /// WorkGiver builds the job.
    /// </summary>
    private static Op.Action RecoverAction(DomainAction action)
    {
        var v = action.RecoveryService ?? throw Contract("not a recovery service action");
        return GiveJob(v.Pawn, RecoveryServiceJob(v.Method), v.Thing);
    }
}
