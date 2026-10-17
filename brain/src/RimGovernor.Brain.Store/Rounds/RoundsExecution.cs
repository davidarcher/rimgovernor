using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>
    /// The action kinds routine execution dispatches (the case list of Go <c>roundsActionsSupported</c>).
    /// </summary>
    private static readonly HashSet<ActionKind> RoundsSupportedKinds = new HashSet<ActionKind>
    {
        ActionKind.CombatBatch,
        ActionKind.Building,
        ActionKind.SupplyAllow,
        ActionKind.SupplyForbid,
        ActionKind.WorkAssignment,
        ActionKind.PawnActivities,
        ActionKind.Acquisition,
        ActionKind.AcquisitionWithdraw,
        ActionKind.ZoneCreate,
        ActionKind.ProductionBill,
        ActionKind.OwnedDraft,
        ActionKind.Subdue,
        ActionKind.Tend,
        ActionKind.Rescue,
        ActionKind.Capture,
        ActionKind.UseItem,
        ActionKind.Haul,
        ActionKind.Equip,
        ActionKind.DropEquipment,
        ActionKind.GearReplace,
        ActionKind.ApparelPolicy,
        ActionKind.RecoveryService,
        ActionKind.Movement,
        ActionKind.Husbandry,
        ActionKind.PrisonerInteraction,
        ActionKind.Repair,
        ActionKind.Clean,
        ActionKind.MineAcquisition,
        ActionKind.BuildingTemperature,
        ActionKind.BedUse,
        ActionKind.GrowerCrop,
        ActionKind.ClaimBuilding,
        ActionKind.AutoRefuel,
        ActionKind.Surgery,
        ActionKind.AutoHomeArea,
        ActionKind.PawnSettings,
        ActionKind.Area,
        ActionKind.PolicyPrune,
        ActionKind.ReadingPolicy,
        ActionKind.DrugPolicy,
        ActionKind.FoodPolicy,
        ActionKind.ZoneDelete,
        ActionKind.ZoneCellEdit,
        ActionKind.StockpilePatch,
        ActionKind.Assign,
        ActionKind.Excavation,
        ActionKind.DialogAnswer,
        ActionKind.ResearchSelect,
        ActionKind.Trade,
        ActionKind.CommsTradeRequest,
        ActionKind.Deconstruction,
        ActionKind.RemoveRoof,
        ActionKind.BuildRoof,
        ActionKind.AreaPlantCut,
        ActionKind.CutPlant,
        ActionKind.Strip,
        ActionKind.MoveBuilding,
        ActionKind.UninstallBuilding,
        ActionKind.CoverClearance,
        ActionKind.WastepackHaul,
        ActionKind.FoundationRemoval,
        ActionKind.FloorRemoval,
        ActionKind.QuestAccept,
        ActionKind.QuestShuttle,
        ActionKind.HackDesignation,
        ActionKind.GiveItem,
        ActionKind.Ritual,
        ActionKind.Ability,
        ActionKind.Ignite,
        ActionKind.Gathering,
        ActionKind.RemoveProductionBill,
        ActionKind.DoorControl,
        ActionKind.WallRemoval,
        ActionKind.CaravanDeparture,
    };

    /// <summary>
    /// Compares world identity and root plan revision across review and worker authority. Native generation is
    /// excluded because authority toggles can change it without changing the world; dispatch separately validates
    /// the active grant.
    /// </summary>
    internal static bool SameRoot(GenerationSnapshot recorded, GenerationSnapshot root) =>
        recorded.SameWorld(root) && recorded.Plan == root.Plan && recorded.Revision == root.Revision;

    /// <summary>
    /// Verifies a method under the existing player direction. It grants no lease and never changes the selected
    /// player plan. Any refusal is <see cref="ConflictException"/> (a Safeguard veto is <see cref="NotAdmittedException"/>).
    /// </summary>
    public void AuthorizeRoundsPlan(GenerationSnapshot root, GenerationSnapshot target, CancellationToken cancellationToken)
    {
        if (!root.IsValid || !target.IsValid || root.Native.Value == 0 || root.Plan == target.Plan)
        {
            throw new ConflictException("invalid rounds authority");
        }
        var matching = target with { Plan = root.Plan, Revision = root.Revision };
        if (matching != root)
        {
            throw new ConflictException("target does not share the root's world");
        }
        Run(
            tx =>
            {
                var review = LoadRounds(tx);
                if (!review.Enabled || !SameRoot(review.Snapshot, root))
                {
                    throw new ConflictException("no current enabled review");
                }
                if (tx.ScalarText("SELECT need_id FROM need_methods WHERE plan_id=$plan", ("$plan", target.Plan)) is null)
                {
                    throw new NotFoundException();
                }
                AuthorizeNeedPlan(tx, review, target.Plan, root, target);
            },
            cancellationToken
        );
    }

    public Task AuthorizeRoundsPlanAsync(GenerationSnapshot root, GenerationSnapshot target, CancellationToken cancellationToken) =>
        Task.Run(() => AuthorizeRoundsPlan(root, target, cancellationToken), cancellationToken);

    /// <summary>
    /// <see cref="AuthorizeRoundsPlan"/> for a Need's method (#3250): the plan belongs to the root's world, no Safeguard
    /// vetoes routine work and the plan is unretired at the target revision. Whether the Need is still unmet is its
    /// family's to judge on its next run; the caller has already required a current enabled review under the root.
    /// A <c>military/combat</c> plan other than the settings restoration dispatches only while the review's combat
    /// standing is active, as the combat incident's did (#3311): nothing dispatches after the clear.
    /// </summary>
    internal static void AuthorizeNeedPlan(StoreTransaction tx, Rounds review, PlanId plan, GenerationSnapshot root, GenerationSnapshot target)
    {
        GuardNeedWork(tx, plan, root);
        var p = LoadIn(tx, plan);
        if (p.Retired || p.Spec.Revision != target.Revision)
        {
            throw new ConflictException("plan is retired or revised");
        }
        if (tx.ScalarText("SELECT need_id FROM need_methods WHERE plan_id=$plan", ("$plan", plan)) == MilitaryFamily.Combat.Value
            && review.Combat is not { Situation: Situation.Active } && !CombatRestorationPlan(tx, p.Spec))
        {
            throw new ConflictException("combat is not in deficit");
        }
        RoundsActionsSupported(p.Spec);
    }

    /// <summary>Refuses a plan with an action kind routine execution does not dispatch.</summary>
    internal static void RoundsActionsSupported(PlanSpec spec)
    {
        foreach (var action in spec.Actions)
        {
            DomainException.Require(RoundsSupportedKinds.Contains(action.Kind), "routine execution requires supported routine methods");
        }
    }

    /// <summary>
    /// Asks the action Safeguards at dispatch: a vetoed action is <see cref="ActionVetoedException"/> with the
    /// Safeguard's reason and only that action is refused.
    /// </summary>
    internal static void VetoAction(StoreTransaction tx, DomainAction a)
    {
        var review = LoadRounds(tx);
        var (refusal, vetoed) = SafeguardsRules.RefuseAction(new SafeguardContext { Enabled = review.Enabled, Unsafe = review.Unsafe }, a);
        if (vetoed)
        {
            throw new ActionVetoedException($"action {a.Id.Value}: {refusal.Safeguard}: {refusal.Reason}");
        }
    }
}
