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
    internal static void AdmitOwnerMethod(StoreTransaction tx, IMethodOwner owner, PlanSpec plan)
    {
        // Ordinary controlled labor uses the shared forecast's exact activity Method.
        // Incident protection and explicit native operations retain their existing authority.
        if (owner is StandardState && owner.OwnerSnapshot.Native.Value != 0)
            foreach (var action in plan.Actions)
                if (action.Kind is ActionKind.Repair or ActionKind.Clean or ActionKind.Haul
                    or ActionKind.RecoveryService or ActionKind.Ritual or ActionKind.Ignite
                    or ActionKind.Equip or ActionKind.GearReplace or ActionKind.Gathering
                    or ActionKind.UseItem or ActionKind.DropEquipment or ActionKind.Movement
                    or ActionKind.Capture or ActionKind.GiveItem or ActionKind.CommsTradeRequest
                    or ActionKind.Rescue or ActionKind.Tend
                    || action.Deconstruction is { ReplacesWithWall: true }
                    || action.CaravanDeparture is { Mode: CaravanDepartureMode.Formation }
                    || action.QuestShuttle is { Loading: ShuttleLoading.ExplicitPawns } or { Loading: ShuttleLoading.Autoload, Autoload: true })
                    throw new NotAdmittedException("unsupported_voluntary_method:" + Wire.Name(action.Kind));
        // Self-treatment is an observed permission, not a local assignment winner.
        if (owner is StandardState assignments && assignments.Standard.Kind == EnsureWorkAssignments
            && plan.Actions.Any(a => a.PawnSettings is { Kind: SettingKind.SelfTend }))
            throw new NotAdmittedException("unsupported_assignment_self_tend_policy");
        if (owner is IncidentState incident)
        {
            if (incident.Incident.Kind == TradeWithCaravan && plan.Actions.Any(a => a.Trade is
                { Kind: TradeOperationKind.Open, Participant.Kind: not TradeParticipantKind.Settlement }))
                throw new NotAdmittedException("unsupported_shared_trade_labor");
            if (incident.Incident.Kind == EnsureMood && plan.Actions.Any(a => a.Kind == ActionKind.Ability))
                throw new NotAdmittedException("unsupported_forecast_mood_cast");
        }
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
    private static void AdmitConstructionLabor(StoreTransaction tx, IMethodOwner owner, PlanSpec plan)
    {
        if (!plan.Actions.Any(a => DrawsConstructionLabor(a.Kind)))
        {
            return;
        }
        var review = LoadRounds(tx);
        // A Need owner is checked by its NeedId: the shelter housing Needs keep their builders.
        if (owner.OwnerNeed(review, out var need)
            && ConstructionLaborHeld(review.Latches, owner is NeedOwner { Need: var held } ? held.FilingKey : need, plan.Actions.Select(a => a.Kind)))
        {
            throw new ConstructionLaborException();
        }
    }

    /// <summary>
    /// The tail every owner's commit shares: refuse a method the owner already binds in its epoch, run the family
    /// admission and store the plan, then write the methods row and name the plan's method.
    /// </summary>
    internal static void BindOwnerMethod(StoreTransaction tx, IMethodOwner owner, MethodId method, string reason, PlanSpec plan)
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
    internal static void AdmitRoundsSafeguards(StoreTransaction tx, IMethodOwner owner)
    {
        var review = LoadRounds(tx);
        if (!owner.OwnerNeed(review, out var need))
        {
            return;
        }
        var (refusal, vetoed) = review.Refuse(new SafeguardProposal { Need = need });
        if (vetoed)
        {
            throw new NotAdmittedException($"{owner.OwnerLabel}: {refusal.Safeguard}: {refusal.Reason}");
        }
    }

    private static void AdmitBillMethod(StoreTransaction tx, IMethodOwner owner, PlanSpec plan)
    {
        if (!plan.Actions.Any(a => a.Kind == ActionKind.ProductionBill))
        {
            return;
        }
        var review = LoadRounds(tx);
        if (!review.Enabled || review.Snapshot != owner.OwnerSnapshot || plan.Actions.Count > MaxBillPlanActions)
        {
            throw new ConflictException($"bill method needs a current autopilot review and at most {MaxBillPlanActions} actions");
        }
        // Bills are the admin/work-ledger Need's: it commits the round's reconcile plan (the economy/resource Need's
        // stock bills, the medical Needs' bills and the biotech/mechs Need's gestation bill among them): places and
        // removals together.
        var bound = owner is NeedOwner { Need: var held } && held == AdminFamily.WorkLedger;
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

    private static void AdmitZoneMethod(StoreTransaction tx, IMethodOwner owner, PlanSpec plan)
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
        if (!review.Enabled || review.Snapshot != owner.OwnerSnapshot)
        {
            throw new ConflictException();
        }
        var bound = false;
        var resource = false;
        if (owner is NeedOwner { Need: var held })
        {
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

    private static void AdmitAcquisitionMethod(StoreTransaction tx, IMethodOwner owner, PlanSpec plan)
    {
        if (!plan.Actions.Any(a => a.Kind == ActionKind.Acquisition))
        {
            return;
        }
        var review = LoadRounds(tx);
        if (!review.Enabled || review.Snapshot != owner.OwnerSnapshot || plan.Actions.Count > MaxCatalogSelection + PolicyConstants.MaxHuntRows)
        {
            throw new ConflictException();
        }
        // The military pests Need hunts its pests (#3259), the food/supply Need forages and hunts (#3254) and the
        // joiners/decrees Need hunts a royal decree's animals (#3302), the economy/resource Need chops, forages and
        // hunts toward its floors (#3303) and the medical/reserves Need harvests healroot (#3258).
        var bound = owner is NeedOwner { Need: var held }
            && (held == MilitaryFamily.Pests || held == FoodSupplyNeed || held == JoinersFamily.Decrees || held == EconomyFamily.Resource
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

    private static void AdmitWorkMethod(StoreTransaction tx, IMethodOwner owner, PlanSpec plan)
    {
        if (!plan.Actions.Any(a => a.Kind == ActionKind.WorkAssignment))
        {
            return;
        }
        var review = LoadRounds(tx);
        if (!review.Enabled || review.Snapshot != owner.OwnerSnapshot)
        {
            throw new ConflictException();
        }
        var bound = false;
        var areaOnly = false;
        // A creepjoiner's isolation (the admin/creep-joiners Need) is an area move: each assignment is area-only.
        var isolation = false;
        var need = EnsureWorkAssignments;
        if (owner is NeedOwner { Need: var held })
        {
            isolation = held == AdminFamily.CreepJoiners;
        }
        else if (owner.OwnerNeed(review, out var served))
        {
            bound = served == need;
            areaOnly = served == RecoverDisasterServices;
        }
        if (!bound && !areaOnly && !isolation)
        {
            throw new ConflictException();
        }
        // The EnsureWorkAssignments plan also carries the pawn settings and per-pawn policies that ride its goal; the
        // eight-pawn cap and one-assignment-per-pawn rule bind only the work assignments. A disaster area plan
        // carries assignments alone.
        var pawns = new HashSet<PawnId>();
        foreach (var action in plan.Actions)
        {
            if ((!areaOnly && RoundsSettingsKinds.Contains(action.Kind)) || (isolation && action.Kind != ActionKind.WorkAssignment))
            {
                continue;
            }
            if (action.WorkAssignment is not { } w || pawns.Contains(w.Pawn) || pawns.Count == 8)
            {
                throw new ConflictException();
            }
            if ((areaOnly || isolation) && (!w.HasArea || w.HasSchedule || w.Settings.Count != 0))
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
    private static void AdmitSupplyMethod(StoreTransaction tx, IMethodOwner owner, PlanSpec plan)
    {
        if (!plan.Actions.Any(a => a.Kind is ActionKind.SupplyAllow or ActionKind.SupplyForbid))
        {
            return;
        }
        var review = LoadRounds(tx);
        if (!review.Enabled || review.Snapshot != owner.OwnerSnapshot)
        {
            throw new ConflictException();
        }
        var bound = false;
        var reserve = new Dictionary<string, ReserveSupply>(StringComparer.Ordinal);
        IReadOnlyList<StartingSupply> cohort = [];
        if (owner is NeedOwner { Need: var held })
        {
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

    /// <summary>
    /// A purely-acquisition method may be committed alongside a goal's already-dispatched production bill or growing
    /// field: the bill may be waiting on exactly the ingredient this acquisition fetches, and a sown field feeds the
    /// colony on a different horizon than harvesting wild food does. Any other open work still blocks.
    /// </summary>
    private static bool AcquisitionOpenWorkExempt(StoreTransaction tx, StandardState goal, PlanSpec plan)
    {
        if (plan.Actions.Count == 0)
        {
            return false;
        }
        // A stall withdraw is admitted over any open work: it only removes a designation nobody took.
        if (plan.Actions[0].AcquisitionWithdraw is not null && plan.Actions.Count == 1)
        {
            return true;
        }
        if (plan.Actions.Any(a => a.Kind != ActionKind.Acquisition))
        {
            return false;
        }
        foreach (var m in goal.Methods)
        {
            foreach (var progress in LoadIn(tx, m.Plan).Progress)
            {
                var action = progress.Action;
                if (AcquisitionIndependentWork(action))
                {
                    continue;
                }
                if (Standard.WorkOpen([progress]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>The action kinds whose open progress does not block a fresh acquisition method for the same goal.</summary>
    private static bool AcquisitionIndependentWork(DomainAction action) =>
        action.Kind is ActionKind.ProductionBill
        || action.ZoneCreate is { Kind: ZoneKind.Growing };

    /// <summary>
    /// A method made only of grower re-crops may be committed under any open work: it re-crops a grower that already
    /// stands and shares no cells or stock with the basin batch that built it.
    /// </summary>
    private static bool GrowerCropOpenWorkExempt(PlanSpec plan) => plan.Actions.Count > 0 && plan.Actions.All(a => a.Kind == ActionKind.GrowerCrop);

    /// <summary>Replacement is observed and guarded natively by owned bill identity and unchanged settings.</summary>
    private static bool MealReplacementOpenWorkExempt(StandardState goal, PlanSpec plan) =>
        plan.Actions.Count == 1 && plan.Actions[0].ProductionBill is { Mode: BillMode.FoodTarget } bill && bill.Replaces.Length != 0;

    /// <summary>
    /// Admits furniture beside an owner's open planned-room ring wave, and a ring wave beside the owner's open
    /// furniture. Both sides are pure construction on cells the other does not build on, a single ring wave beside
    /// non-ring methods; any other open method still holds the owner.
    /// </summary>
    private static bool RoomShellOpenWorkExempt(StoreTransaction tx, StandardState goal, MethodId method, PlanSpec plan)
    {
        if (plan.Actions.Count == 0)
        {
            return false;
        }
        var newRing = StorePackage.IsRoomShellMethod(method);
        var taken = new HashSet<Cell>();
        foreach (var m in goal.Methods)
        {
            var p = LoadIn(tx, m.Plan);
            if (!StorePackage.PlanOpen(p))
            {
                continue;
            }
            if (StorePackage.IsRoomShellMethod(m.MethodId) == newRing)
            {
                return false;
            }
            foreach (var a in p.Spec.Actions)
            {
                if (a.Building is not { } b)
                {
                    return false;
                }
                _ = taken.Add(b.Cell);
            }
        }
        return plan.Actions.All(a => a.Building is { } b && !taken.Contains(b.Cell));
    }

    /// <summary>
    /// Admits a pure-construction method beside the owner's open work when that work is pure construction on other
    /// cells: building plans that touch disjoint cells cannot conflict, so there is nothing to observe first.
    /// </summary>
    private static bool BuildingOpenWorkExempt(StoreTransaction tx, StandardState goal, PlanSpec plan)
    {
        var taken = new HashSet<Cell>();
        foreach (var m in goal.Methods)
        {
            var p = LoadIn(tx, m.Plan);
            if (!StorePackage.PlanOpen(p))
            {
                continue;
            }
            foreach (var a in p.Spec.Actions)
            {
                if (a.Building is not { } b)
                {
                    return false;
                }
                _ = taken.Add(b.Cell);
            }
        }
        return plan.Actions.Count != 0 && plan.Actions.All(a => a.Building is { } b && !taken.Contains(b.Cell));
    }
}
