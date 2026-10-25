using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>Family permissions and supported Method shapes, shared by proposal checks and journal admission.
/// Evidence is supplied by the caller; successful checks never authorize a later commit.</summary>
public static class FamilyAdmissionRules
{
    /// <summary>Bounds one batched bill plan to a round's reconcile.</summary>
    public const int MaxBillPlanActions = 64;
    private const int MaxStockpileCreates = 16;
    private static readonly HashSet<ActionKind> SettingsKinds = [ActionKind.PawnSettings, ActionKind.ReadingPolicy, ActionKind.DrugPolicy, ActionKind.FoodPolicy];
    private static string OwnerLabel(NeedId need) => "need " + need.Value;

    /// <summary>An unsupported use is a normal refusal, rather than a malformed-plan conflict.</summary>
    public static string? UnsupportedReason(NeedId need, PlanSpec plan)
    {
        // Self-treatment is an observed permission, not a local assignment winner.
        if (need == AdminFamily.WorkAssignments
            && plan.Actions.Any(a => a.PawnSettings is { Kind: SettingKind.SelfTend }))
            return "unsupported_assignment_self_tend_policy";
        // A people/mood Need casts nothing: a preventative cast needs shared caster time and psyfocus (#3304).
        if (PeopleFamily.IsMood(need) && plan.Actions.Any(a => a.Kind == ActionKind.Ability))
            return "unsupported_forecast_mood_cast";
        // A window answer is the people/dialog Need's alone (#3304); another Need answers only its own letters.
        if (need != PeopleFamily.Dialog
            && plan.Actions.Any(a => a.DialogAnswer is { LetterToken.Length: 0 }))
            return "dialog_answer_not_owned";
        return null;
    }

