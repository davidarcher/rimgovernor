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
        ActionKind.OpenCasket,
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
        ActionKind.RulesAttach,
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
                var (owner, found) = tx.QueryFirst("SELECT kind,owner_id FROM plan_methods WHERE plan_id=$plan", [("$plan", target.Plan)], r => (Kind: r.GetString(0), OwnerId: r.GetString(1)));
                if (!found)
                {
                    throw new NotFoundException();
                }
                if (owner.Kind == "incident")
                {
                    AuthorizeIncidentPlan(tx, review, new IncidentId(owner.OwnerId), root, target);
                    return;
                }
                AuthorizeStandardPlan(tx, review, LoadStandard(tx, new StandardId(owner.OwnerId)), root, target);
            },
            cancellationToken
        );
    }

    public Task AuthorizeRoundsPlanAsync(GenerationSnapshot root, GenerationSnapshot target, CancellationToken cancellationToken) =>
        Task.Run(() => AuthorizeRoundsPlan(root, target, cancellationToken), cancellationToken);

    /// <summary>
    /// <see cref="AuthorizeRoundsPlan"/> for an incident's method: the review binds the open occurrence in deficit,
    /// no Safeguard vetoes it and the plan is its unretired method.
    /// </summary>
    internal static void AuthorizeIncidentPlan(StoreTransaction tx, Rounds review, IncidentId id, GenerationSnapshot root, GenerationSnapshot target)
    {
        var binding = review.IncidentBinding(id);
        var state = LoadIncident(tx, id);
        if (state.Incident.Closed || !SameRoot(state.Incident.Snapshot, root))
        {
            throw new ConflictException("incident is closed or belongs to another root");
        }
        var reason = review.VetoIncident(state.Incident);
        if (reason.Length != 0)
        {
            throw new NotAdmittedException($"incident {id.Value}: {reason}");
        }
        if (!state.Methods.Any(m => m.Plan == target.Plan))
        {
            throw new ConflictException("plan is not a method of the incident");
        }
        var p = LoadIn(tx, target.Plan);
        if (p.Retired || p.Spec.Revision != target.Revision)
        {
            throw new ConflictException("plan is retired or revised");
        }
        if ((binding is null || binding.Situation != Situation.Active) && !CombatRestorationPlan(tx, p.Spec))
        {
            throw new ConflictException("incident is not in deficit");
        }
        RoundsActionsSupported(p.Spec);
    }

    internal static void AuthorizeStandardPlan(StoreTransaction tx, Rounds review, StandardState owner, GenerationSnapshot root, GenerationSnapshot target)
    {
        if (!review.Need(owner.Standard.Id, out _))
        {
            throw new ConflictException("standard is not bound by the review");
        }
        var g = SummarizeOwner(owner);
        if (g.Retired || g.Status != StandardStatus.Open || g.Finding == Finding.Unclear || !SameRoot(g.Snapshot, root))
        {
            throw new ConflictException("standard is not open and measured under this root");
        }
        // A prepared plan does not dispatch while a Safeguard vetoes its goal.
        var reason = review.Veto(owner.Standard);
        if (reason.Length != 0)
        {
            throw new NotAdmittedException($"standard {g.Id}: {reason}");
        }
        if (!owner.Methods.Any(method => method.Plan == target.Plan && method.Episode == g.Episode))
        {
            throw new ConflictException("plan is not a method of the standard's episode");
        }
        var p = LoadIn(tx, target.Plan);
        if (p.Retired || p.Spec.Revision != target.Revision)
        {
            throw new ConflictException("plan is retired or revised");
        }
        if (g.Finding == Finding.Met)
        {
            // A configured bill has recovered setup, but its already-issued first output
            // still needs ordinary pawn time. This never permits another setup write.
            var waiting = false;
            foreach (var progress in p.Progress)
            {
                var v = progress.View;
                if (progress.Action.Kind != ActionKind.ProductionBill || v.Attempt.Value == 0)
                {
                    throw new ConflictException("recovered standard has work beyond its issued bill");
                }
                waiting = waiting || v.Unresolved;
            }
            if (!waiting)
            {
                throw new ConflictException("recovered standard has no output waiting");
            }
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
    /// Loads the Standard goal the review binds to <paramref name="need"/> and reports whether a planner may work
    /// it: an active deficit the Safeguards admit. The goal is returned whenever the review binds one, workable or
    /// not, and null when it binds none.
    /// </summary>
    public (StandardState? Goal, bool Workable) Workable(Rounds r, ConcernId need, CancellationToken cancellationToken)
    {
        var id = default(StandardId);
        foreach (var binding in r.Standards)
        {
            if (binding.Concern == need)
            {
                id = binding.Standard;
            }
        }
        if (id.IsEmpty)
        {
            return (null, false);
        }
        var goal = LoadStandard(id, cancellationToken);
        return (goal, goal.Standard.Status == StandardStatus.Open && goal.Standard.Finding == Finding.Unmet && r.Veto(goal.Standard).Length == 0);
    }

    public Task<(StandardState? Goal, bool Workable)> WorkableAsync(Rounds r, ConcernId need, CancellationToken cancellationToken) =>
        Task.Run(() => Workable(r, need, cancellationToken), cancellationToken);

    /// <summary>
    /// Asks the action Safeguards at dispatch: a vetoed action is <see cref="ActionVetoedException"/> with the
    /// Safeguard's reason and only that action is refused.
    /// </summary>
    internal static void VetoAction(StoreTransaction tx, DomainAction a)
    {
        var review = LoadRounds(tx);
        var (refusal, vetoed) = PolicyFunctions.RefuseAction(new SafeguardContext { Enabled = review.Enabled, Unsafe = review.Unsafe }, a);
        if (vetoed)
        {
            throw new ActionVetoedException($"action {a.Id.Value}: {refusal.Safeguard}: {refusal.Reason}");
        }
    }
}
