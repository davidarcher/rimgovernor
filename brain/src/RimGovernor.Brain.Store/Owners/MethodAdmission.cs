using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    private const int MaxBillPlanActions = StorePackage.MaxBillPlanActions;

    private static readonly HashSet<string> FieldInfrastructure = new HashSet<string>(StringComparer.Ordinal) { "SunLamp", "HydroponicsBasin", "Heater" };

    private static readonly HashSet<ActionKind> RoundsSettingsKinds = new HashSet<ActionKind> { ActionKind.PawnSettings, ActionKind.ReadingPolicy, ActionKind.DrugPolicy, ActionKind.FoodPolicy };

    /// <summary>The free, instant bench EnsureFoodSupply builds so a hunt's corpse can be butchered.</summary>
    private const string ButcherSpotDefinition = "ButcherSpot";

    /// <summary>
    /// The per-family admission checks every owner shares, then the plan itself; the caller then writes the
    /// methods row (Go <c>admitOwnerMethod</c>).
    /// </summary>
    internal static void AdmitOwnerMethod(StoreTransaction tx, IMethodOwner owner, PlanSpec plan)
    {
        AdmitBillMethod(tx, owner, plan);
        AdmitZoneMethod(tx, owner, plan);
        AdmitAcquisitionMethod(tx, owner, plan);
        AdmitWorkMethod(tx, owner, plan);
        AdmitSupplyMethod(tx, owner, plan);
        CreatePlanIn(tx, plan);
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
        var (refusal, vetoed) = review.Refuse(new SafeguardProposal { Need = need, Priority = owner.OwnerPriority });
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
        // Bills serve the cooking/food goals, the resource-target goals whose production path stages a bench and then
        // a StockTarget bill on it, the equipment goal whose replacement is a StockTarget bill on a standing bench,
        // the refrigeration goal's cook-ahead bill, the art goal's pinned sculpture bills, the baby feeding goal's
        // baby food bill, the mech goal's gestation bills and the surgery goal's part bills. The work ledger goal
        // commits the round's whole reconcile plan: places and removals together.
        var bound = owner.OwnerNeed(review, out var need)
            && (need == EnsureCooking
                || need == EnsureFoodSupply
                || need == MaintainFoodStorage
                || need == MaintainResource
                || need == MaintainEquipment
                || need == MaintainRefrigeration
                || need == MaintainArt
                || need == MaintainBabyFeeding
                || need == MaintainMechs
                || need == MaintainSurgery
                || need == MaintainWorkLedger);
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
        // A growing-field method places a bounded batch (EnsureFoodSupply) or the social crops (MaintainResource).
        // A stockpile zone is admitted for any concern that owns stores (StockpileZoneLimit), bounded per owner.
        var limit = 32;
        if (!review.Enabled || review.Snapshot != owner.OwnerSnapshot)
        {
            throw new ConflictException();
        }
        var bound = false;
        var resource = false;
        if (owner.OwnerNeed(review, out var need))
        {
            if (stockpile)
            {
                limit = StockpileZoneLimit(need);
                bound = limit > 0;
            }
            else
            {
                bound = need == EnsureFoodSupply || need == MaintainResource;
                resource = need == MaintainResource;
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
        var bound = owner.OwnerNeed(review, out var need) && (need == EnsureFoodSupply || need == MaintainMedicalReserves || need == ClearPests || need == MaintainResource);
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
        // A creepjoiner's isolation is an area move among its drops and inspections: the other actions pass, each
        // assignment is area-only.
        var isolation = false;
        var need = EnsureWorkAssignments;
        if (owner.OwnerNeed(review, out var served))
        {
            bound = served == need;
            areaOnly = served == RecoverDisasterServices;
            isolation = served == ManageCreepJoiners;
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

    /// <summary>Admits only stacks the review's cohort still lists, each at the cell the census last reported.</summary>
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
        if (owner.OwnerNeed(review, out var need))
        {
            if (need == ManageSupplySafety)
            {
                bound = true;
                cohort = review.EventLoot.Pending;
            }
            else if (need == MaintainFoodStorage)
            {
                bound = true;
                cohort = review.LarderSupplies;
                foreach (var row in review.ReserveSupplies)
                {
                    reserve[row.Thing] = row;
                }
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
        var pest = goal.Standard.Kind == ClearPests;
        // EnsureFoodSupply's hunt-only plan passes the goal's open plant harvests: a forage batch runs for days and
        // the hunt rows the butcher spot and bill were placed for would otherwise wait behind it.
        var hunts = goal.Standard.Kind == EnsureFoodSupply && plan.Actions.All(a => a.Acquisition is { Hunt: true });
        foreach (var m in goal.Methods)
        {
            foreach (var progress in LoadIn(tx, m.Plan).Progress)
            {
                var action = progress.Action;
                if (AcquisitionIndependentWork(m.MethodId, action) || (pest && action.Kind == ActionKind.Acquisition))
                {
                    continue;
                }
                if (hunts && action.Kind == ActionKind.Acquisition)
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
    private static bool AcquisitionIndependentWork(MethodId method, DomainAction action) =>
        action.Kind is ActionKind.ProductionBill or ActionKind.CommsTradeRequest
        || ButcherFacilityWork(method, action)
        || action.ZoneCreate is { Kind: ZoneKind.Growing };

    /// <summary>
    /// The mirror of <see cref="AcquisitionOpenWorkExempt"/>: a method made only of growing-zone creations or farm
    /// infrastructure may be committed while the goal's open work is nothing but dispatched acquisitions or
    /// production bills. Open zone work or any other family's open work still blocks, so field batches stay
    /// strictly sequential.
    /// </summary>
    private static bool FieldOpenWorkExempt(StoreTransaction tx, StandardState goal, PlanSpec plan)
    {
        if (plan.Actions.Count == 0)
        {
            return false;
        }
        foreach (var action in plan.Actions)
        {
            if (action.ZoneCreate is { Kind: ZoneKind.Growing or ZoneKind.Fishing } || (action.Building is { } building && FieldInfrastructure.Contains(building.Definition)))
            {
                continue;
            }
            return false;
        }
        foreach (var m in goal.Methods)
        {
            foreach (var progress in LoadIn(tx, m.Plan).Progress)
            {
                var kind = progress.Action.Kind;
                if (kind != ActionKind.Acquisition && kind != ActionKind.ProductionBill && !ButcherFacilityWork(m.MethodId, progress.Action) && Standard.WorkOpen([progress]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// A method made only of grower re-crops may be committed under any open work: it re-crops a grower that already
    /// stands and shares no cells or stock with the basin batch that built it.
    /// </summary>
    private static bool GrowerCropOpenWorkExempt(PlanSpec plan) => plan.Actions.Count > 0 && plan.Actions.All(a => a.Kind == ActionKind.GrowerCrop);

    /// <summary>Replacement is observed and guarded natively by owned bill identity and unchanged settings.</summary>
    private static bool MealReplacementOpenWorkExempt(StandardState goal, PlanSpec plan) =>
        plan.Actions.Count == 1 && plan.Actions[0].ProductionBill is { Mode: BillMode.FoodTarget } bill && bill.Replaces.Length != 0;

    private static bool ButcherBenchAction(DomainAction action) =>
        action.Building is { } building
            ? building.Definition == ButcherSpotDefinition || building.Definition == PolicyConstants.ButcherTableDefinition
            : action.Deconstruction is { } cut && cut.Definition == ButcherSpotDefinition;

    /// <summary>An action of the butcher facility under <paramref name="method"/>: a bench action, or any action of the planned butchery's ring wave.</summary>
    private static bool ButcherFacilityWork(MethodId method, DomainAction action) => StorePackage.IsRoomShellMethod(method) || ButcherBenchAction(action);

    /// <summary>
    /// Lets EnsureFoodSupply's butcher facility (the spot, the table, the planned butchery's ring and the spot's
    /// removal) and its production bills be committed while the goal's fields, foraging and hunts stay open.
    /// </summary>
    private static bool FoodFacilityOpenWorkExempt(StoreTransaction tx, StandardState goal, MethodId method, PlanSpec plan)
    {
        if (plan.Actions.Count == 0 || goal.Standard.Kind != EnsureFoodSupply)
        {
            return false;
        }
        var spot = false;
        var bill = false;
        var ring = StorePackage.IsRoomShellMethod(method);
        foreach (var action in plan.Actions)
        {
            if (ButcherFacilityWork(method, action))
            {
                spot = true;
            }
            else if (action.Kind == ActionKind.ProductionBill)
            {
                bill = true;
            }
            else
            {
                return false;
            }
        }
        if (spot == bill)
        {
            return false;
        }
        foreach (var m in goal.Methods)
        {
            foreach (var progress in LoadIn(tx, m.Plan).Progress)
            {
                var own = (spot && ButcherFacilityWork(m.MethodId, progress.Action) && StorePackage.IsRoomShellMethod(m.MethodId) == ring) || (bill && progress.Action.Kind == ActionKind.ProductionBill);
                if (progress.Action.ProductionBill is { Mode: BillMode.ButcherForever } && plan.Actions.Any(a => a.ProductionBill is { Mode: BillMode.HumanButcherForever }))
                {
                    own = false;
                }
                if (own && Standard.WorkOpen([progress]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>A standing refill bill must not delay access to food in an emergency.</summary>
    private static bool ReserveAccessOpenWorkExempt(StoreTransaction tx, StandardState goal, PlanSpec plan)
    {
        var review = LoadRounds(tx);
        var bound = review.Standards.Any(b => b.Standard == goal.Standard.Id && b.Concern == MaintainFoodStorage);
        if (!bound || review.ReserveSupplies.Count == 0 || plan.Actions.Count == 0)
        {
            return false;
        }
        foreach (var action in plan.Actions)
        {
            if (action.SupplyAllow is not { } supply)
            {
                return false;
            }
            if (!review.ReserveSupplies.Any(row => row.Thing == supply.Thing && row.Definition == supply.Definition && row.Forbid == supply.Forbidden))
            {
                return false;
            }
        }
        foreach (var method in goal.Methods)
        {
            foreach (var progress in LoadIn(tx, method.Plan).Progress)
            {
                if (progress.Action.Kind != ActionKind.ProductionBill && Standard.WorkOpen([progress]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>Independent pawn dressing may share a review; a bill remains exclusive.</summary>
    private static bool GearOpenWorkExempt(StoreTransaction tx, StandardState goal, PlanSpec plan)
    {
        var review = LoadRounds(tx);
        if (!review.Standards.Any(b => b.Standard == goal.Standard.Id && b.Concern == MaintainEquipment))
        {
            return false;
        }
        var pawns = new HashSet<PawnId>();
        var items = new HashSet<string>(StringComparer.Ordinal);

        bool Add(PlanSpec spec)
        {
            foreach (var a in spec.Actions)
            {
                PawnId pawn;
                if (a.GearReplace is { } gear)
                {
                    pawn = gear.Pawn;
                    if (!items.Add(gear.Thing))
                    {
                        return false;
                    }
                }
                else if (a.ApparelPolicy is { } apparel)
                {
                    pawn = apparel.Pawn;
                }
                else
                {
                    return false;
                }
                if (!pawns.Add(pawn))
                {
                    return false;
                }
            }
            return true;
        }

        if (!Add(plan))
        {
            return false;
        }
        // No slot limit: open work only has to stay off the pawns and items this plan touches.
        foreach (var m in goal.Methods)
        {
            var p = LoadIn(tx, m.Plan);
            if (Standard.WorkOpen(p.Progress) && !Add(p.Spec))
            {
                return false;
            }
        }
        return true;
    }

    private static bool CraftingSpotAction(DomainAction action) =>
        action.Building is { } building
            ? building.Definition == CraftingSpotDefinition
            : action.Deconstruction is { } cut && cut.Definition == CraftingSpotDefinition;

    /// <summary>
    /// Lets MaintainEquipment's crafting spot (its placement in the planned workshop, its deconstruction) be
    /// committed beside the goal's bills and dressing. A plan made only of spot actions is blocked only by an open
    /// spot action.
    /// </summary>
    private static bool CraftingSpotOpenWorkExempt(StoreTransaction tx, StandardState goal, PlanSpec plan)
    {
        if (plan.Actions.Count == 0 || !plan.Actions.All(CraftingSpotAction))
        {
            return false;
        }
        var review = LoadRounds(tx);
        if (!review.Standards.Any(b => b.Standard == goal.Standard.Id && b.Concern == MaintainEquipment))
        {
            return false;
        }
        foreach (var m in goal.Methods)
        {
            foreach (var progress in LoadIn(tx, m.Plan).Progress)
            {
                if (CraftingSpotAction(progress.Action) && Standard.WorkOpen([progress]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static readonly HashSet<string> ShelterBunkMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "shelter-spots",
        "shelter-bedrolls",
        "shelter-beds",
        "shelter-clear-bedrolls",
        "shelter-clear-beds",
    };

    /// <summary>
    /// Admits a method under the initial shelter while its only open work is bunk rungs: the ring is sited around
    /// the spots and beds, so walls and door proceed while a bed stalls. The new method must be pure construction
    /// on no cell an open bunk stands on; any other open method (the shell itself) still holds the goal.
    /// </summary>
    private static bool ShelterOpenWorkExempt(StoreTransaction tx, StandardState goal, MethodId method, PlanSpec plan)
    {
        var review = LoadRounds(tx);
        if (!review.Standards.Any(b => b.Standard == goal.Standard.Id && b.Concern == MaintainHousing) || plan.Actions.Count == 0)
        {
            return false;
        }
        var taken = new HashSet<Cell>();
        foreach (var m in goal.Methods)
        {
            var p = LoadIn(tx, m.Plan);
            if (!StorePackage.PlanOpen(p))
            {
                continue;
            }
            var ring = StorePackage.IsRoomShellMethod(m.MethodId);
            if (ring && StorePackage.IsRoomShellMethod(method))
            {
                return false; // one ring wave at a time
            }
            if (!ShelterBunkMethods.Contains(m.MethodId.Value) && !ring)
            {
                return false;
            }
            foreach (var a in p.Spec.Actions)
            {
                if (a.Building is { } b)
                {
                    if (ring)
                    {
                        _ = taken.Add(b.Cell);
                        continue;
                    }
                    foreach (var c in BunkCells(b.Cell, b.Rotation))
                    {
                        _ = taken.Add(c);
                    }
                }
            }
        }
        // The shelter room is one continuous reconcile: the ring's open wave and the bunk rungs (placements and the
        // clearing deconstructions) run together on disjoint cells.
        foreach (var a in plan.Actions)
        {
            if (a.Building is { } b)
            {
                if (taken.Contains(b.Cell))
                {
                    return false;
                }
                continue;
            }
            if (a.Deconstruction is { } d)
            {
                if (taken.Contains(d.Cell))
                {
                    return false;
                }
                continue;
            }
            if (a.UninstallBuilding is not { } u || taken.Contains(u.Cell))
            {
                return false;
            }
        }
        return true;
    }

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
