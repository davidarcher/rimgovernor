using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    private const int MaxBillPlanActions = StorePackage.MaxBillPlanActions;

    /// <summary>The stockpile-zone creations one admin/stockpiles Method may carry.</summary>
    private const int MaxStockpileCreates = 16;

    private static readonly HashSet<ActionKind> SettingsKinds = new HashSet<ActionKind> { ActionKind.PawnSettings, ActionKind.ReadingPolicy, ActionKind.DrugPolicy, ActionKind.FoodPolicy };

    /// <summary>
    /// The per-family admission checks every owner shares, then the plan itself; the caller then writes the
    /// methods row. A refusal on the current evidence is returned, with nothing written; a malformed plan throws.
    /// </summary>
    internal static AdmissionRefusal? AdmitOwnerMethod(StoreTransaction tx, NeedOwner owner, PlanSpec plan)
    {
        // Self-treatment is an observed permission, not a local assignment winner.
        if (owner.Need == AdminFamily.WorkAssignments
            && plan.Actions.Any(a => a.PawnSettings is { Kind: SettingKind.SelfTend }))
            return new(RefusalKind.SharedAdmission, "unsupported_assignment_self_tend_policy");
        // A visiting trader's session draws a negotiator off the shared forecast; only a mission's settlement trade is ours.
        if (owner.Need == EconomyFamily.Caravan && plan.Actions.Any(a => a.Trade is
            { Kind: TradeOperationKind.Open, Participant.Kind: not TradeParticipantKind.Settlement }))
            return new(RefusalKind.SharedAdmission, "unsupported_shared_trade_labor");
        // A people/mood Need casts nothing: a preventative cast needs shared caster time and psyfocus (#3304).
        if (PeopleFamily.IsMood(owner.Need) && plan.Actions.Any(a => a.Kind == ActionKind.Ability))
            return new(RefusalKind.SharedAdmission, "unsupported_forecast_mood_cast");
        // A window answer is the people/dialog Need's alone (#3304); another Need answers only its own letters.
        if (owner.Need != PeopleFamily.Dialog
            && plan.Actions.Any(a => a.DialogAnswer is { LetterToken.Length: 0 }))
            return new(RefusalKind.SharedAdmission, "dialog_answer_not_owned");
        AdmitBillMethod(tx, owner, plan);
        AdmitZoneMethod(tx, owner, plan);
        AdmitAcquisitionMethod(tx, owner, plan);
        AdmitWorkMethod(tx, owner, plan);
        AdmitSupplyMethod(tx, owner, plan);
        if (WorkResourcesRefusalIn(tx, owner, plan) is { } refused)
        {
            return refused;
        }
        CreatePlanIn(tx, plan);
        return null;
    }

    /// <summary>
    /// The tail every owner's commit shares: refuse a method the owner already binds in its epoch, run the family
    /// admission and store the plan, then write the methods row and name the plan's method. An admission refusal is
    /// returned with nothing written.
    /// </summary>
    internal static AdmissionRefusal? BindOwnerMethod(StoreTransaction tx, NeedOwner owner, MethodId method, string reason, PlanSpec plan)
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
        if (AdmitOwnerMethod(tx, owner, plan) is { } refused)
        {
            return refused;
        }
        owner.BindMethod(tx, method, plan.Id, reason);
        _ = tx.Execute("UPDATE plans SET method_id=$method WHERE id=$id", ("$method", method), ("$id", plan.Id));
        return null;
    }

    /// <summary>
    /// Method admission's backstop for the Safeguards the planner already asked: a vetoed proposal is refused with
    /// the Safeguard's reason. An owner the review does not bind (a player goal) is outside the routine Safeguards.
    /// </summary>
    internal static AdmissionRefusal? SafeguardRefusal(StoreTransaction tx, NeedOwner owner)
    {
        var review = LoadReview(tx);
        var (refusal, vetoed) = review.Refuse(new SafeguardProposal());
        return vetoed ? new(RefusalKind.SharedAdmission, $"{owner.OwnerLabel}: {refusal.Safeguard}: {refusal.Reason}") : null;
    }

    /// <summary>
    /// The Safeguard backstop of the paths outside claimed commits (combat commits, dispatch, activity continuation),
    /// which still report a veto as <see cref="NotAdmittedException"/>.
    /// </summary>
    internal static void AdmitReviewSafeguards(StoreTransaction tx, NeedOwner owner)
    {
        if (SafeguardRefusal(tx, owner) is { } refused)
        {
            throw new NotAdmittedException(refused.Reason);
        }
    }

    /// <summary><see cref="BindOwnerMethod"/> for the combat commits outside claimed commits: a refusal is <see cref="NotAdmittedException"/>.</summary>
    internal static void BindOwnerMethodOrThrow(StoreTransaction tx, NeedOwner owner, MethodId method, string reason, PlanSpec plan)
    {
        if (BindOwnerMethod(tx, owner, method, reason, plan) is { } refused)
        {
            throw new NotAdmittedException(refused.Reason);
        }
    }

    private static void AdmitBillMethod(StoreTransaction tx, NeedOwner owner, PlanSpec plan)
    {
        if (!plan.Actions.Any(a => a.Kind == ActionKind.ProductionBill))
        {
            return;
        }
        var review = LoadReview(tx);
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
        var review = LoadReview(tx);
        // A growing-field or fishing method places a bounded batch (the food/supply Need), a royal decree's harvest
        // field (the joiners/decrees Need, #3302) or the hay, social and resource crops (the economy/resource Need).
        // A stockpile zone is admitted for the admin/stockpiles Need alone, which applies every store the departments
        // declare in one batch (decision 20).
        var limit = 32;
        if (!review.Enabled || review.Snapshot != owner.Snapshot)
        {
            throw new ConflictException($"{owner.OwnerLabel}: zone method needs a current autopilot review");
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
            bound = held == FoodRules.FoodSupplyNeed || held == JoinersFamily.Decrees || resource;
        }
        if (!bound || plan.Actions.Count > limit)
        {
            throw new ConflictException($"{owner.OwnerLabel}: zone method not admitted for this owner or over {limit} actions");
        }
        var cells = new HashSet<Cell>();
        foreach (var action in plan.Actions)
        {
            if (action.ZoneCreate is not { } zone || stockpile != (zone.Kind == ZoneKind.Stockpile))
            {
                throw new ConflictException($"zone method mixes action kinds at {action.Id.Value}");
            }
            // Any resource crop may be sown; the planner holds the social crops to their brewing gate and ceiling.
            if (resource && zone.Kind != ZoneKind.Growing)
            {
                throw new ConflictException($"economy/resource zone {action.Id.Value} is not a growing zone");
            }
            foreach (var cell in zone.Cells)
            {
                if (!cells.Add(cell))
                {
                    throw new ConflictException($"zone method repeats cell {cell} at {action.Id.Value}");
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
        var review = LoadReview(tx);
        if (!review.Enabled || review.Snapshot != owner.Snapshot || plan.Actions.Count > AcquisitionCatalogRules.MaxCatalogSelection + PawnRules.MaxHuntRows)
        {
            throw new ConflictException($"{owner.OwnerLabel}: acquisition method needs a current autopilot review and at most {AcquisitionCatalogRules.MaxCatalogSelection + PawnRules.MaxHuntRows} actions");
        }
        // The military pests Need hunts its pests (#3259), the food/supply Need forages and hunts (#3254) and the
        // joiners/decrees Need hunts a royal decree's animals (#3302), the economy/resource Need chops, forages and
        // hunts toward its floors (#3303) and the medical/reserves Need harvests healroot (#3258).
        var held = owner.Need;
        var bound = (held == MilitaryFamily.Pests || held == FoodRules.FoodSupplyNeed || held == JoinersFamily.Decrees || held == EconomyFamily.Resource
                || held == MedicalRules.MedicalReservesNeed);
        if (!bound)
        {
            throw new ConflictException($"{owner.OwnerLabel} does not admit acquisitions");
        }
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in plan.Actions)
        {
            if (action.Acquisition is not { } w || !sources.Add(w.Thing))
            {
                throw new ConflictException($"acquisition method mixes action kinds or repeats a source at {action.Id.Value}");
            }
        }
    }

    private static void AdmitWorkMethod(StoreTransaction tx, NeedOwner owner, PlanSpec plan)
    {
        if (!plan.Actions.Any(a => a.Kind == ActionKind.WorkAssignment))
        {
            return;
        }
        var review = LoadReview(tx);
        if (!review.Enabled || review.Snapshot != owner.Snapshot)
        {
            throw new ConflictException($"{owner.OwnerLabel}: work method needs a current autopilot review");
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
            throw new ConflictException($"{owner.OwnerLabel} does not admit work assignments");
        }
        // The admin/work-assignments plan also carries the pawn settings, per-pawn policies and construction
        // finishing-skill choices that ride its goal (#3558); the eight-pawn cap and one-assignment-per-pawn rule bind
        // only the work assignments.
        var pawns = new HashSet<PawnId>();
        foreach (var action in plan.Actions)
        {
            if (
                SettingsKinds.Contains(action.Kind)
                || (bound && action.FinishingSkill is not null)
                || (isolation && action.Kind != ActionKind.WorkAssignment)
            )
            {
                continue;
            }
            if (action.WorkAssignment is not { } w || pawns.Contains(w.Pawn) || pawns.Count == 8)
            {
                throw new ConflictException($"work method carries a {Wire.Name(action.Kind)} action, repeats a pawn or exceeds 8 pawns at {action.Id.Value}");
            }
            if (isolation && (!w.HasArea || w.HasSchedule || w.Settings.Count != 0))
            {
                throw new ConflictException($"{owner.OwnerLabel}: assignment {action.Id.Value} is not area-only");
            }
            _ = pawns.Add(w.Pawn);
        }
    }

    /// <summary>
    /// Admits only stacks the review's cohort still lists, each at the cell the census last reported: the event loot
    /// for the admin/supply-safety Need, the larder corpse and reserve stacks for the food-stores/storage Need, and the
    /// forbidden wastepacks for the biotech/pollution Need, whose Method also carries its clear-area edit and hauls.
    /// </summary>
    private static void AdmitSupplyMethod(StoreTransaction tx, NeedOwner owner, PlanSpec plan)
    {
        if (!plan.Actions.Any(a => a.Kind is ActionKind.SupplyAllow or ActionKind.SupplyForbid))
        {
            return;
        }
        var review = LoadReview(tx);
        if (!review.Enabled || review.Snapshot != owner.Snapshot)
        {
            throw new ConflictException($"{owner.OwnerLabel}: supply method needs a current autopilot review");
        }
        var bound = false;
        var reserve = new Dictionary<string, ReserveSupply>(StringComparer.Ordinal);
        IReadOnlyList<StartingSupply> cohort = [];
        var held = owner.Need;
        if (held == FoodStoresRules.FoodStorageNeed)
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
        var pollution = held == BiotechFamily.Pollution;
        if (pollution)
        {
            bound = true;
            cohort = review.PollutionSupplies;
        }
        if (!bound)
        {
            throw new ConflictException($"{owner.OwnerLabel} does not admit supply batches");
        }
        var pending = new Dictionary<string, StartingSupply>(StringComparer.Ordinal);
        foreach (var row in cohort)
        {
            pending[row.Thing] = row;
        }
        foreach (var action in plan.Actions)
        {
            if (pollution && action.Kind is ActionKind.Area or ActionKind.WastepackHaul)
            {
                continue;
            }
            if (action.SupplyAllow is not { } supply)
            {
                throw new ConflictException($"supply method mixes action kinds at {action.Id.Value}");
            }
            var listed = pending.TryGetValue(supply.Thing, out var row);
            if (reserve.TryGetValue(supply.Thing, out var entry) && entry.Definition == supply.Definition && entry.Forbid == supply.Forbidden)
            {
                continue;
            }
            if (!listed || row!.Definition != supply.Definition || row.Cell != supply.Cell || row.Forbid != supply.Forbidden)
            {
                throw new ConflictException($"supply {supply.Thing} at {action.Id.Value} is not the review's cohort row");
            }
        }
    }
}
