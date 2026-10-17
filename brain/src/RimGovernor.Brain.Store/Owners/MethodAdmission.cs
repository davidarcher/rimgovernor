using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    private const int MaxBillPlanActions = StorePackage.MaxBillPlanActions;

    /// <summary>The stockpile-zone creations one admin/stockpiles Method may carry.</summary>
    private const int MaxStockpileCreates = 16;

    private static readonly HashSet<ActionKind> RoundsSettingsKinds = new HashSet<ActionKind> { ActionKind.PawnSettings, ActionKind.ReadingPolicy, ActionKind.DrugPolicy, ActionKind.FoodPolicy };

    /// <summary>
    /// The per-family admission checks every owner shares, then the plan itself; the caller then writes the
    /// methods row (Go <c>admitOwnerMethod</c>).
    /// </summary>
    internal static void AdmitOwnerMethod(StoreTransaction tx, NeedOwner owner, PlanSpec plan)
    {
        // Self-treatment is an observed permission, not a local assignment winner.
        if (owner.Need == AdminFamily.WorkAssignments
            && plan.Actions.Any(a => a.PawnSettings is { Kind: SettingKind.SelfTend }))
            throw new NotAdmittedException("unsupported_assignment_self_tend_policy");
        // A visiting trader's session draws a negotiator off the shared forecast; only a mission's settlement trade is ours.
        if (owner.Need == EconomyFamily.Caravan && plan.Actions.Any(a => a.Trade is
            { Kind: TradeOperationKind.Open, Participant.Kind: not TradeParticipantKind.Settlement }))
            throw new NotAdmittedException("unsupported_shared_trade_labor");
        // A people/mood Need casts nothing: a preventative cast needs shared caster time and psyfocus (#3304).
        if (PeopleFamily.IsMood(owner.Need) && plan.Actions.Any(a => a.Kind == ActionKind.Ability))
            throw new NotAdmittedException("unsupported_forecast_mood_cast");
        // A window answer is the people/dialog Need's alone (#3304); another Need answers only its own letters.
        if (owner.Need != PeopleFamily.Dialog
            && plan.Actions.Any(a => a.DialogAnswer is { LetterToken.Length: 0 }))
            throw new NotAdmittedException("dialog_answer_not_owned");
        AdmitBillMethod(tx, owner, plan);
        AdmitZoneMethod(tx, owner, plan);
        AdmitAcquisitionMethod(tx, owner, plan);
        AdmitWorkMethod(tx, owner, plan);
        AdmitSupplyMethod(tx, owner, plan);
        AdmitWorkResources(tx, owner, plan);
        AdmitConstructionLabor(tx, owner, plan);
        CreatePlanIn(tx, plan);
    }

    // While the initial shelter is owed its builders are its own. An owner the review does not bind (a player
    // goal) is outside the rule, as it is outside the routine Safeguards.
    private static void AdmitConstructionLabor(StoreTransaction tx, NeedOwner owner, PlanSpec plan)
    {
        if (!plan.Actions.Any(a => DrawsConstructionLabor(a.Kind)))
        {
            return;
        }
        var review = LoadRounds(tx);
        // A Need owner is checked by its NeedId: the shelter housing Needs keep their builders.
        if (ConstructionLaborHeld(review.Latches, owner.Need.FilingKey, plan.Actions.Select(a => a.Kind)))
        {
            throw new ConstructionLaborException();
        }
    }

    /// <summary>
    /// The tail every owner's commit shares: refuse a method the owner already binds in its epoch, run the family
    /// admission and store the plan, then write the methods row and name the plan's method.
    /// </summary>
    internal static void BindOwnerMethod(StoreTransaction tx, NeedOwner owner, MethodId method, string reason, PlanSpec plan)
    {
        // Plan ids are minted; the real double-admission key is the owner table's primary key (owner, [episode,] method).
        if (owner.BoundPlan(tx, method) is { } bound)
        {
            throw new MethodBoundException($"{owner.OwnerLabel} already binds method {method.Value} to plan {bound.Value}");
        }
        // Native execution reports the payload's Method. It must identify the journal owner that
        // authorized it, and a plan must not race multiple revisions of one pawn through Hands.
        var activityPawns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in plan.Actions)
        {
            if (action.PawnActivities is not { } batch) continue;
            batch.Validate();
            foreach (var order in batch.Orders)
            {
                if (order.Method != method.Value)
                    throw new ConflictException("activity order method does not match its journal method");
                if (!activityPawns.Add(order.Pawn))
                    throw new ConflictException("method repeats a pawn across activity actions");
            }
        }
        AdmitOwnerMethod(tx, owner, plan);
        owner.BindMethod(tx, method, plan.Id, reason);
        _ = tx.Execute("UPDATE plans SET method_id=$method WHERE id=$id", ("$method", method), ("$id", plan.Id));
    }

    /// <summary>
    /// Method admission's backstop for the Safeguards the planner already asked: a vetoed proposal is
    /// <see cref="NotAdmittedException"/> with the Safeguard's reason. An owner the review does not bind (a player
    /// goal) is outside the routine Safeguards.
    /// </summary>
    internal static void AdmitRoundsSafeguards(StoreTransaction tx, NeedOwner owner)
    {
        var review = LoadRounds(tx);
        var (refusal, vetoed) = review.Refuse(new SafeguardProposal());
        if (vetoed)
        {
            throw new NotAdmittedException($"{owner.OwnerLabel}: {refusal.Safeguard}: {refusal.Reason}");
        }
    }

    private static void AdmitBillMethod(StoreTransaction tx, NeedOwner owner, PlanSpec plan)
    {
        if (!plan.Actions.Any(a => a.Kind == ActionKind.ProductionBill))
        {
            return;
        }
        var review = LoadRounds(tx);
        if (!review.Enabled || review.Snapshot != owner.Snapshot || plan.Actions.Count > MaxBillPlanActions)
        {
            throw new ConflictException($"bill method needs a current autopilot review and at most {MaxBillPlanActions} actions");
        }
        // Bills are the admin/work-ledger Need's: it commits the round's reconcile plan (the economy/resource Need's
        // stock bills, the medical Needs' bills and the biotech/mechs Need's gestation bill among them): places and
        // removals together.
        var bound = owner.Need == AdminFamily.WorkLedger;
        if (!bound)
        {
            throw new ConflictException($"{owner.OwnerLabel} does not admit production bills");
        }
        var placed = new HashSet<(string, string)>();
        foreach (var a in plan.Actions)
        {
            if (a.Kind == ActionKind.RemoveProductionBill)
            {
                continue;
            }
            if (a.ProductionBill is not { } b || !placed.Add((b.Bench, b.ClaimRecipe)))
            {
                throw new ConflictException("bill method mixes action kinds or repeats a bench and recipe");
            }
        }
    }

    private static void AdmitZoneMethod(StoreTransaction tx, NeedOwner owner, PlanSpec plan)
    {
        var hasWork = false;
        var stockpile = false;
        foreach (var action in plan.Actions)
        {
            if (action.ZoneCreate is { } zone)
            {
                hasWork = true;
                stockpile = stockpile || zone.Kind == ZoneKind.Stockpile;
            }
        }
        if (!hasWork)
        {
            return;
        }
        var review = LoadRounds(tx);
        // A growing-field or fishing method places a bounded batch (the food/supply Need), a royal decree's harvest
        // field (the joiners/decrees Need, #3302) or the hay, social and resource crops (the economy/resource Need).
        // A stockpile zone is admitted for the admin/stockpiles Need alone, which applies every store the departments
        // declare in one batch (decision 20).
        var limit = 32;
        if (!review.Enabled || review.Snapshot != owner.Snapshot)
        {
            throw new ConflictException();
        }
        var bound = false;
        var resource = false;
        var held = owner.Need;
        resource = held == EconomyFamily.Resource;
        if (stockpile)
        {
            bound = held == AdminFamily.Stockpiles;
            limit = MaxStockpileCreates;
        }
        else
        {
            bound = held == FoodSupplyNeed || held == JoinersFamily.Decrees || resource;
        }
        if (!bound || plan.Actions.Count > limit)
        {
            throw new ConflictException();
        }
        var cells = new HashSet<Cell>();
        foreach (var action in plan.Actions)
        {
            if (action.ZoneCreate is not { } zone || stockpile != (zone.Kind == ZoneKind.Stockpile))
            {
                throw new ConflictException();
            }
            // Any resource crop may be sown; the planner holds the social crops to their brewing gate and ceiling.
            if (resource && zone.Kind != ZoneKind.Growing)
            {
                throw new ConflictException();
            }
            foreach (var cell in zone.Cells)
            {
                if (!cells.Add(cell))
                {
                    throw new ConflictException();
                }
            }
        }
    }

    private static void AdmitAcquisitionMethod(StoreTransaction tx, NeedOwner owner, PlanSpec plan)
    {
        if (!plan.Actions.Any(a => a.Kind == ActionKind.Acquisition))
        {
            return;
        }
        var review = LoadRounds(tx);
        if (!review.Enabled || review.Snapshot != owner.Snapshot || plan.Actions.Count > MaxCatalogSelection + PolicyConstants.MaxHuntRows)
        {
            throw new ConflictException();
        }
        // The military pests Need hunts its pests (#3259), the food/supply Need forages and hunts (#3254) and the
        // joiners/decrees Need hunts a royal decree's animals (#3302), the economy/resource Need chops, forages and
        // hunts toward its floors (#3303) and the medical/reserves Need harvests healroot (#3258).
        var held = owner.Need;
        var bound = (held == MilitaryFamily.Pests || held == FoodSupplyNeed || held == JoinersFamily.Decrees || held == EconomyFamily.Resource
                || held == MedicalReservesNeed);
        if (!bound)
        {
            throw new ConflictException();
        }
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in plan.Actions)
        {
            if (action.Acquisition is not { } w || !sources.Add(w.Thing))
            {
                throw new ConflictException();
            }
        }
    }

    private static void AdmitWorkMethod(StoreTransaction tx, NeedOwner owner, PlanSpec plan)
    {
        if (!plan.Actions.Any(a => a.Kind == ActionKind.WorkAssignment))
        {
            return;
        }
        var review = LoadRounds(tx);
        if (!review.Enabled || review.Snapshot != owner.Snapshot)
        {
            throw new ConflictException();
        }
        var bound = false;
        // A creepjoiner's isolation (the admin/creep-joiners Need) and a sheltering move (disaster/sheltering) are
        // area moves: each assignment is area-only.
        var isolation = false;
        var held = owner.Need;
        bound = held == AdminFamily.WorkAssignments;
        isolation = held == AdminFamily.CreepJoiners || held == DisasterFamily.Sheltering;
        if (!bound && !isolation)
        {
            throw new ConflictException();
        }
        // The admin/work-assignments plan also carries the pawn settings and per-pawn policies that ride its goal; the
        // eight-pawn cap and one-assignment-per-pawn rule bind only the work assignments.
        var pawns = new HashSet<PawnId>();
        foreach (var action in plan.Actions)
        {
            if (RoundsSettingsKinds.Contains(action.Kind) || (isolation && action.Kind != ActionKind.WorkAssignment))
            {
                continue;
            }
            if (action.WorkAssignment is not { } w || pawns.Contains(w.Pawn) || pawns.Count == 8)
            {
                throw new ConflictException();
            }
            if (isolation && (!w.HasArea || w.HasSchedule || w.Settings.Count != 0))
            {
                throw new ConflictException();
            }
            _ = pawns.Add(w.Pawn);
        }
    }

    /// <summary>
    /// Admits only stacks the review's cohort still lists, each at the cell the census last reported: the event loot
    /// for the admin/supply-safety Need, and the larder corpse and reserve stacks for the food-stores/storage Need.
    /// </summary>
    private static void AdmitSupplyMethod(StoreTransaction tx, NeedOwner owner, PlanSpec plan)
    {
        if (!plan.Actions.Any(a => a.Kind is ActionKind.SupplyAllow or ActionKind.SupplyForbid))
        {
            return;
        }
        var review = LoadRounds(tx);
        if (!review.Enabled || review.Snapshot != owner.Snapshot)
        {
            throw new ConflictException();
        }
        var bound = false;
        var reserve = new Dictionary<string, ReserveSupply>(StringComparer.Ordinal);
        IReadOnlyList<StartingSupply> cohort = [];
        var held = owner.Need;
        if (held == FoodStorageNeed)
        {
            bound = true;
            cohort = review.LarderSupplies;
            foreach (var row in review.ReserveSupplies)
            {
                reserve[row.Thing] = row;
            }
        }
        else if (held == AdminFamily.SupplySafety)
        {
            bound = true;
            cohort = review.EventLoot.Pending;
        }
        if (!bound)
        {
            throw new ConflictException();
        }
        var pending = new Dictionary<string, StartingSupply>(StringComparer.Ordinal);
        foreach (var row in cohort)
        {
            pending[row.Thing] = row;
        }
        foreach (var action in plan.Actions)
        {
            if (action.SupplyAllow is not { } supply)
            {
                throw new ConflictException();
            }
            var listed = pending.TryGetValue(supply.Thing, out var row);
            if (reserve.TryGetValue(supply.Thing, out var entry) && entry.Definition == supply.Definition && entry.Forbid == supply.Forbidden)
            {
                continue;
            }
            if (!listed || row!.Definition != supply.Definition || row.Cell != supply.Cell || row.Forbid != supply.Forbidden)
            {
                throw new ConflictException();
            }
        }
    }
}