    /// <summary>Checks bill ownership, batch bounds and the reconciled plan shape.</summary>
    public static string? BillConflict(NeedId need, PlanSpec plan, bool currentReview)
    {
        if (!plan.Actions.Any(a => a.Kind == ActionKind.ProductionBill))
        {
            return null;
        }

        if (!currentReview || plan.Actions.Count > MaxBillPlanActions)
        {
            return $"bill method needs a current autopilot review and at most {MaxBillPlanActions} actions";
        }
        // Bills are the admin/work-ledger Need's: it commits the round's reconcile plan (the economy/resource Need's
        // stock bills, the medical Needs' bills and the biotech/mechs Need's gestation bill among them): places and
        // removals together.
        var bound = need == AdminFamily.WorkLedger;
        if (!bound)
        {
            return $"{OwnerLabel(need)} does not admit production bills";
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
                return "bill method mixes action kinds or repeats a bench and recipe";
            }
        }
        return null;
    }

    /// <summary>Checks zone ownership, homogeneous kinds, batch bounds and distinct cells.</summary>
    public static string? ZoneConflict(NeedId need, PlanSpec plan, bool currentReview)
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
            return null;
        }

        // A growing-field or fishing method places a bounded batch (the food/supply Need), a royal decree's harvest
        // field (the joiners/decrees Need, #3302) or the hay, social and resource crops (the economy/resource Need).
        // A stockpile zone is admitted for the admin/stockpiles Need alone, which applies every store the departments
        // declare in one batch (decision 20).
        var limit = 32;
        if (!currentReview)
        {
            return $"{OwnerLabel(need)}: zone method needs a current autopilot review";
        }
        var bound = false;
        var resource = false;
        var held = need;
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
            return $"{OwnerLabel(need)}: zone method not admitted for this owner or over {limit} actions";
        }
        var cells = new HashSet<Cell>();
        foreach (var action in plan.Actions)
        {
            if (action.ZoneCreate is not { } zone || stockpile != (zone.Kind == ZoneKind.Stockpile))
            {
                return $"zone method mixes action kinds at {action.Id.Value}";
            }
            // Any resource crop may be sown; the planner holds the social crops to their brewing gate and ceiling.
            if (resource && zone.Kind != ZoneKind.Growing)
            {
                return $"economy/resource zone {action.Id.Value} is not a growing zone";
            }
            foreach (var cell in zone.Cells)
            {
                if (!cells.Add(cell))
                {
                    return $"zone method repeats cell {cell} at {action.Id.Value}";
                }
            }
        }
        return null;
    }

    /// <summary>Checks acquisition ownership, batch bounds and distinct sources.</summary>
    public static string? AcquisitionConflict(NeedId need, PlanSpec plan, bool currentReview)
    {
        if (!plan.Actions.Any(a => a.Kind == ActionKind.Acquisition))
        {
            return null;
        }

        if (!currentReview || plan.Actions.Count > AcquisitionCatalogRules.MaxCatalogSelection + PawnRules.MaxHuntRows)
        {
            return $"{OwnerLabel(need)}: acquisition method needs a current autopilot review and at most {AcquisitionCatalogRules.MaxCatalogSelection + PawnRules.MaxHuntRows} actions";
        }
        // The military pests Need hunts its pests (#3259), the food/supply Need forages and hunts (#3254) and the
        // joiners/decrees Need hunts a royal decree's animals (#3302), the economy/resource Need chops, forages and
        // hunts toward its floors (#3303) and the medical/reserves Need harvests healroot (#3258).
        var held = need;
        var bound = (held == MilitaryFamily.Pests || held == FoodRules.FoodSupplyNeed || held == JoinersFamily.Decrees || held == EconomyFamily.Resource
                || held == MedicalRules.MedicalReservesNeed);
        if (!bound)
        {
            return $"{OwnerLabel(need)} does not admit acquisitions";
        }
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in plan.Actions)
        {
            if (action.Acquisition is not { } w || !sources.Add(w.Thing))
            {
                return $"acquisition method mixes action kinds or repeats a source at {action.Id.Value}";
            }
        }
        return null;
    }

    /// <summary>Checks assignment ownership, companion settings and isolation-only moves.</summary>
    public static string? WorkConflict(NeedId need, PlanSpec plan, bool currentReview)
    {
        if (!plan.Actions.Any(a => a.Kind == ActionKind.WorkAssignment))
        {
            return null;
        }

        if (!currentReview)
        {
            return $"{OwnerLabel(need)}: work method needs a current autopilot review";
        }
        var bound = false;
        // A creepjoiner's isolation (the admin/creep-joiners Need) and a sheltering move (disaster/sheltering) are
        // area moves: each assignment is area-only.
        var isolation = false;
        var held = need;
        bound = held == AdminFamily.WorkAssignments;
        isolation = held == AdminFamily.CreepJoiners || held == DisasterFamily.Sheltering;
        if (!bound && !isolation)
        {
            return $"{OwnerLabel(need)} does not admit work assignments";
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
                return $"work method carries a {Wire.Name(action.Kind)} action, repeats a pawn or exceeds 8 pawns at {action.Id.Value}";
            }
            if (isolation && (!w.HasArea || w.HasSchedule || w.Settings.Count != 0))
            {
                return $"{OwnerLabel(need)}: assignment {action.Id.Value} is not area-only";
            }
            _ = pawns.Add(w.Pawn);
        }
        return null;
    }
    /// <summary>
    /// Admits only stacks the review's cohort still lists, each at the cell the census last reported: the event loot
    /// for the admin/supply-safety Need, the larder corpse and reserve stacks for the food-stores/storage Need, and the
    /// forbidden wastepacks for the biotech/pollution Need, whose Method also carries its clear-area edit and hauls.
    /// </summary>
    public static string? SupplyConflict(NeedId need, PlanSpec plan, bool currentReview, IReadOnlyList<StartingSupply> larder, IReadOnlyList<ReserveSupply> reserves, IReadOnlyList<StartingSupply> eventLoot, IReadOnlyList<StartingSupply> pollutionSupplies)
    {
        if (!plan.Actions.Any(a => a.Kind is ActionKind.SupplyAllow or ActionKind.SupplyForbid))
        {
            return null;
        }

        if (!currentReview)
        {
            return $"{OwnerLabel(need)}: supply method needs a current autopilot review";
        }
        var bound = false;
        var reserve = new Dictionary<string, ReserveSupply>(StringComparer.Ordinal);
        IReadOnlyList<StartingSupply> cohort = [];
        var held = need;
        if (held == FoodStoresRules.FoodStorageNeed)
        {
            bound = true;
            cohort = larder;
            foreach (var row in reserves)
            {
                reserve[row.Thing] = row;
            }
        }
        else if (held == AdminFamily.SupplySafety)
        {
            bound = true;
            cohort = eventLoot;
        }
        var pollution = held == BiotechFamily.Pollution;
        if (pollution)
        {
            bound = true;
            cohort = pollutionSupplies;
        }
        if (!bound)
        {
            return $"{OwnerLabel(need)} does not admit supply batches";
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
                return $"supply method mixes action kinds at {action.Id.Value}";
            }
            var listed = pending.TryGetValue(supply.Thing, out var row);
            if (reserve.TryGetValue(supply.Thing, out var entry) && entry.Definition == supply.Definition && entry.Forbid == supply.Forbidden)
            {
                continue;
            }
            if (!listed || row!.Definition != supply.Definition || row.Cell != supply.Cell || row.Forbid != supply.Forbidden)
            {
                return $"supply {supply.Thing} at {action.Id.Value} is not the review's cohort row";
            }
        }
        return null;
    }
}
